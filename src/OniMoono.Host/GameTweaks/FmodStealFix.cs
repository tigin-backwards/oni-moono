#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `fmod-steal-fix` (on by default): patches, in memory, the write to a freed event
    /// instance in FMOD 2.03.12's `EventInstance::changeStateStopped` (FmodStealPatch has what and
    /// why; docs/TWEAKS.md, "The FMOD steal fix"; issues #78, #72, #73 and #58).
    ///
    /// A postfix on FMODUnity's `RuntimeManager.Initialize`, once FMOD Studio is up (its Studio
    /// system valid: Initialize returns its first error even when a retry with no sound output
    /// then succeeded) and before the game has made any event instance (no thread can be in the
    /// patched code): FMOD's own version (the core system's getVersion), the loaded library's
    /// file (its SHA-256, one of FmodStealPatch.Known's) and the bytes expected must all be the
    /// ones it was written for, and on Windows the system's unwind lookup must find the
    /// function's own entry for the helper call's return address; any difference (a game update
    /// shipping another FMOD, a modified file), and nothing is written, with one line saying
    /// what did not match. fmod-quarantine stays the only guard then. A postfix that runs the
    /// method as it is: no fingerprint.
    ///
    /// On Windows the cave counts each store it skipped (`Skipped`, and the line at quit); on
    /// Linux the patch is a jump alone and counts nothing.
    [GameMember(GameUse.Postfix, "FMOD.RESULT FMODUnity.RuntimeManager::Initialize()")]
    [GameMember(GameUse.Uses, "FMOD.System FMODUnity.RuntimeManager::coreSystem")]
    [GameMember(GameUse.Uses, "FMOD.Studio.System FMODUnity.RuntimeManager::studioSystem")]
    internal static unsafe class FmodStealFix
    {
        public const string Name = GameTweakNames.FmodStealFix;

        static byte* counter;

        /// For specs: the patch is in; the stores to freed instances it skipped (Windows).
        public static bool Applied { get; private set; }
        public static long Skipped => counter == null ? 0 : System.Threading.Volatile.Read(ref *(int*)counter);

        public static void Install(Harmony harmony)
        {
            var initialize = AccessTools.DeclaredMethod(typeof(FMODUnity.RuntimeManager), "Initialize", Type.EmptyTypes)
                ?? throw new MissingMethodException("FMODUnity.RuntimeManager", "Initialize");
            harmony.Patch(initialize, postfix: new HarmonyMethod(typeof(FmodStealFix), nameof(Initialized)));
        }

        static void Initialized(FMODUnity.RuntimeManager __instance, FMOD.RESULT __result)
        {
            if (Applied) return;
            try
            {
                var studio = (FMOD.Studio.System)AccessTools.Field(typeof(FMODUnity.RuntimeManager), "studioSystem").GetValue(__instance)!;
                if (!studio.isValid())
                {
                    EngineLog.Engine($"{Name}: not applied, FMOD Studio did not start ({__result})");
                    return;
                }
                Apply(__instance);
            }
            catch (Exception e)
            {
                EngineLog.Engine($"{Name}: not applied, {e.GetType().Name}: {e.Message}");
            }
        }

        static void Apply(FMODUnity.RuntimeManager manager)
        {
            var core = (FMOD.System)AccessTools.Field(typeof(FMODUnity.RuntimeManager), "coreSystem").GetValue(manager)!;
            if (core.getVersion(out uint version, out uint build) != FMOD.RESULT.OK)
            {
                EngineLog.Engine($"{Name}: not applied, FMOD did not say its version");
                return;
            }
            if (!Locate(out byte* image, out string? path) || path == null)
            {
                EngineLog.Engine($"{Name}: not applied, FMOD Studio's library was not found in the process");
                return;
            }
            string platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other";
            string sha;
            using (var file = File.OpenRead(path))
                sha = Convert.ToHexStringLower(SHA256.HashData(file));
            var module = FmodStealPatch.Find(platform, Path.GetFileName(path), sha, out string? why);
            why ??= FmodStealPatch.Check(module!, version, build, (offset, length) => new ReadOnlySpan<byte>(image + offset, length).ToArray());
            if (why != null)
            {
                EngineLog.Engine($"{Name}: not applied, {why}; FMOD runs as shipped");
                return;
            }
            // Windows: the system's unwinder, from the original return address of the helper's
            // call, then (patched) from the new one, must reach the same caller on the same stack
            // with the same registers restored.
            bool probe = module!.CaveCode != null && OperatingSystem.IsWindows();
            var before = probe ? Unwind(image, module.OldReturn) : null;
            var original = FmodStealPatch.Writes(module).Select(w => (w.Offset, Bytes: new ReadOnlySpan<byte>(image + w.Offset, w.Bytes.Length).ToArray())).ToArray();
            // From the first write on, every outcome is logged as what it leaves in memory.
            int written = 0;
            string? undo = null;
            try
            {
                foreach (var (offset, bytes) in FmodStealPatch.Writes(module))
                {
                    written++;
                    Write(image + offset, bytes);
                }
                if (probe)
                {
                    var after = Unwind(image, module.Return);
                    if (before == null || after == null || before[0] == 0 || !after.AsSpan().SequenceEqual(before))
                        undo = $"unwinding from +0x{module.Return:X} (0x{after?[0]:x} at +{(long)(after?[1] ?? 0)}) does not match unwinding from +0x{module.OldReturn:X} (0x{before?[0]:x} at +{(long)(before?[1] ?? 0)}) in its caller, stack depth and restored registers";
                }
            }
            catch (Exception e)
            {
                undo = $"{e.GetType().Name} while patching: {e.Message}";
            }
            if (undo != null)
            {
                try
                {
                    foreach (var (offset, bytes) in original.Take(written).Reverse())
                        Write(image + offset, bytes);
                    EngineLog.Engine($"{Name}: not applied, {undo}; the original bytes are back, FMOD runs as shipped");
                }
                catch (Exception e)
                {
                    // The patch's bytes stay in: say so, and count what it skips.
                    if (module.CaveCode != null) counter = image + module.Counter;
                    Applied = true;
                    EngineLog.Engine($"{Name}: patched, but {undo}, and putting the original bytes back failed ({e.GetType().Name}: {e.Message}); the patch stays in");
                }
                return;
            }
            if (module.CaveCode != null) counter = image + module.Counter;
            Applied = true;
            EngineLog.Engine($"{Name}: FMOD 2.03.12's store to a stopped instance it has just freed is skipped ({Path.GetFileName(path)} of {module.Game}, at +0x{module.Site:X})");
        }

        /// The library's base address and file: on Windows the loaded module by name, on Linux
        /// the mapping of offset 0 in /proc/self/maps.
        static bool Locate(out byte* image, out string? path)
        {
            image = null;
            path = null;
            if (OperatingSystem.IsWindows())
            {
                foreach (var name in new[] { "fmodstudio.dll", "fmodstudioL.dll" })
                {
                    var module = GetModuleHandleW(name);
                    if (module == IntPtr.Zero) continue;
                    var buffer = new char[1024];
                    uint length = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
                    if (length == 0 || length >= buffer.Length) return false;
                    image = (byte*)module;
                    path = new string(buffer, 0, (int)length);
                    return true;
                }
                return false;
            }
            if (!OperatingSystem.IsLinux()) return false;
            var found = FmodStealPatch.FromMaps(File.ReadLines("/proc/self/maps"));
            if (found == null) return false;
            image = (byte*)found.Value.Base;
            path = found.Value.Path;
            return true;
        }

        /// Writes `bytes` over code: the pages made writable for the copy, then given their
        /// protection back.
        static void Write(byte* at, byte[] bytes)
        {
            if (OperatingSystem.IsWindows())
            {
                if (!VirtualProtect(at, (nuint)bytes.Length, 0x40 /* PAGE_EXECUTE_READWRITE */, out uint old))
                    throw new InvalidOperationException($"VirtualProtect failed ({Marshal.GetLastPInvokeError()})");
                bytes.CopyTo(new Span<byte>(at, bytes.Length));
                VirtualProtect(at, (nuint)bytes.Length, old, out _);
                FlushInstructionCache(GetCurrentProcess(), at, (nuint)bytes.Length);
                return;
            }
            nuint page = (nuint)at & ~(nuint)0xFFF;
            nuint span = ((nuint)at + (nuint)bytes.Length - page + 0xFFF) & ~(nuint)0xFFF;
            if (mprotect((void*)page, span, 7 /* PROT_READ | PROT_WRITE | PROT_EXEC */) != 0)
                throw new InvalidOperationException($"mprotect failed ({Marshal.GetLastPInvokeError()})");
            bytes.CopyTo(new Span<byte>(at, bytes.Length));
            mprotect((void*)page, span, 5 /* PROT_READ | PROT_EXEC */);
        }

        /// Windows: what the system's unwinder makes of a frame stopped at `rva` (a return address
        /// in the patched function) on a synthetic stack whose slots hold distinct values: the
        /// caller's address it reads, how far up the stack it is, and the nonvolatile registers
        /// it restores (rbx, rbp, rsi, rdi, r12-r15). Null when the address has no unwind entry
        /// in the module.
        static ulong[]? Unwind(byte* image, int rva)
        {
            ulong pc = (ulong)(image + rva);
            var entry = RtlLookupFunctionEntry(pc, out ulong imageBase, IntPtr.Zero);
            if (entry == null || imageBase != (ulong)image) return null;
            const int Slots = 256;
            var stack = (ulong*)NativeMemory.AlignedAlloc(Slots * 8, 16);
            var context = (byte*)NativeMemory.AlignedAlloc(ContextSize, 16);
            try
            {
                for (int i = 0; i < Slots; i++) stack[i] = 0x5A5A000000000000UL + (ulong)i;
                new Span<byte>(context, ContextSize).Clear();
                *(ulong*)(context + ContextRsp) = (ulong)stack;
                *(ulong*)(context + ContextRip) = pc;
                RtlVirtualUnwind(0, imageBase, pc, entry, context, out _, out _, IntPtr.Zero);
                var state = new ulong[2 + ContextNonvolatile.Length];
                state[0] = *(ulong*)(context + ContextRip);
                state[1] = *(ulong*)(context + ContextRsp) - (ulong)stack;
                for (int i = 0; i < ContextNonvolatile.Length; i++) state[2 + i] = *(ulong*)(context + ContextNonvolatile[i]);
                return state;
            }
            finally
            {
                NativeMemory.AlignedFree(stack);
                NativeMemory.AlignedFree(context);
            }
        }

        /// x64 CONTEXT: its size, where Rsp and Rip are in it, and rbx, rbp, rsi, rdi, r12-r15.
        const int ContextSize = 0x4D0, ContextRsp = 0x98, ContextRip = 0xF8;
        static readonly int[] ContextNonvolatile = [0x90, 0xA0, 0xA8, 0xB0, 0xD8, 0xE0, 0xE8, 0xF0];

        /// The end of the run: what the cave skipped (Windows).
        public static void Shutdown()
        {
            if (Applied && counter != null) EngineLog.Engine($"{Name}: {Skipped} store(s) to a freed event instance skipped");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] buffer, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualProtect(void* address, nuint size, uint protect, out uint old);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool FlushInstructionCache(IntPtr process, void* address, nuint size);

        /// The function's RUNTIME_FUNCTION (begin, end, unwind data offsets) for a code address.
        [DllImport("kernel32.dll")]
        static extern uint* RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr history);

        [DllImport("kernel32.dll")]
        static extern IntPtr RtlVirtualUnwind(uint handlerType, ulong imageBase, ulong controlPc, uint* functionEntry, byte* context, out IntPtr handlerData, out ulong establisherFrame, IntPtr contextPointers);

        [DllImport("libc", SetLastError = true)]
        static extern int mprotect(void* address, nuint length, int protection);
    }
}
