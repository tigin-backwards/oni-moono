using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-save-type-checks`. SaveLoadRoot.SaveWithoutTransform, run for every
    /// saved object, asks for each of its components twice whether its type carries
    /// [SkipSaveFileSerialization] (Type.IsDefined, which reads the attribute metadata every time)
    /// and writes the type's name (Type.ToString, a new string every time): in a large colony a
    /// tenth of a save's time is these runtime calls.
    ///
    /// Both answers depend only on the type, so they are kept per type: the same booleans and the
    /// same name are used, from the first time each type is asked. Only those two calls in that
    /// method are replaced. Mods' transpilers of the method run first and see the game's calls
    /// (Better Save replaces the same IsDefined calls with its cache, and gave up when it
    /// found ours); what they leave of them is replaced.
    ///
    /// The method also starts with GetComponents<KMonoBehaviour>(), a new array for every saved
    /// object (about 70 MB of a late-game colony's autosave). The array never leaves the method,
    /// so it is replaced by one kept per length and per nesting depth (a component's Serialize
    /// saves its stored items through the same method), filled with the same components in the
    /// same order and cleared when the method ends. Off for the session, back to the game's
    /// array, once another Harmony owner transpiles the method (checked at the first call, then
    /// every 65,536). With ONI_TWEAK_VERIFY=1 each kept array is compared with the game's.
    [GameMember(GameUse.Transpiler | GameUse.Prefix | GameUse.Finalizer, "System.Void SaveLoadRoot::SaveWithoutTransform(System.IO.BinaryWriter)", "4847188af20e37a7 linux-744825 win-744825 linux-763796 win-763796 linux-764025 win-764025 linux-764961 win-764961", "32d81ac47c5cba37 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761211 win-761654 win-762107 win-762230 win-763023 win-763325 win-763612", Rewrites = 4)]
    public static class FastSaveTypeChecks
    {
        public const string Name = "fast-save-type-checks";

        static readonly ConcurrentDictionary<(MemberInfo, Type, bool), bool> defined = new ConcurrentDictionary<(MemberInfo, Type, bool), bool>();
        static readonly ConcurrentDictionary<Type, string> names = new ConcurrentDictionary<Type, string>();

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static MethodBase target;

        /// Test infrastructure (TimelapseTimingTestMod's same-frame saves): true hands out the
        /// game's GetComponents array, for comparing saves in one run.
        public static bool Bypass;

        public static void Install(Harmony harmony)
        {
            target = AccessTools.Method(typeof(SaveLoadRoot), nameof(SaveLoadRoot.SaveWithoutTransform));
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(FastSaveTypeChecks), nameof(Entering)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(FastSaveTypeChecks), nameof(Leaving)) { priority = Priority.First },
                transpiler: new HarmonyMethod(typeof(FastSaveTypeChecks), nameof(Transpiler)) { priority = Priority.Last });
        }

        /// The method whose transpiling by another owner makes the kept arrays stand down. Not a
        /// ModOverlaps guard: the type checks stay on, so such a transpiler is still an overlap.
        internal static MethodBase Guarded() => AccessTools.Method(typeof(SaveLoadRoot), nameof(SaveLoadRoot.SaveWithoutTransform));

        static bool reported;

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var isDefined = AccessTools.Method(typeof(MemberInfo), nameof(MemberInfo.IsDefined), new[] { typeof(Type), typeof(bool) });
            var getType = AccessTools.Method(typeof(object), nameof(GetType));
            var toString = AccessTools.Method(typeof(object), nameof(ToString));
            var cachedIsDefined = AccessTools.Method(typeof(FastSaveTypeChecks), nameof(IsDefined));
            var cachedName = AccessTools.Method(typeof(FastSaveTypeChecks), nameof(TypeName));
            var getComponents = AccessTools.Method(typeof(UnityEngine.Component), nameof(UnityEngine.Component.GetComponents), Type.EmptyTypes).MakeGenericMethod(typeof(KMonoBehaviour));
            var keptComponents = AccessTools.Method(typeof(FastSaveTypeChecks), nameof(Components));
            int checks = 0, typeNames = 0, arrays = 0;
            CodeInstruction previous = null;
            foreach (var i in instructions)
            {
                // Replaced in place, so the instruction keeps its labels and exception blocks.
                if (i.Calls(isDefined))
                {
                    checks++;
                    i.opcode = OpCodes.Call;
                    i.operand = cachedIsDefined;
                }
                // Object.ToString called on the result of GetType(): the type's name.
                else if (i.Calls(toString) && previous != null && previous.Calls(getType))
                {
                    typeNames++;
                    i.opcode = OpCodes.Call;
                    i.operand = cachedName;
                }
                else if (i.Calls(getComponents))
                {
                    arrays++;
                    i.opcode = OpCodes.Call;
                    i.operand = keptComponents;
                }
                previous = i;
                yield return i;
            }
            OniMoono.Tweaks.TranspilerCounts.Report(Name, original, checks + typeNames + arrays);
            if ((checks != 2 || typeNames != 1 || arrays != 1) && !reported)
            {
                reported = true;
                global::Debug.Log($"{Name}: SaveLoadRoot.SaveWithoutTransform has {checks} of the game's 2 IsDefined calls, {typeNames} of its 1 type name and {arrays} of its 1 GetComponents left (another patch changed it); those are kept");
            }
        }

        /// MemberInfo.IsDefined(attributeType, inherit), kept per member.
        static bool IsDefined(MemberInfo member, Type attribute, bool inherit)
            => defined.GetOrAdd((member, attribute, inherit), key => key.Item1.IsDefined(key.Item2, key.Item3));

        /// Type.ToString(), kept per type.
        static string TypeName(object type) => names.GetOrAdd((Type)type, t => t.ToString());

        // ----- The component arrays -----

        /// Per thread (saves run on the main thread; a mod's save elsewhere gets arrays of its
        /// own): the nesting depth, and for each depth the arrays by length and the one in use.
        [ThreadStatic] static Kept kept;

        sealed class Kept
        {
            public int Depth;
            public KMonoBehaviour[][][] ByLength = new KMonoBehaviour[4][][];
            public KMonoBehaviour[][] InUse = new KMonoBehaviour[4][];
            public readonly System.Collections.Generic.List<KMonoBehaviour> Scratch = new System.Collections.Generic.List<KMonoBehaviour>(64);
        }

        static bool off;
        static long calls, verified, mismatches;

        /// Prefix: one level deeper; `__state` is the depth to go back to.
        static void Entering(out int __state)
        {
            var k = kept ??= new Kept();
            __state = k.Depth;
            k.Depth = __state + 1;
        }

        /// Finalizer: the array of this level emptied (it holds no component past the call), and
        /// the depth back to the caller's, however the method ended.
        static void Leaving(int __state)
        {
            var k = kept;
            if (k == null) return;
            int level = __state;
            if (level < k.InUse.Length && k.InUse[level] is { } used)
            {
                Array.Clear(used, 0, used.Length);
                k.InUse[level] = null;
            }
            k.Depth = level;
        }

        /// Component.GetComponents<KMonoBehaviour>() for the method's first line: the same
        /// components in the same order, in an array kept for this depth and length.
        static KMonoBehaviour[] Components(UnityEngine.Component self)
        {
            var k = kept;
            if (Bypass || off || k == null || k.Depth == 0) return self.GetComponents<KMonoBehaviour>();
            if ((calls++ & 0xFFFF) == 0 && TranspiledByOther() is { } why)
            {
                off = true;
                global::Debug.Log($"{Name}: GetComponents keeps the game's array ({why})");
                return self.GetComponents<KMonoBehaviour>();
            }
            var scratch = k.Scratch;
            self.GetComponents(scratch);
            int level = k.Depth - 1, n = scratch.Count;
            if (level >= k.ByLength.Length)
            {
                Array.Resize(ref k.ByLength, level * 2);
                Array.Resize(ref k.InUse, level * 2);
            }
            var lengths = k.ByLength[level] ??= new KMonoBehaviour[64][];
            if (n >= lengths.Length)
            {
                Array.Resize(ref lengths, n * 2);
                k.ByLength[level] = lengths;
            }
            var array = lengths[n] ??= new KMonoBehaviour[n];
            scratch.CopyTo(array);
            scratch.Clear();
            k.InUse[level] = array;
            if (verify) Verify(self, array);
            return array;
        }

        /// The first other owner's transpiler (or inner patch) of the method, which could keep
        /// the array past the call, as "OWNER transpiles SaveLoadRoot.SaveWithoutTransform".
        static string TranspiledByOther()
        {
            var info = Harmony.GetPatchInfo(target ?? Guarded());
            if (info == null) return null;
            foreach (var list in new[] { info.Transpilers, info.InnerPrefixes, info.InnerPostfixes })
                foreach (var patch in list)
                    if (ModOverlapsCore.IsForeign(patch.owner)) return $"{patch.owner} transpiles SaveLoadRoot.SaveWithoutTransform";
            return null;
        }

        static void Verify(UnityEngine.Component self, KMonoBehaviour[] array)
        {
            var game = self.GetComponents<KMonoBehaviour>();
            bool same = game.Length == array.Length;
            for (int i = 0; same && i < game.Length; i++) same = ReferenceEquals(game[i], array[i]);
            verified++;
            if (!same)
            {
                mismatches++;
                global::Debug.LogWarning($"{Name}: verify: the kept component array of {self.name} differs from GetComponents' ({game.Length} against {array.Length})");
            }
            if ((verified & 0xFFFF) == 1) global::Debug.Log($"{Name}: verify: {verified} kept component arrays compared, {mismatches} differ");
        }
    }
}
