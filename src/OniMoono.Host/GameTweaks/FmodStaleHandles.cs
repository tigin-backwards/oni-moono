#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FMOD;
using FMOD.Studio;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `fmod-stale-handles` (on by default): the game's calls on an FMOD event instance
    /// that FMOD has already destroyed are not passed to FMOD (docs/TWEAKS.md, "The stale sound
    /// handle guard"; issues #78, #72, #73 and #58).
    ///
    /// An FMOD Studio handle is 32 bits: a table (system and page, the low 8 bits), a slot (13
    /// bits) and a serial (11 bits). FMOD resolves one by checking that the slot's object still
    /// holds that handle; the serial goes up by one each time the slot is freed and wraps after
    /// 2,047, and freed slots are reused oldest first. Each call is resolved twice: on the
    /// calling thread, and again by the Studio update thread when it runs the queued command,
    /// which writes into whatever object the handle resolves to without checking its kind
    /// (EventInstance::stop's command increments a counter at +0x84 and clears a flag at +0xBD).
    /// The game keeps handles after it has released them: an ambience layer keeps its stopped
    /// instance and checks it with isValid() every update (AmbienceManager.Layer.Stop), and
    /// LoopingSoundManager.StopAllSounds (switching worlds) releases every playing loop but leaves
    /// it flagged as playing, so its position, volume and parameters are set every frame. Each
    /// such handle fails while its slot is free or holds an object with another serial, but once
    /// the slot has been reused 2,047 times it holds a new object with the same handle, and the
    /// game's call lands on it: on another sound, or on an object of another kind and the memory
    /// past its end, the stray writes behind #78's `fmod heap:` lines.
    ///
    /// Here each event instance the game creates (EventDescription.createInstance, the only way to
    /// make one) is noted alive, and each one FMOD destroys is noted dead from FMOD's DESTROYED
    /// callback, which FMOD's Studio update thread makes just before it frees the instance and
    /// its handle. Each event's description gets that callback (EventDescription.setCallback,
    /// DESTROYED only) before its first instance is made, so every instance of it carries it;
    /// none is set on a description a mod or the game gave a callback of its own, and an instance
    /// given its own (EventInstance.setCallback) is simply never noted dead. A call on a handle
    /// noted dead and not made again since returns ERR_INVALID_HANDLE (isValid() false) without
    /// reaching FMOD, which is what FMOD itself answers for a destroyed instance whose slot has
    /// not come round again: nothing audible changes. Calls on live instances and on handles the
    /// game never made run as they always have. Only the calls that change an instance are
    /// guarded, and isValid(); getters only read.
    ///
    /// The record is one bit per possible handle of each FMOD handle table the game's instances
    /// come from (2 MB a table, made when its first instance is), so it never grows with play and
    /// its checks take no lock: the callback sets a bit, a create clears it, a call tests it.
    ///
    /// With ONI_TWEAK_VERIFY=1 (or ONI_VERIFY_FMOD_STALE_HANDLES=1) every skipped call is counted by method, the first for each handle
    /// is logged with its caller, and each is checked against a second record of the destroyed
    /// handles kept under a lock and against FMOD's own isValid (true there means FMOD would have
    /// sent the call to another object that has the same handle now).
    [GameMember(GameUse.Prefix | GameUse.Postfix, "FMOD.RESULT FMOD.Studio.EventDescription::createInstance(FMOD.Studio.EventInstance&)")]
    [GameMember(GameUse.Prefix, "FMOD.RESULT FMOD.Studio.EventDescription::setCallback(FMOD.Studio.EVENT_CALLBACK,FMOD.Studio.EVENT_CALLBACK_TYPE)")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Boolean FMOD.Studio.EventInstance::isValid()", "11ab653238781fc5 linux-744825 win-744825", "175c2cca53ce6a66 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setVolume(System.Single)", "059ae69c480ed978 linux-744825 win-744825", "2fc7b2ff93091ffa linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setPitch(System.Single)", "5ca372829b81bc08 linux-744825 win-744825", "f5306557ff1ba25a linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::set3DAttributes(FMOD.ATTRIBUTES_3D)", "7dce5080b742d10c linux-744825 win-744825", "490ef6f1dcb3a2f7 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setListenerMask(System.UInt32)", "8f2ccaf04658c6c0 linux-744825 win-744825", "004814ca6a86ac3b linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setProperty(FMOD.Studio.EVENT_PROPERTY,System.Single)", "3facd0c96014b6af linux-744825 win-744825", "e08d6423da8f51f6 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setReverbLevel(System.Int32,System.Single)", "473ee9400751b31a linux-744825 win-744825", "4b0ff41a47573757 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setPaused(System.Boolean)", "1649db84c707bb02 linux-744825 win-744825", "7fe59ead960db7d4 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::start()", "fb8ca4d6b4e7112c linux-744825 win-744825", "e227a8947dc566f6 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::stop(FMOD.Studio.STOP_MODE)", "993e7d2380f497f5 linux-744825 win-744825", "1de1cfa39871cfae linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setTimelinePosition(System.Int32)", "706f3c9943aeee44 linux-744825 win-744825", "31a1fbc789f0b2d0 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::release()", "2c3165fb77180513 linux-744825 win-744825", "aebfae587eafc7c5 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setParameterByID(FMOD.Studio.PARAMETER_ID,System.Single,System.Boolean)", "260152f81f2995c7 linux-744825 win-744825", "2ae73751f7965d2b linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setParameterByIDWithLabel(FMOD.Studio.PARAMETER_ID,System.String,System.Boolean)", "d19ee1c13d6c2d2a linux-744825 win-744825", "aefc1db19eff9ab6 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setParametersByIDs(FMOD.Studio.PARAMETER_ID[],System.Single[],System.Int32,System.Boolean)", "71069c4c6d5c18c1 linux-744825 win-744825", "2b31f6edeedc04b3 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setParameterByName(System.String,System.Single,System.Boolean)", "1f4b5d658993c512 linux-744825 win-744825", "57cccff9bf338425 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setParameterByNameWithLabel(System.String,System.String,System.Boolean)", "b6c53cb5be8559bf linux-744825 win-744825", "f11e923aa93ee938 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::keyOff()", "3b70cef560dcd6da linux-744825 win-744825", "21fc25d572da9936 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setCallback(FMOD.Studio.EVENT_CALLBACK,FMOD.Studio.EVENT_CALLBACK_TYPE)", "1b0db06afe84dd50 linux-744825 win-744825", "8beedd31c1d1986b linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.RESULT FMOD.Studio.EventInstance::setUserData(System.IntPtr)", "f9fdb8838d29b94b linux-744825 win-744825", "b56fa8330da2d43b linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Uses, "System.Boolean FMOD.Studio.EventInstance::FMOD_Studio_EventInstance_IsValid(System.IntPtr)")]
    internal static unsafe class FmodStaleHandles
    {
        public const string Name = GameTweakNames.FmodStaleHandles;

        /// The EventInstance methods guarded: those that change the instance (each a command the
        /// Studio update thread runs on whatever the handle resolves to), and isValid.
        internal static readonly string[] Guarded =
        [
            "setVolume", "setPitch", "set3DAttributes", "setListenerMask", "setProperty", "setReverbLevel", "setPaused", "start", "stop",
            "setTimelinePosition", "release", "setParameterByID", "setParameterByIDWithLabel", "setParametersByIDs", "setParameterByName",
            "setParameterByNameWithLabel", "keyOff", "setCallback", "setUserData",
        ];

        /// A handle's bit: 2^13 slots × 2^11 serials a table, 2^18 words (2 MB).
        const int WordsPerTable = 1 << 18;

        /// Per handle table (the handle's low byte): one bit per handle, set while that handle's
        /// instance is destroyed and not made again. Made by the first create in the table (or, if
        /// FMOD destroys one first, by its callback); never freed.
        static readonly IntPtr[] tables = new IntPtr[256];

        static readonly EVENT_CALLBACK destroyed = OnDestroyed;
        /// Descriptions handled: ours set (true), or one of someone else's (false).
        static readonly Dictionary<IntPtr, bool> descriptions = new();
        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1" || Environment.GetEnvironmentVariable("ONI_VERIFY_FMOD_STALE_HANDLES") == "1";
        [ThreadStatic] static bool setting;

        static long created, destroyedCount, skipped, foreignDescriptions;
        static readonly long[] skippedBy = new long[Guarded.Length + 1];
        static long reportedSkipped;
        static long logAt;

        // Verify mode: the destroyed handles under a lock, FMOD's own check, the handles logged.
        static readonly HashSet<uint> verifyDead = new();
        static readonly HashSet<uint> verifyLogged = new();
        static Func<IntPtr, bool>? fmodIsValid;
        static long verifyAccepted, verifyMismatch;

        /// For specs (`type:OniMoono.Host.GameTweaks.FmodStaleHandles::Skipped`).
        public static bool Active { get; private set; }
        public static long Created => Interlocked.Read(ref created);
        public static long Destroyed => Interlocked.Read(ref destroyedCount);
        public static long Skipped => Interlocked.Read(ref skipped);
        /// Verify mode: skipped calls FMOD would have sent to another object; skips the second
        /// record disagreed with (must stay 0).
        public static long VerifyAccepted => Interlocked.Read(ref verifyAccepted);
        public static long VerifyMismatch => Interlocked.Read(ref verifyMismatch);

        [NameLookupReviewed("FMODUnity's EventInstance methods named in Guarded, each declared")]
        public static void Install(Harmony harmony)
        {
            var description = typeof(EventDescription);
            var instance = typeof(EventInstance);
            harmony.Patch(Method(description, "createInstance", [typeof(EventInstance).MakeByRefType()]),
                prefix: new HarmonyMethod(typeof(FmodStaleHandles), nameof(Creating)),
                postfix: new HarmonyMethod(typeof(FmodStaleHandles), nameof(Created_)));
            harmony.Patch(Method(description, "setCallback", [typeof(EVENT_CALLBACK), typeof(EVENT_CALLBACK_TYPE)]),
                prefix: new HarmonyMethod(typeof(FmodStaleHandles), nameof(DescriptionCallback)));
            harmony.Patch(Method(instance, "isValid", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(FmodStaleHandles), nameof(IsValid)));
            for (int i = 0; i < Guarded.Length; i++)
            {
                var found = AccessTools.GetDeclaredMethods(instance).FindAll(m => m.Name == Guarded[i] && m.ReturnType == typeof(RESULT));
                if (found.Count == 0) throw new MissingMethodException(instance.FullName, Guarded[i]);
                foreach (var m in found)
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(FmodStaleHandles), verify ? nameof(GuardVerify) : nameof(Guard)));
            }
            if (verify)
                fmodIsValid = Method(instance, "FMOD_Studio_EventInstance_IsValid", [typeof(IntPtr)]).CreateDelegate<Func<IntPtr, bool>>();
            Active = true;
            FrameStatsWindow.Reports.Add(Report);
            PlayerLoop.EarlyUpdate += Tick;
            EngineLog.Engine($"{Name}: on, the game's calls on event instances FMOD has destroyed are skipped{(verify ? " (verify: each skip checked and logged)" : "")}");
        }

        [NameLookupReviewed("FMODUnity's EventDescription and EventInstance members, each declared")]
        static System.Reflection.MethodInfo Method(Type type, string name, Type[] args) =>
            AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type.FullName, name);

        // ---- The record. ----

        static ulong* Table(uint handle, bool make)
        {
            var at = Volatile.Read(ref tables[handle & 0xFF]);
            if (at != IntPtr.Zero || !make) return (ulong*)at;
            var made = (IntPtr)NativeMemory.AllocZeroed(WordsPerTable, sizeof(ulong));
            var was = Interlocked.CompareExchange(ref tables[handle & 0xFF], made, IntPtr.Zero);
            if (was == IntPtr.Zero) return (ulong*)made;
            NativeMemory.Free((void*)made);
            return (ulong*)was;
        }

        /// The handle's bit in its table: slot (bits 8-20) and serial (bits 21-31).
        static int Bit(uint handle) => (int)(handle >> 8);

        /// Whether `handle` is one of an instance the game made that FMOD has destroyed since.
        static bool IsDead(IntPtr handle)
        {
            ulong h = (ulong)handle;
            if (h > uint.MaxValue) return false;
            var table = (ulong*)tables[h & 0xFF];
            if (table == null) return false;
            int bit = Bit((uint)h);
            return (table[bit >> 6] & (1UL << bit)) != 0;
        }

        /// FMOD's Studio update thread (any thread FMOD calls back on), just before it destroys
        /// the instance: one atomic OR, no allocation (but a table's first).
        static RESULT OnDestroyed(EVENT_CALLBACK_TYPE type, IntPtr instance, IntPtr parameters)
        {
            if ((type & EVENT_CALLBACK_TYPE.DESTROYED) == 0) return RESULT.OK;
            ulong h = (ulong)instance;
            if (h == 0 || h > uint.MaxValue) return RESULT.OK;
            var table = Table((uint)h, make: true);
            int bit = Bit((uint)h);
            Interlocked.Or(ref *(long*)&table[bit >> 6], (long)(1UL << bit));
            Interlocked.Increment(ref destroyedCount);
            if (verify) lock (verifyDead) verifyDead.Add((uint)h);
            return RESULT.OK;
        }

        // ---- The patches. ----

        /// EventDescription.createInstance's prefix: the description's DESTROYED callback, set
        /// once before its first instance is made (so that instance carries it too).
        static void Creating(ref EventDescription __instance)
        {
            var key = __instance.handle;
            lock (descriptions)
            {
                if (descriptions.ContainsKey(key)) return;
                descriptions[key] = true;
            }
            setting = true;
            try { __instance.setCallback(destroyed, EVENT_CALLBACK_TYPE.DESTROYED); }
            finally { setting = false; }
        }

        /// Its postfix: the new instance's handle is alive (FMOD may have reused a dead one's).
        static void Created_(RESULT __result, ref EventInstance instance)
        {
            if (__result != RESULT.OK) return;
            ulong h = (ulong)instance.handle;
            if (h == 0 || h > uint.MaxValue) return;
            var table = Table((uint)h, make: true);
            int bit = Bit((uint)h);
            if ((table[bit >> 6] & (1UL << bit)) != 0)
                Interlocked.And(ref *(long*)&table[bit >> 6], ~(long)(1UL << bit));
            Interlocked.Increment(ref created);
            if (verify) lock (verifyDead) verifyDead.Remove((uint)h);
        }

        /// EventDescription.setCallback by anyone else: the description is theirs from now on.
        /// Theirs replaces ours for its instances to come, which are then never noted dead.
        static void DescriptionCallback(ref EventDescription __instance)
        {
            if (setting) return;
            lock (descriptions)
            {
                if (descriptions.TryGetValue(__instance.handle, out bool ours) && !ours) return;
                descriptions[__instance.handle] = false;
            }
            Interlocked.Increment(ref foreignDescriptions);
        }

        static bool IsValid(ref EventInstance __instance, ref bool __result)
        {
            if (!IsDead(__instance.handle)) return true;
            Skip(__instance.handle, Guarded.Length);
            __result = false;
            return false;
        }

        static bool Guard(ref EventInstance __instance, ref RESULT __result)
        {
            if (!IsDead(__instance.handle)) return true;
            Interlocked.Increment(ref skipped);
            __result = RESULT.ERR_INVALID_HANDLE;
            return false;
        }

        static bool GuardVerify(ref EventInstance __instance, ref RESULT __result, System.Reflection.MethodBase __originalMethod)
        {
            if (!IsDead(__instance.handle)) return true;
            Skip(__instance.handle, Array.IndexOf(Guarded, __originalMethod.Name));
            __result = RESULT.ERR_INVALID_HANDLE;
            return false;
        }

        static void Skip(IntPtr handle, int method)
        {
            Interlocked.Increment(ref skipped);
            if (!verify) return;
            Interlocked.Increment(ref skippedBy[method < 0 ? Guarded.Length : method]);
            uint h = (uint)(ulong)handle;
            bool recorded, first;
            lock (verifyDead)
            {
                recorded = verifyDead.Contains(h);
                first = verifyLogged.Add(h);
            }
            bool accepted = fmodIsValid!(handle);
            if (accepted) Interlocked.Increment(ref verifyAccepted);
            if (!recorded) Interlocked.Increment(ref verifyMismatch);
            if (first || accepted || !recorded)
            {
                var caller = new System.Diagnostics.StackTrace(3, false).GetFrame(0)?.GetMethod();
                EngineLog.Engine($"{Name}: skipped {(method < Guarded.Length ? Guarded[method] : "isValid")} on destroyed instance 0x{h:x8} (slot {(h >> 8) & 0x1FFF}, serial {h >> 21})"
                    + $" from {caller?.DeclaringType?.FullName}.{caller?.Name}; FMOD {(accepted ? "still resolves the handle: the call would have reached another object" : "would refuse it")}"
                    + (recorded ? "" : "; VERIFY FAILED: not in the record of destroyed handles"));
            }
        }

        // ---- Reports. ----

        /// Each frame: a line at most once a minute while calls are skipped.
        static void Tick()
        {
            long now = Environment.TickCount64;
            if (now < logAt) return;
            long s = Skipped;
            if (s == reportedSkipped) return;
            EngineLog.Engine($"{Name}: skipped {s - reportedSkipped:N0} call{(s - reportedSkipped == 1 ? "" : "s")} on destroyed event instances ({s:N0} so far)");
            reportedSkipped = s;
            logAt = now + 60_000;
        }

        static long windowCreated, windowDestroyed, windowSkipped;

        /// The frame-stats window's line.
        static string[] Report(double seconds, int frames)
        {
            long c = Created, d = Destroyed, s = Skipped;
            double perFrame(long a, long b) => frames > 0 ? (double)(a - b) / frames : 0;
            var line = $"{Name}: {perFrame(c, windowCreated):F1} instances made and {perFrame(d, windowDestroyed):F1} destroyed a frame, {perFrame(s, windowSkipped):F2} calls skipped a frame ({s:N0} so far)";
            if (verify)
            {
                line += "; by method:";
                for (int i = 0; i < skippedBy.Length; i++)
                {
                    long n = Interlocked.Read(ref skippedBy[i]);
                    if (n > 0) line += $" {(i < Guarded.Length ? Guarded[i] : "isValid")} {n:N0}";
                }
                line += $"; FMOD would have accepted {VerifyAccepted:N0}; record mismatches {VerifyMismatch:N0}";
            }
            (windowCreated, windowDestroyed, windowSkipped) = (c, d, s);
            return [line];
        }

        /// The end of the run: the totals.
        public static void Shutdown()
        {
            if (!Active) return;
            EngineLog.Engine($"{Name}: {Created:N0} event instances made, {Destroyed:N0} destroyed, {Skipped:N0} calls on destroyed ones skipped"
                + (foreignDescriptions > 0 ? $"; {foreignDescriptions} event description{(foreignDescriptions == 1 ? "" : "s")} had a callback of another's" : "")
                + (verify ? $"; FMOD would have accepted {VerifyAccepted:N0} of them, record mismatches {VerifyMismatch:N0}" : ""));
        }
    }
}
