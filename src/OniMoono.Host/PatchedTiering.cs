#nullable enable
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// Keeps a method Harmony patches before its first compile off tiered compilation, so it is
    /// compiled once, optimised, and never again (issue #127; docs/research/detours-across-tier-up.md).
    ///
    /// MonoMod detours a patched method by writing a jump into its code. Each time tiered
    /// compilation recompiles the method, MonoMod writes the jump again into the new code
    /// (ManagedDetourBox.OnMethodCompiled), and that move can go wrong: it returns at once while
    /// another thread applies or undoes the detour, an exception in an earlier handler ends it, and
    /// code shorter than any jump it knows (an empty method's tier-1 `ret`) gets a 14-byte jump
    /// written past its end. The method's own body never runs behind the jump, so its recompiles
    /// buy nothing.
    ///
    /// Before MonoMod compiles the method it detours (PlatformTripleDetourFactory.CreateDetour, which
    /// then applies the detour), the host clears the MethodDesc's IsEligibleForTieredCompilation
    /// flag when the method has no native code yet. CoreCLR 10 (src/coreclr/vm):
    /// - The flag is set once per chunk (MethodDescChunk::DetermineAndSetIsEligibleForTieredCompilation),
    ///   for a type's methods when MethodTableBuilder builds it, before reflection can name them.
    /// - Call counting starts only once the method has code: CodeVersionManager::
    ///   PublishVersionableCodeIfNecessary compiles the default code version first and then
    ///   publishes it through CallCountingManager::SetCodeEntryPoint, which installs the
    ///   call-counting stub and checks the flag again first. With no native code there is no stub.
    /// - With the flag clear, the prestub takes the path of a method never eligible (as one with
    ///   AggressiveOptimization): DoPrestub's IsVersionable() is false, PrepareILBasedCode compiles
    ///   it with shouldTier false, and TieredCompilationManager::GetJitFlags gives no tier-0 flags.
    ///
    /// A method compiled before its patch (a late patch, a mod's patch of a method already run) keeps
    /// tiering, and MonoMod's move with it (MonoModLog logs what goes wrong there). So do stubs and
    /// generic code (wrapper and instantiated MethodDescs), whose moves UnboxedMethods handles.
    /// A first call racing the patch on another thread may already be compiling tier-0 code; the
    /// flag is set back when code appears in that window, and otherwise that code is final at
    /// tier 0 (CallCountingManager::SetCodeEntryPoint sees the flag clear).
    ///
    /// The layout is CoreCLR 10's, checked at start-up on an emitted probe; off, with a log line,
    /// when it differs, and with ONI_PATCHED_TIERING=0 (to compare). Cost: one flag write per
    /// patch; nothing at run time.
    internal static unsafe class PatchedTiering
    {
        // CoreCLR 10's MethodDesc (method.hpp): UINT16 m_wFlags3AndTokenRemainder at 0, whose
        // enum_flag3_IsEligibleForTieredCompilation is 0x8000; WORD m_wFlags at 6 (mdfClassification
        // 0x7, mcIL 0, mdfHasNonVtableSlot 0x8, mdfMethodImpl 0x10, mdfHasNativeCodeSlot 0x20).
        // sizeof(MethodDesc) is 16 (with m_codeData); the native code slot follows the optional
        // non-vtable slot (8) and MethodImpl (two pointers), as GetAddrOfNativeCodeSlot reads it.
        const int Flags3 = 0, Flags = 6, MethodDescSize = 16, NonVtableSlotSize = 8, MethodImplSize = 16;
        const int EligibleForTiering = 0x8000;
        const ushort ClassificationMask = 0x0007, IL = 0, HasNonVtableSlot = 0x0008, MethodImpl = 0x0010, HasNativeCodeSlot = 0x0020;

        /// Where lines go (EngineLog.Engine; tests replace it).
        internal static Action<string> Sink = EngineLog.Engine;

        static bool installed;
        static MethodInfo? requestSource;

        /// Whether patched methods are kept off tier-up (the layout check passed).
        internal static bool Available { get; private set; }

        /// Patched methods kept off tier-up; patched methods compiled before their patch, which keep
        /// tiering; patched methods not tiered anyway (stubs, generic code, optimised or debug code).
        internal static int kept, compiledFirst, notTiered;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            if (Environment.GetEnvironmentVariable("ONI_PATCHED_TIERING") == "0")
            {
                Sink("patched tiering: off (ONI_PATCHED_TIERING=0), patched methods tier up as before");
                return;
            }
            string? why;
            try
            {
                why = CheckLayout();
            }
            catch (Exception e)
            {
                why = $"{e.GetType().Name}: {e.Message}";
            }
            if (why != null)
            {
                Sink($"patched tiering: off, patched methods tier up as before ({why})");
                return;
            }
            var harmony = typeof(Harmony).Assembly;
            var factory = harmony.GetType("MonoMod.Core.Platforms.PlatformTripleDetourFactory", true)!;
            var create = AccessTools.Method(factory, "CreateDetour") ?? throw new MissingMethodException(factory.FullName, "CreateDetour");
            var request = harmony.GetType("MonoMod.Core.CreateDetourRequest", true)!;
            requestSource = AccessTools.PropertyGetter(request, "Source") ?? throw new MissingMethodException(request.FullName, "get_Source");
            new Harmony("OniMoono.Host.PatchedTiering").Patch(create, prefix: new HarmonyMethod(typeof(PatchedTiering), nameof(Creating)));
            Available = true;
        }

        /// Prefix of PlatformTripleDetourFactory.CreateDetour(CreateDetourRequest): before MonoMod
        /// compiles and detours the request's source.
        static void Creating(object[] __args)
        {
            try
            {
                if (requestSource!.Invoke(__args[0], null) is MethodBase source) Keep(source);
            }
            catch (Exception)
            {
                // Never stand in the detour's way.
            }
        }

        /// Clears `method`'s eligibility for tiered compilation when it has no code yet; what it did.
        internal static Outcome Keep(MethodBase method)
        {
            if (method is DynamicMethod) return Count(Outcome.NotTiered);
            IntPtr handle;
            try
            {
                handle = method.MethodHandle.Value;
            }
            catch (Exception) // no handle (a dynamic method's RTDynamicMethod)
            {
                return Count(Outcome.NotTiered);
            }
            byte* p = (byte*)handle;
            if (!Tiered(p)) return Count(Outcome.NotTiered);
            if (NativeCode(p) != IntPtr.Zero) return Count(Outcome.CompiledFirst);
            Update(p, EligibleForTiering, set: false);
            // A first call on another thread compiled it meanwhile: it tiers, as compiled first.
            if (NativeCode(p) != IntPtr.Zero)
            {
                Update(p, EligibleForTiering, set: true);
                return Count(Outcome.CompiledFirst);
            }
            return Count(Outcome.Kept);
        }

        internal enum Outcome { Kept, CompiledFirst, NotTiered }

        static Outcome Count(Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.Kept: Interlocked.Increment(ref kept); break;
                case Outcome.CompiledFirst: Interlocked.Increment(ref compiledFirst); break;
                default: Interlocked.Increment(ref notTiered); break;
            }
            return outcome;
        }

        /// A line for Player.log: what the session's patches did.
        internal static string Summary() => Available
            ? $"patched tiering: {kept:N0} patched method(s) kept off tier-up; {compiledFirst:N0} compiled before their patch keep tiering; {notTiered:N0} not tiered anyway"
            : "patched tiering: off";

        /// An IL method eligible for tiered compilation, with a native code slot.
        static bool Tiered(byte* p)
        {
            ushort flags = *(ushort*)(p + Flags);
            return (Volatile.Read(ref *(ushort*)(p + Flags3)) & EligibleForTiering) != 0
                && (flags & ClassificationMask) == IL && (flags & HasNativeCodeSlot) != 0;
        }

        /// MethodDesc::GetNativeCode for an IL method with a native code slot: its default code
        /// version's code, or zero before its first compile.
        static IntPtr NativeCode(byte* p)
        {
            ushort flags = *(ushort*)(p + Flags);
            int offset = MethodDescSize + ((flags & HasNonVtableSlot) != 0 ? NonVtableSlotSize : 0) + ((flags & MethodImpl) != 0 ? MethodImplSize : 0);
            return Volatile.Read(ref *(IntPtr*)(p + offset));
        }

        /// Whether `method` is eligible for tiered compilation (tests).
        internal static bool Eligible(MethodBase method) => Tiered((byte*)method.MethodHandle.Value);

        /// m_wFlags3AndTokenRemainder is the low half of the aligned 32-bit word at offset 0
        /// (little-endian); the runtime updates it with interlocked operations on that word
        /// (MethodDesc::InterlockedUpdateFlags3).
        static void Update(byte* methodDesc, int flag, bool set)
        {
            ref int word = ref *(int*)(methodDesc + Flags3);
            int seen, wanted;
            do
            {
                seen = Volatile.Read(ref word);
                wanted = set ? seen | flag : seen & ~flag;
            }
            while (seen != wanted && Interlocked.CompareExchange(ref word, wanted, seen) != seen);
        }

        /// Reads the flags of an emitted type's methods, never called, and of ones compiled: why the
        /// layout is not CoreCLR 10's, or null. Tiering off (DOTNET_TieredCompilation=0) shows as no
        /// method eligible: then there is nothing to keep off anyway.
        static string? CheckLayout()
        {
            if (!BitConverter.IsLittleEndian || IntPtr.Size != 8) return "not a 64-bit little-endian process";
            var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OniMoono.Host.PatchedTieringProbe"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("PatchedTieringProbe");
            var type = module.DefineType("Probe", TypeAttributes.Public);
            // Static methods have a non-vtable slot, virtual ones none.
            foreach (var name in new[] { "StaticA", "StaticB", "VirtualA", "VirtualB" })
            {
                bool isStatic = name.StartsWith("Static", StringComparison.Ordinal);
                var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.HideBySig | (isStatic ? MethodAttributes.Static : MethodAttributes.Virtual),
                    typeof(int), [typeof(int)]);
                var il = method.GetILGenerator();
                il.Emit(isStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_3);
                il.Emit(OpCodes.Mul);
                il.Emit(OpCodes.Ret);
            }
            type.DefineDefaultConstructor(MethodAttributes.Public);
            var probe = type.CreateType();
            foreach (var pair in new[] { ("StaticA", "StaticB"), ("VirtualA", "VirtualB") })
            {
                var uncalled = probe.GetMethod(pair.Item1)!;
                var compiled = probe.GetMethod(pair.Item2)!;
                // Their temporary entry points: PrepareMethod compiles a virtual method only once it
                // has one (MethodDesc::IsPointingToPrestub), as MonoMod's detour gives it one.
                uncalled.MethodHandle.GetFunctionPointer();
                compiled.MethodHandle.GetFunctionPointer();
                byte* u = (byte*)uncalled.MethodHandle.Value, c = (byte*)compiled.MethodHandle.Value;
                if (!Tiered(u) || !Tiered(c))
                    return $"{pair.Item1}'s MethodDesc flags are 0x{*(ushort*)(u + Flags3):x4} 0x{*(ushort*)(u + Flags):x4}, not an IL method eligible for tiering";
                if (NativeCode(u) != IntPtr.Zero || NativeCode(c) != IntPtr.Zero) return $"{pair.Item1} has native code before any call";
                RuntimeHelpers.PrepareMethod(compiled.MethodHandle);
                if (NativeCode(c) == IntPtr.Zero) return $"{pair.Item2} has no native code once compiled";
                if (NativeCode(u) != IntPtr.Zero || !Tiered(c)) return $"{pair.Item2}'s compile changed what was read";
            }
            return null;
        }
    }
}
