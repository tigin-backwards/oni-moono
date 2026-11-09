using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-anim-events` (on by default). Every frame AnimEventManager's loop visits
    /// each playing animation (U57: about 6,450), and for each reads its controller: whether it
    /// was destroyed, its frame index for the elapsed time (from its current animation and play
    /// mode), and its play speed. Those reads are a cache miss per controller and most of the
    /// loop's 2 ms; the loop's own entries are compact structs.
    ///
    /// The tweak runs the same loop, entry by entry in the same order with the same arithmetic,
    /// but takes those three inputs from a mirror kept per entry: the controller's current
    /// animation (frame count, total time, frame rate), play mode and speed, and whether it is
    /// destroyed. A mirror row is refreshed from the controller when its entry holds a different
    /// controller (a reference compare) or when the controller was flagged: every method that
    /// changes those inputs (StartQueuedAnim, Queue, the PlayMode setter, SwapAnims, OnDestroy)
    /// flags it, as does AnimEventManager.PlayAnim (an entry started anew, for a mod that sets
    /// the controller's animation by reflection and then restarts its events). Frame events,
    /// completions and everything else run as in the game. A mod that writes those fields
    /// directly and does not restart the events is read stale until the next flag.
    ///
    /// Stands down for the session (logged once) when another Harmony owner patches what the loop
    /// replaces or relies on (Guarded, which kinds count by KindsOf), checked at the first update
    /// in a game, when every mod's patches are made: the game's loop runs instead.
    ///
    /// With ONI_TWEAK_VERIFY=1 every entry's mirrored inputs are compared with the controller's
    /// own, and the controller's are used; differences are logged.
    [GameMember(GameUse.Postfix, "HandleVector`1/Handle<System.Int32> AnimEventManager::PlayAnim(KAnimControllerBase,KAnim/Anim,KAnim/PlayMode,System.Single,System.Boolean)")]
    [GameMember(GameUse.Uses, "System.Void AnimEventManager::PlayEvents(AnimEventManager/EventPlayerData)")]
    [GameMember(GameUse.Uses, "System.Void AnimEventManager::StopEvents(AnimEventManager/EventPlayerData)")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void AnimEventManager::Update(System.Single,System.Collections.Generic.List`1<AnimEventManager/AnimData>,System.Collections.Generic.List`1<AnimEventManager/EventPlayerData>)", "7ed564a03b1e771f linux-744825 win-744825", "da665f499501053d linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Postfix, "System.Void KAnimControllerBase::OnDestroy()")]
    [GameMember(GameUse.Postfix, "System.Void KAnimControllerBase::Queue(HashedString,KAnim/PlayMode,System.Single,System.Single)")]
    [GameMember(GameUse.Postfix, "System.Void KAnimControllerBase::StartQueuedAnim()")]
    [GameMember(GameUse.Postfix, "System.Void KAnimControllerBase::set_PlayMode(KAnim/PlayMode)")]
    [GameMember(GameUse.Postfix, "System.Void KBatchedAnimController::SwapAnims(KAnimFile[])")]
    public static class FastAnimEvents
    {
        public const string Name = "fast-anim-events";

        enum State { Unchecked, On, Off }
        static State state;

        /// For tests (E2eDriver's state reads): whether the loop is ours, why not, and verify's counts.
        public static bool Running => state == State.On;
        public static string OffReason { get; private set; }
        public static long Checks => checks;
        public static long Mismatches => wrong;

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Each watched controller's index into `stale`. Indices are never reused: a row may still
        /// hold a destroyed controller, whose index stays stale.
        static readonly Dictionary<KAnimControllerBase, int> watches = new Dictionary<KAnimControllerBase, int>();
        static bool[] stale = new bool[4096];
        static int nextWatch;
        /// Rows by the game's entry list; weak, as each colony's manager has lists of its own.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<AnimEventManager.EventPlayerData>, Row[]> mirrors =
            new System.Runtime.CompilerServices.ConditionalWeakTable<List<AnimEventManager.EventPlayerData>, Row[]>();
        static long checks, wrong, addedDuringLoop;

        /// A controller's inputs to the loop, as last read.
        struct Row
        {
            public KBatchedAnimController Controller;
            public int Watch;
            public bool Destroyed, HasAnim;
            public KAnim.PlayMode Mode;
            public int NumFrames;
            public float TotalTime, FrameRate, PlaySpeed;
        }

        public static void Install(Harmony harmony)
        {
            var flag = new HarmonyMethod(typeof(FastAnimEvents), nameof(Flag));
            harmony.Patch(AccessTools.Method(typeof(KAnimControllerBase), "StartQueuedAnim"), postfix: flag);
            harmony.Patch(AccessTools.Method(typeof(KAnimControllerBase), nameof(KAnimControllerBase.Queue)), postfix: flag);
            harmony.Patch(AccessTools.PropertySetter(typeof(KAnimControllerBase), nameof(KAnimControllerBase.PlayMode)), postfix: flag);
            harmony.Patch(AccessTools.Method(typeof(KBatchedAnimController), nameof(KBatchedAnimController.SwapAnims)), postfix: flag);
            harmony.Patch(AccessTools.Method(typeof(KAnimControllerBase), "OnDestroy"), postfix: new HarmonyMethod(typeof(FastAnimEvents), nameof(Destroyed)));
            harmony.Patch(AccessTools.Method(typeof(AnimEventManager), nameof(AnimEventManager.PlayAnim)), postfix: new HarmonyMethod(typeof(FastAnimEvents), nameof(Started)));
            harmony.Patch(LoopUpdate(), prefix: new HarmonyMethod(typeof(FastAnimEvents), nameof(Update)));
        }

        static MethodBase LoopUpdate() =>
            AccessTools.Method(typeof(AnimEventManager), "Update", new[] { typeof(float), typeof(List<AnimEventManager.AnimData>), typeof(List<AnimEventManager.EventPlayerData>) });

        // ---- Standing down ----

        /// The methods another owner's patch makes this tweak stand down for: the loop and its
        /// caller, what the loop copies or caches, the entries' other writers, and the methods whose
        /// flags keep the mirror current.
        internal static IEnumerable<MethodBase> Guarded()
        {
            var aem = typeof(AnimEventManager);
            var handle = typeof(HandleVector<int>.Handle);
            yield return LoopUpdate();
            yield return AccessTools.Method(aem, nameof(AnimEventManager.Update), Type.EmptyTypes);
            yield return AccessTools.Method(aem, "PlayEvents");
            yield return AccessTools.Method(aem, "StopEvents");
            yield return AccessTools.Method(aem, nameof(AnimEventManager.GetElapsedTime), new[] { handle });
            yield return AccessTools.Method(aem, nameof(AnimEventManager.SetElapsedTime), new[] { handle, typeof(float) });
            yield return AccessTools.Method(aem, nameof(AnimEventManager.PlayAnim));
            yield return AccessTools.Method(aem, nameof(AnimEventManager.StopAnim));
            yield return AccessTools.Method(aem, nameof(AnimEventManager.SetMode));
            var controller = typeof(KAnimControllerBase);
            yield return AccessTools.Method(controller, "StartQueuedAnim");
            yield return AccessTools.Method(controller, nameof(KAnimControllerBase.Queue));
            yield return AccessTools.PropertySetter(controller, nameof(KAnimControllerBase.PlayMode));
            yield return AccessTools.Method(typeof(KBatchedAnimController), nameof(KBatchedAnimController.SwapAnims));
            yield return AccessTools.Method(controller, "OnDestroy");
            yield return AccessTools.PropertyGetter(controller, nameof(KAnimControllerBase.CurrentAnim));
            yield return AccessTools.Method(controller, nameof(KAnimControllerBase.GetPlaySpeed));
            foreach (var m in AccessTools.GetDeclaredMethods(controller))
                if (m.Name == nameof(KAnimControllerBase.GetFrameIdx)) yield return m;
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(KAnim.Anim)))
                if (m.Name == nameof(KAnim.Anim.GetFrameIdx)) yield return m;
        }

        /// Which patches of a guarded method count (ProbeRepairPolicy.GuardedKinds): every kind on
        /// what the loop copies (PlayEvents, StopEvents, the frame index) or caches (the play speed,
        /// the current animation), where another owner's postfix would not run or its answer would
        /// be kept; only finalizers on the methods whose flag postfix keeps the mirror current, as
        /// the flag runs after anything else they do and the mirror reads it at the next update;
        /// all but postfixes on the rest (the loop, its caller and the entries' other writers).
        internal static ProbeRepairPolicy.GuardedKinds KindsOf(MethodBase m) => m.Name switch
        {
            "PlayEvents" or "StopEvents" or "GetFrameIdx" or "GetPlaySpeed" or "get_CurrentAnim" => ProbeRepairPolicy.GuardedKinds.All,
            "StartQueuedAnim" or "Queue" or "set_PlayMode" or "SwapAnims" or "OnDestroy" or "PlayAnim" => ProbeRepairPolicy.GuardedKinds.Finalizers,
            _ => ProbeRepairPolicy.GuardedKinds.AllButPostfixes,
        };

        static IEnumerable<string> Owners(IEnumerable<Patch> patches)
        {
            foreach (var p in patches) yield return p.owner;
        }

        static string Foreign()
        {
            var owners = new List<KeyValuePair<string, IEnumerable<string>>>();
            foreach (var m in Guarded())
            {
                if (m == null) return "a method it relies on was not found";
                var info = Harmony.GetPatchInfo(m);
                if (info == null) continue;
                var counted = ProbeRepairPolicy.Counted(KindsOf(m), Owners(info.Prefixes), Owners(info.Postfixes), Owners(info.Transpilers), Owners(info.Finalizers),
                    System.Linq.Enumerable.Concat(Owners(info.InnerPrefixes), Owners(info.InnerPostfixes)));
                owners.Add(new KeyValuePair<string, IEnumerable<string>>($"{m.DeclaringType?.Name}.{m.Name}", counted));
            }
            return ProbeRepairPolicy.Foreign(owners);
        }

        /// Once, at the first update in a game (every mod has patched by then; the main menu's few
        /// animations run the game's loop until then).
        static void Check()
        {
            string why;
            try { why = Foreign(); }
            catch (Exception e) { why = "its methods could not be read: " + e.Message; }
            if (why != null)
            {
                state = State.Off;
                OffReason = why;
                global::Debug.Log($"{Name}: off ({why}); the game's loop stays");
                return;
            }
            state = State.On;
            global::Debug.Log($"{Name}: on");
        }

        // ---- The flags (no-ops unless the loop is ours) ----

        static void Flag(KAnimControllerBase __instance)
        {
            if (state == State.On && watches.TryGetValue(__instance, out int watch)) stale[watch] = true;
        }

        /// AnimEventManager.PlayAnim: the controller's entry was (re)started.
        static void Started(KAnimControllerBase controller)
        {
            if (state == State.On && controller is not null && watches.TryGetValue(controller, out int watch)) stale[watch] = true;
        }

        static void Destroyed(KAnimControllerBase __instance)
        {
            if (state != State.On || !watches.TryGetValue(__instance, out int watch)) return;
            watches.Remove(__instance);
            stale[watch] = true; // rows holding it re-read it (and find it destroyed)
        }

        /// AnimEventManager.Update(dt, anim_data, event_data), the game's loop with mirrored inputs.
        static bool Update(AnimEventManager __instance, float dt, List<AnimEventManager.AnimData> anim_data, List<AnimEventManager.EventPlayerData> event_data)
        {
            if (state != State.On)
            {
                if (state == State.Off || Game.Instance == null) return true;
                Check();
                if (state != State.On) return true;
            }
            if (dt <= 0f) return false;
            mirrors.TryGetValue(event_data, out var rows);
            var finished = __instance.finishedCalls;
            int i = 0, startCount = event_data.Count;
            // The count is read on every pass, as the game's loop does: an event can start an
            // animation (a speech event its Duplicant's mouth), which appends an entry to visit
            // in this same update.
            for (; i < event_data.Count; i++)
            {
                if (rows == null || rows.Length <= i) rows = Grow(event_data, rows);
                var data = event_data[i];
                ref var row = ref rows[i];
                if (!ReferenceEquals(row.Controller, data.controller) || stale[row.Watch]) Refresh(ref row, data.controller);
                if (verify) Verify(ref row, data.controller);
                // controller == null, as Unity compares it
                if (row.Destroyed || data.mode == KAnim.PlayMode.Paused) continue;
                data.currentFrame = row.HasAnim ? FrameIdx(row.Mode, row.NumFrames, row.TotalTime, row.FrameRate, data.elapsedTime) : -1;
                event_data[i] = data;
                for (int e = 0; e < data.events.Count; e++) data.events[e].Play(data);
                data.previousFrame = data.currentFrame;
                // The speed as the game reads it, after the events: one of them may have started
                // another animation on this controller, which flags it.
                float speed = stale[row.Watch] ? data.controller.GetPlaySpeed() : row.PlaySpeed;
                if (verify && speed != data.controller.GetPlaySpeed())
                    global::Debug.Log($"{Name}: stale play speed after the events of {data.controller.name} ({++wrong})");
                data.elapsedTime += dt * speed;
                event_data[i] = data;
                if (data.updatingEvents != null)
                    for (int e = 0; e < data.updatingEvents.Count; e++) data.updatingEvents[e].OnUpdate(data);
                event_data[i] = data;
                if (data.mode != KAnim.PlayMode.Loop && data.currentFrame >= anim_data[i].numFrames - 1)
                {
                    for (int e = 0; e < data.events.Count; e++) data.events[e].Stop(data);
                    data.updatingEvents?.Clear();
                    finished.Add(data.controller);
                }
            }
            if (verify)
            {
                // Entries started by an event during this update, visited in it as the game does.
                if (event_data.Count > startCount) addedDuringLoop += event_data.Count - startCount;
            }
            return false;
        }

        static Row[] Grow(List<AnimEventManager.EventPlayerData> event_data, Row[] rows)
        {
            var grown = new Row[Math.Max(event_data.Count, Math.Max(16, (rows?.Length ?? 0) * 2))];
            if (rows != null) Array.Copy(rows, grown, rows.Length);
            mirrors.AddOrUpdate(event_data, grown);
            return grown;
        }

        static void Refresh(ref Row row, KBatchedAnimController controller)
        {
            row.Controller = controller;
            if (controller is null)
            {
                row.Destroyed = true;
                row.Watch = 0;
                return;
            }
            if (!watches.TryGetValue(controller, out int watch))
            {
                watch = ++nextWatch;
                if (watch >= stale.Length) Array.Resize(ref stale, stale.Length * 2);
                watches[controller] = watch;
            }
            stale[watch] = false;
            row.Watch = watch;
            row.Destroyed = controller == null;
            if (row.Destroyed) return;
            var anim = controller.CurrentAnim;
            row.HasAnim = anim != null;
            row.Mode = controller.mode;
            row.NumFrames = anim?.numFrames ?? 0;
            row.TotalTime = anim?.totalTime ?? 0f;
            row.FrameRate = anim?.frameRate ?? 0f;
            row.PlaySpeed = controller.GetPlaySpeed();
        }

        /// KAnim.Anim.GetFrameIdx(mode, elapsedSeconds), with the animation's numbers.
        static int FrameIdx(KAnim.PlayMode mode, int numFrames, float totalTime, float frameRate, float elapsedSeconds)
        {
            if (numFrames <= 0) return -1;
            int result = 0;
            if (mode == KAnim.PlayMode.Loop) elapsedSeconds %= totalTime;
            if (elapsedSeconds > 0f)
            {
                float num = 0.01f / frameRate;
                float num2 = elapsedSeconds * frameRate + num;
                result = Math.Min(numFrames - 1, (int)num2);
            }
            return result;
        }

        /// Test runs: the mirrored inputs against the controller's, and the controller's kept.
        static void Verify(ref Row row, KBatchedAnimController controller)
        {
            checks++;
            bool destroyed = controller == null;
            var anim = destroyed ? null : controller.CurrentAnim;
            bool same = row.Destroyed == destroyed
                        && (destroyed || (row.HasAnim == (anim != null) && row.Mode == controller.mode && row.PlaySpeed == controller.GetPlaySpeed()
                                          && row.NumFrames == (anim?.numFrames ?? 0) && row.TotalTime == (anim?.totalTime ?? 0f) && row.FrameRate == (anim?.frameRate ?? 0f)));
            if (!same)
            {
                global::Debug.Log($"{Name}: stale inputs for {(destroyed ? "a destroyed controller" : controller.name)} ({++wrong})");
                Refresh(ref row, controller);
            }
            if (checks % 2000000 == 0) global::Debug.Log($"{Name}: {checks} entries checked, {wrong} stale, {addedDuringLoop} added during the loop");
        }
    }
}
