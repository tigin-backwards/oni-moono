using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// ARCHITECTURE D13. Before detouring a method, Harmony's MonoMod marks it not to be inlined
    /// (its MethodDesc's NotInline flag), so code compiled later calls the patched method instead
    /// of inlining the original's body. For a virtual method of a value type (an override of
    /// GetHashCode, Equals, ToString), reflection's handle is the method's unboxing stub; CoreCLR's
    /// JIT inlines the unboxed MethodDesc that follows it, which MonoMod never marks, so code tiered
    /// up after the patch runs the original (Fast Track's AxialI.GetHashCode: lookups in
    /// dictionaries filled at tier 0 missed, ClusterGrid.RegisterEntity threw). The host marks the
    /// unboxed MethodDesc as well. Mono has no unboxing stubs, so the original player never needs to.
    ///
    /// Likewise for an instantiation whose code is shared, a generic method over a reference type:
    /// reflection's handle is its instantiating stub, and the JIT inlines the shared MethodDesc the
    /// stub wraps (`GetObjectUnderCursor<__Canon>`), which the host marks too. That shared code
    /// is also tiered up under its own MethodDesc, where MonoMod does not look for the detour to
    /// move onto the new code (Recompiled). Fast Track replaces the body of
    /// InterfaceTool.GetObjectUnderCursor<KSelectable> and calls it from its SelectTool click
    /// prefix; once the shared code tiered up, the original body ran, which returns the first object
    /// under the cursor every time, so clicks stopped cycling through a stack (issue #48).
    internal static class UnboxedMethods
    {
        // CoreCLR 10's MethodDesc (src/coreclr/vm/method.hpp): UINT16 m_wFlags3AndTokenRemainder,
        // BYTE m_chunkIndex, BYTE m_bFlags4, WORD m_wSlotNumber, WORD m_wFlags; 8-byte aligned.
        const int Flags3 = 0, ChunkIndex = 2, Flags = 6, Alignment = 8;
        const ushort TokenRemainderMask = 0x0FFF, IsUnboxingStub = 0x4000;
        const ushort HasNonVtableSlot = 0x0008, NotInline = 0x2000;
        // m_wFlags' classification (mdcClassification), and InstantiatedMethodDesc's fields after the
        // MethodDesc (its m_codeData pointer makes it 16 bytes): m_pWrappedMethodDesc (a union with
        // m_pDictLayout), m_pPerInstInfo, WORD m_wFlags2, whose low bits are the kind.
        const ushort ClassificationMask = 0x0007, Instantiated = 5;
        const int WrappedMethodDesc = 16, Flags2 = 32;
        const ushort KindMask = 0x0007, SharedInstantiation = 3, WrapperStub = 4;
        // MethodTableBuilder puts a value type's unboxed MethodDesc right after its unboxing stub;
        // the stub's size is its base size and optional slots, at most this.
        const int MaxStubSize = 64;

        static bool installed;
        static readonly object gate = new();
        // A shared MethodDesc → the patched instantiations it is the code of.
        static readonly Dictionary<IntPtr, List<MethodBase>> instantiationsOf = new();
        static MethodInfo? onMethodCompiled;
        [ThreadStatic] static bool forwarding;
        /// How many times a patched instantiation's detour was moved onto its recompiled shared code.
        internal static int recompiles;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            var tryDisable = AccessTools.Method("MonoMod.Core.Platforms.PlatformTriple:TryDisableInlining")
                ?? throw new MissingMethodException("MonoMod.Core.Platforms.PlatformTriple", "TryDisableInlining");
            onMethodCompiled = AccessTools.Method("MonoMod.Core.Platforms.PlatformTripleDetourFactory+Detour:OnMethodCompiled")
                ?? throw new MissingMethodException("MonoMod.Core.Platforms.PlatformTripleDetourFactory+Detour", "OnMethodCompiled");
            var harmony = new Harmony("OniMoono.Host.UnboxedMethods");
            harmony.Patch(tryDisable, postfix: new HarmonyMethod(typeof(UnboxedMethods), nameof(Postfix)));
            harmony.Patch(onMethodCompiled, prefix: new HarmonyMethod(typeof(UnboxedMethods), nameof(Recompiled)));
        }

        static void Postfix(MethodBase method, bool __result)
        {
            if (!__result) return;
            MarkShared(method);
            if (method.DeclaringType is not { IsValueType: true } || method.IsStatic || !method.IsVirtual) return;
            if (!MarkUnboxed(method.MethodHandle.Value))
                EngineLog.Engine($"unboxed methods: no unboxed MethodDesc after {method.DeclaringType.FullName}.{method.Name}'s unboxing stub; a patch of it may be inlined past");
        }

        /// For an instantiation whose code is shared (a generic method over a reference type, as
        /// Fast Track patches InterfaceTool.GetObjectUnderCursor<KSelectable>): sets NotInline on the
        /// shared MethodDesc its instantiating stub wraps, which is what the JIT inlines.
        static unsafe void MarkShared(MethodBase method)
        {
            IntPtr handle;
            try
            {
                handle = method.MethodHandle.Value;
            }
            catch (Exception) // no handle (a dynamic method): nothing inlines it
            {
                return;
            }
            var shared = Shared(handle);
            if (shared == IntPtr.Zero) return;
            if (!SameDefinition(method, shared))
            {
                EngineLog.Engine($"unboxed methods: the shared code under {method.DeclaringType?.FullName}.{method.Name}'s instantiating stub is another method; a patch of it may be inlined past");
                return;
            }
            SetFlag((byte*)shared, NotInline);
            lock (gate)
            {
                if (!instantiationsOf.TryGetValue(shared, out var list)) instantiationsOf[shared] = list = new List<MethodBase>();
                if (!list.Contains(method)) list.Add(method);
            }
        }

        /// MonoMod's handler for a method the JIT compiled again (PlatformTripleDetourFactory.Detour's
        /// OnMethodCompiled) moves a detour onto the new code. It finds the detours by the method
        /// compiled, and tiered compilation recompiles the shared code under its own MethodDesc
        /// (`GetObjectUnderCursor<__Canon>`), not the instantiation patched: the new code would run
        /// unpatched. The compile is reported again here for each patched instantiation it is the
        /// code of, so their detours follow.
        static void Recompiled(RuntimeMethodHandle methodHandle, IntPtr codeStart, IntPtr codeStartRw, ulong codeSize)
        {
            if (forwarding) return;
            MethodBase[] patched;
            lock (gate)
            {
                if (!instantiationsOf.TryGetValue(methodHandle.Value, out var list)) return;
                patched = list.ToArray();
            }
            forwarding = true;
            try
            {
                foreach (var method in patched)
                {
                    onMethodCompiled!.Invoke(null, [method.MethodHandle, method, codeStart, codeStartRw, codeSize]);
                    Interlocked.Increment(ref recompiles);
                }
            }
            finally
            {
                forwarding = false;
            }
        }

        /// The shared MethodDesc an instantiating stub wraps, or zero for any other MethodDesc.
        internal static unsafe IntPtr Shared(IntPtr stub)
        {
            byte* p = (byte*)stub;
            if ((*(ushort*)(p + Flags) & ClassificationMask) != Instantiated || (*(ushort*)(p + Flags2) & KindMask) != WrapperStub) return IntPtr.Zero;
            byte* q = *(byte**)(p + WrappedMethodDesc);
            if (q == null || (*(ushort*)(q + Flags) & ClassificationMask) != Instantiated || (*(ushort*)(q + Flags2) & KindMask) != SharedInstantiation)
                return IntPtr.Zero;
            return (IntPtr)q;
        }

        static bool SameDefinition(MethodBase method, IntPtr shared)
        {
            try
            {
                var found = MethodBase.GetMethodFromHandle(RuntimeMethodHandle.FromIntPtr(shared));
                return found != null && found.MetadataToken == method.MetadataToken && found.Module == method.Module;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// Sets NotInline on the unboxed MethodDesc following an unboxing stub. False when the handle
        /// is no unboxing stub or no MethodDesc after it matches (same chunk, same token, not a stub,
        /// in a non-vtable slot).
        internal static unsafe bool MarkUnboxed(IntPtr stub)
        {
            var unboxed = Unboxed(stub);
            if (unboxed == IntPtr.Zero) return false;
            SetFlag((byte*)unboxed, NotInline);
            return true;
        }

        /// The unboxed MethodDesc following an unboxing stub, or zero.
        internal static unsafe IntPtr Unboxed(IntPtr stub)
        {
            byte* p = (byte*)stub;
            ushort flags3 = *(ushort*)(p + Flags3);
            if ((flags3 & IsUnboxingStub) == 0) return IntPtr.Zero;
            for (int offset = Alignment; offset <= MaxStubSize; offset += Alignment)
            {
                byte* q = p + offset;
                ushort qFlags3 = *(ushort*)(q + Flags3);
                if (q[ChunkIndex] != p[ChunkIndex] + offset / Alignment) continue;
                if ((qFlags3 & TokenRemainderMask) != (flags3 & TokenRemainderMask) || (qFlags3 & IsUnboxingStub) != 0) continue;
                if ((*(ushort*)(q + Flags) & HasNonVtableSlot) == 0) continue;
                return (IntPtr)q;
            }
            return IntPtr.Zero;
        }

        /// m_wFlags is the high half of the aligned 32-bit word at offset 4 (little-endian); the
        /// runtime updates it with interlocked operations too.
        static unsafe void SetFlag(byte* methodDesc, ushort flag)
        {
            ref int word = ref *(int*)(methodDesc + Flags - 2);
            int seen, wanted;
            do
            {
                seen = Volatile.Read(ref word);
                wanted = seen | (flag << 16);
            }
            while (seen != wanted && Interlocked.CompareExchange(ref word, wanted, seen) != seen);
        }
    }
}
