using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// ARCHITECTURE D34. CoreCLR's JIT inlines small methods, and virtual ones it devirtualizes
    /// (guarded by the receiver's type, from tiered PGO), into the code it compiles at tier 1. A
    /// method patched later is marked not to be inlined from then on, but code compiled before
    /// keeps its old body, so the patch never runs from there: a prefix on Brain.UpdateBrain added
    /// a minute into a colony never ran, as the brain groups' tiered-up loop had inlined it. Mono
    /// inlines only methods under 20 bytes of IL, and no overridable one, so in the original a late
    /// patch of a virtual method, or of any but the smallest methods, runs from every caller.
    ///
    /// The host records, for every method the JIT compiles, the methods it inlined, from the
    /// JIT's own report of each inlining decision (ICorJitInfo::reportInliningDecision, through
    /// the ICorJitInfo wrapper Harmony's MonoMod hands the JIT). When a method is patched the first
    /// time, PatchDependents rebuilds the code that inlined it: a replacement Harmony made (rebuilt
    /// with its patches, as D21 rebuilds one) or a method of the game or a mod, which is given a
    /// copy compiled from its own IL (a Harmony replacement without patches, so that Harmony replaces
    /// it when the method is patched itself), which calls the patched method.
    internal static unsafe class PatchInliners
    {
        // ICorJitInfo's slot (dotnet/runtime v10.0.0 src/coreclr/inc/corinfo.h: isIntrinsic,
        // notifyMethodInfoUsage, getMethodAttribs, setMethodAttribs, getMethodSig, getMethodInfo,
        // haveSameMethodDefinition, getTypeDefinition, canInline, beginInlining, then this), and
        // CorInfoInline's INLINE_PASS.
        const int ReportInliningDecision = 10;
        const int InlinePass = 0;

        static bool installed;
        static readonly object gate = new();
        // Per compiling thread: the methods the compile in progress inlined so far.
        [ThreadStatic] static List<IntPtr>? inlining;
        // A compiled method's handle → the methods its compiled code inlined (sorted handles), and
        // the method (null for a dynamic method).
        static readonly Dictionary<IntPtr, Compiled> compiled = new();
        // A replacement's handle → the method it replaces; a method's current replacement's handle.
        static readonly Dictionary<IntPtr, MethodBase> replaced = new();
        static readonly Dictionary<MethodBase, IntPtr> replacementOf = new();
        // The handles of patched methods (and of a value type's unboxed method under one), and the
        // methods given a copy without patches.
        static readonly HashSet<IntPtr> patchedHandles = new();
        static readonly HashSet<MethodBase> copies = new();
        static readonly List<IntPtr> late = new();
        static MethodInfo? methodDescriptor;
        // While `compiled` is enumerated (under the gate): a compile on the same thread (an OSR
        // version of a loop in the scan, a method it calls the first time) is held back.
        static bool enumerating;
        static readonly List<(IntPtr Handle, MethodBase? Method, IntPtr[] Inlined)> heldBack = new();

        sealed class Compiled
        {
            public MethodBase? Method;
            public IntPtr[] Inlined = [];
        }

        /// Whether the JIT's inlining decisions are seen (MonoMod's managed JIT hook; its native one,
        /// on macOS, hands the JIT no wrapper).
        public static bool Available { get; private set; }

        public static void Install()
        {
            if (installed) return;
            installed = true;
            var harmony = typeof(Harmony).Assembly;
            var triple = harmony.GetType("MonoMod.Core.Platforms.PlatformTriple", true)!;
            var runtime = triple.GetProperty("Runtime")!.GetValue(triple.GetProperty("Current")!.GetValue(null))!;
            methodDescriptor = typeof(DynamicMethod).GetMethod("GetMethodDescriptor", BindingFlags.NonPublic | BindingFlags.Instance);
            // MonoMod installs its JIT hook with its runtime: the managed hook's delegate holds the
            // wrapper's vtable, whose slots forward to the JIT's ICorJitInfo.
            var hook = harmony.GetType("MonoMod.Core.Platforms.Runtimes.Core21Runtime", true)!
                .GetField("ourCompileMethod", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(runtime) as Delegate;
            // The slot is .NET 10's (the JIT-EE interface MonoMod's Core100Runtime checks for).
            if (runtime.GetType().Name != "Core100Runtime"
                || hook?.Target?.GetType().GetField("iCorJitInfoWrapperVtbl")?.GetValue(hook.Target) is not IntPtr vtable || vtable == IntPtr.Zero
                || hook.Target.GetType().FullName != "MonoMod.Core.Platforms.Runtimes.Core60Runtime+JitHookDelegateHolder")
            {
                EngineLog.Engine($"patch inliners: MonoMod's JIT hook ({runtime.GetType().Name}) has no .NET 10 ICorJitInfo wrapper here; code compiled before a patch that inlined the patched method is not rebuilt");
                return;
            }
            var compiledEvent = harmony.GetType("MonoMod.Core.Platforms.IRuntime", true)!.GetEvent("OnMethodCompiled")
                ?? throw new MissingMemberException("MonoMod.Core.Platforms.IRuntime", "OnMethodCompiled");
            compiledEvent.AddEventHandler(runtime, Delegate.CreateDelegate(compiledEvent.EventHandlerType!, typeof(PatchInliners).GetMethod(nameof(MethodCompiled), BindingFlags.NonPublic | BindingFlags.Static)!));
            // The report is compiled before the JIT calls it (a null this returns at once).
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, IntPtr, void> report = &Report;
            report(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, InlinePass, IntPtr.Zero);
            ((IntPtr*)vtable)[ReportInliningDecision] = (IntPtr)report;
            Available = true;
        }

        /// The JIT's report of a decision to inline `inlinee` (into `inliner`, the method being
        /// compiled or one inlined into it), made through MonoMod's wrapper (`self`), whose second
        /// word is the JIT's own ICorJitInfo; the report is passed on to it.
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        static void Report(IntPtr self, IntPtr inliner, IntPtr inlinee, int result, IntPtr reason)
        {
            if (self == IntPtr.Zero) return;
            if (result == InlinePass)
                try
                {
                    (inlining ??= new List<IntPtr>()).Add(inlinee);
                }
                catch (Exception) // out of memory: the inlining goes unrecorded
                {
                }
            IntPtr** wrapped = *(IntPtr***)((byte*)self + IntPtr.Size);
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, IntPtr, void>)(*wrapped)[ReportInliningDecision])((IntPtr)wrapped, inliner, inlinee, result, reason);
        }

        /// MonoMod's OnMethodCompiledCallback, raised on the compiling thread after each compile
        /// made through its wrapper: what that compile inlined is recorded under the method.
        static void MethodCompiled(RuntimeMethodHandle handle, MethodBase? method, IntPtr codeStart, IntPtr codeRw, ulong codeSize)
        {
            var list = inlining;
            if (list == null || list.Count == 0) return;
            list.Sort((a, b) => a.CompareTo(b));
            int distinct = 1;
            for (int i = 1; i < list.Count; i++) if (list[i] != list[distinct - 1]) list[distinct++] = list[i];
            var inlined = list.GetRange(0, distinct).ToArray();
            list.Clear();
            lock (gate)
            {
                if (enumerating) heldBack.Add((handle.Value, method, inlined));
                else Record(handle.Value, method, inlined);
            }
        }

        /// Under the gate: `inlined` (sorted, distinct) merged into what `handle`'s compiles inlined.
        /// A compile that inlined a method already patched began before the patch was looked for:
        /// it is rebuilt once its code is in (a tier-up's code is installed after its compile).
        static void Record(IntPtr handle, MethodBase? method, IntPtr[] inlined)
        {
            bool stale = false;
            foreach (var h in inlined) stale |= patchedHandles.Contains(h);
            if (stale)
            {
                late.Add(handle);
                if (late.Count == 1) ThreadPool.QueueUserWorkItem(_ => RebuildLate());
            }
            if (!compiled.TryGetValue(handle, out var entry)) compiled[handle] = entry = new Compiled();
            entry.Method ??= method is DynamicMethod ? null : method;
            if (entry.Inlined.Length == 0) entry.Inlined = inlined;
            else
            {
                var merged = new SortedSet<IntPtr>(entry.Inlined);
                merged.UnionWith(inlined);
                entry.Inlined = new IntPtr[merged.Count];
                merged.CopyTo(entry.Inlined);
            }
        }

        /// The handles code inlining `method` names: its own, for a value type's override the unboxed
        /// method under its unboxing stub, and for an instantiation over a reference type the shared
        /// code under its instantiating stub (D13): what the JIT inlines.
        static List<IntPtr> HandlesOf(MethodBase method)
        {
            var handles = new List<IntPtr>();
            try
            {
                var handle = method.MethodHandle.Value;
                handles.Add(handle);
                if (method.DeclaringType is { IsValueType: true } && method.IsVirtual && !method.IsStatic
                    && UnboxedMethods.Unboxed(handle) is var unboxed && unboxed != IntPtr.Zero)
                    handles.Add(unboxed);
                if (UnboxedMethods.Shared(handle) is var shared && shared != IntPtr.Zero) handles.Add(shared);
            }
            catch (Exception) // no handle (a dynamic method): nothing inlines it
            {
            }
            return handles;
        }

        static IntPtr HandleOf(MethodInfo replacement)
        {
            try
            {
                return replacement is DynamicMethod && methodDescriptor?.Invoke(replacement, null) is RuntimeMethodHandle handle ? handle.Value
                    : replacement.MethodHandle.Value;
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        /// After Harmony replaced `original` by `replacement` (a patch, an unpatch, a rebuild or a
        /// copy): the replacement's code is `original`'s now, and the earlier code of both is no
        /// longer run.
        public static void Replaced(MethodBase original, MethodInfo replacement, bool copy)
        {
            if (!Available) return;
            var handle = HandleOf(replacement);
            lock (gate)
            {
                if (replacementOf.TryGetValue(original, out var old))
                {
                    replaced.Remove(old);
                    compiled.Remove(old);
                }
                foreach (var h in HandlesOf(original)) compiled.Remove(h);
                if (handle != IntPtr.Zero)
                {
                    replaced[handle] = original;
                    replacementOf[original] = handle;
                }
                if (copy) copies.Add(original);
                else copies.Remove(original);
            }
        }

        /// The methods whose code may hold `method`'s inlined, now that it is patched and marked
        /// not to be inlined: those of a replacement (its original) and other compiled methods.
        public static List<MethodBase> Inlining(MethodBase method)
        {
            var found = new List<MethodBase>();
            if (!Available) return found;
            var handles = HandlesOf(method);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int recorded;
            lock (gate)
            {
                recorded = compiled.Count;
                foreach (var h in handles) patchedHandles.Add(h);
                var seen = new HashSet<MethodBase>();
                enumerating = true;
                try
                {
                    foreach (var (handle, entry) in compiled)
                    {
                        bool inlines = false;
                        foreach (var h in handles) inlines |= Array.BinarySearch(entry.Inlined, h) >= 0;
                        if (inlines && Owner(handle, entry) is { } owner && seen.Add(owner)) found.Add(owner);
                    }
                }
                finally
                {
                    enumerating = false;
                }
                foreach (var (handle, held, inlined) in heldBack) Record(handle, held, inlined);
                heldBack.Clear();
            }
            if (found.Count > 0)
                EngineLog.Engine($"patch inliners: {method.DeclaringType?.FullName}.{method.Name} is inlined in {found.Count} of {recorded} compiled methods (looked up in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms)");
            return found;
        }

        /// The method whose code `handle` is: the original of a replacement, or the method itself
        /// (none for another dynamic method).
        static MethodBase? Owner(IntPtr handle, Compiled entry) =>
            replaced.TryGetValue(handle, out var original) ? original : entry.Method;

        /// Whether `method` runs a copy of its own made by this class (no patches).
        public static bool IsCopy(MethodBase method)
        {
            lock (gate) return copies.Contains(method);
        }

        /// Whether `method` can be given a copy: it has IL Harmony can rebuild, and is not a type
        /// initializer (run once already), shared generic code (its MethodBase is over __Canon) or
        /// Harmony's or this host's own.
        public static bool Copyable(MethodBase method, out string why)
        {
            why = "";
            if (method.IsAbstract || method.GetMethodBody() == null) why = "it has no IL";
            else if (method is ConstructorInfo { IsStatic: true }) why = "it is a type initializer";
            else if (method.ContainsGenericParameters || Canonical(method.DeclaringType) || (method.IsGenericMethod && Array.Exists(method.GetGenericArguments(), Canonical)))
                why = "it is shared generic code";
            else if (method.DeclaringType?.Assembly is { } assembly && (assembly == typeof(Harmony).Assembly || assembly == typeof(PatchInliners).Assembly))
                why = "it is Harmony's or the host's";
            return why.Length == 0;
        }

        static bool Canonical(Type? type) => type != null && (type.FullName == "System.__Canon"
            || (type.IsGenericType && Array.Exists(type.GetGenericArguments(), Canonical)));

        /// Compiles that finished after a patch of a method they inlined had been looked for.
        static void RebuildLate()
        {
            Thread.Sleep(100);
            var lockObject = AccessTools.Field("HarmonyLib.PatchProcessor:locker")?.GetValue(null) ?? gate;
            List<MethodBase> owners = new();
            lock (lockObject)
            {
                lock (gate)
                {
                    foreach (var handle in late)
                        if (compiled.TryGetValue(handle, out var entry) && Owner(handle, entry) is { } owner && !owners.Contains(owner)) owners.Add(owner);
                    late.Clear();
                }
                PatchDependents.Rebuild(owners, "it inlined a method patched while it was compiled");
            }
        }
    }
}
