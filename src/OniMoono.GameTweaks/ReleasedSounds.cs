using System;
using System.Collections.Generic;
using System.Reflection;
using FMOD.Studio;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    using Sound = LoopingSoundManager.Sound;

    /// Game tweak `fix-released-sounds` (Bug fixes, on by default; left out of `exact`): the game
    /// clears its copy of an FMOD event instance's handle when it releases the instance, at the
    /// two places where it keeps the handle and goes on using it (docs/TWEAKS.md, "The released
    /// sounds fix"; issues #78, #72, #73 and #58).
    ///
    /// An FMOD Studio handle carries an 11-bit serial that goes up each time its slot is freed and
    /// wraps after 2,047. A handle kept after release() is refused by FMOD until then; once its
    /// slot has come round, it resolves to whatever object holds the slot now, and the game's call
    /// writes into it (EventInstance::stop's command on an object of another kind is the pair of
    /// writes behind #78's `fmod heap:` lines). In U59 and U60:
    ///
    /// - AmbienceManager.Layer.Stop stops and releases the layer's instance but keeps the handle,
    ///   and Quadrant.Update calls Stop on every looping layer not among the top three each
    ///   update: an isValid() on a released handle each time, and stop() and release() on
    ///   whatever it resolves to once it wraps. Here Stop runs as the game's, then clears the
    ///   handle (EventInstance.clearHandle, as the game's own SpeechMonitor and MainMenu do after
    ///   a release). isValid() is then false without asking FMOD, as it is for a layer never
    ///   started; Start makes a new instance as before.
    /// - LoopingSoundManager.StopAllSounds (a world switch, and Game.StopBE as a scene unloads)
    ///   stops and releases every playing loop but leaves it flagged as playing with its handle:
    ///   RenderEveryTick goes on setting its position, the parameter updaters its parameters, and
    ///   once it leaves the view it is stopped and released again. Here each playing loop is
    ///   stopped and released as the game does, taken off the parameter updaters as StopSound
    ///   does (they hold their own copy of the handle and find it by it), and its handle cleared.
    ///   It stays flagged as playing, as in the game: the calls the game goes on making on it
    ///   carry the null handle, which FMOD refuses (ERR_INVALID_HANDLE) whatever it has made since,
    ///   and the loop is restarted, stopped or freed just when the game's would be. Clearing the
    ///   flag too (as the fix sent to Klei does) restarts the loops in the frame of the switch,
    ///   while the sound culler still has the old view, and stops them a frame later.
    ///
    /// Nothing audible changes: every call that no longer reaches FMOD is one FMOD refused before
    /// the wrap, and nothing is started or stopped that the game would not have.
    ///
    /// With ONI_TWEAK_VERIFY=1 (or ONI_VERIFY_RELEASED_SOUNDS=1) the handles released here are
    /// kept, and any later call on one of FMODUnity's EventInstance methods the game uses is
    /// counted and logged with its caller (VerifyStaleCalls must stay 0), until FMOD hands out
    /// that handle again. The first call on a cleared loop logs FMOD's answer, and after each
    /// StopAllSounds its loops are followed for 300 frames: one that gets a new instance in that
    /// time before the game stopped it is logged (VerifyRestarted, 0 when the game's own timing
    /// holds; a loop the game stops out of view and starts again back in view does not count).
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void AmbienceManager/Layer::Stop()", "2f74965174a7ee20 linux-744825 win-744825", "15c1f2ba0af14d7b linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void LoopingSoundManager::StopAllSounds()", "db3b86cb9239e9a2 linux-744825 win-744825", "a1ba518f98eb9619 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Postfix, "System.Void LoopingSoundManager::RenderEveryTick(System.Single)")]
    [GameMember(GameUse.Postfix, "FMOD.RESULT FMOD.Studio.EventDescription::createInstance(FMOD.Studio.EventInstance&)")]
    [GameMember(GameUse.Prefix, "System.Boolean FMOD.Studio.EventInstance::isValid()")]
    [GameMember(GameUse.Prefix, "FMOD.RESULT FMOD.Studio.EventInstance::start()")]
    [GameMember(GameUse.Prefix, "FMOD.RESULT FMOD.Studio.EventInstance::stop(FMOD.Studio.STOP_MODE)")]
    [GameMember(GameUse.Prefix, "FMOD.RESULT FMOD.Studio.EventInstance::release()")]
    [GameMember(GameUse.Prefix, "FMOD.RESULT FMOD.Studio.EventInstance::setPaused(System.Boolean)")]
    [GameMember(GameUse.Prefix, "FMOD.RESULT FMOD.Studio.EventInstance::setVolume(System.Single)")]
    [GameMember(GameUse.Prefix, "FMOD.RESULT FMOD.Studio.EventInstance::set3DAttributes(FMOD.ATTRIBUTES_3D)")]
    [GameMember(GameUse.Prefix, "FMOD.RESULT FMOD.Studio.EventInstance::setParameterByID(FMOD.Studio.PARAMETER_ID,System.Single,System.Boolean)")]
    [GameMember(GameUse.Prefix, "FMOD.RESULT FMOD.Studio.EventInstance::setParameterByName(System.String,System.Single,System.Boolean)")]
    public static class ReleasedSounds
    {
        public const string Name = "fix-released-sounds";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1" || Environment.GetEnvironmentVariable("ONI_VERIFY_RELEASED_SOUNDS") == "1";

        /// For specs (`type:OniMoono.GameTweaks.ReleasedSounds::LoopsCleared`): ambience layers
        /// whose released handle was cleared, loops StopAllSounds cleared, parameter updater
        /// entries it asked to drop.
        public static long LayersCleared, LoopsCleared, UpdaterRemovals;
        /// Verify mode: calls on a handle released here (must stay 0); loops StopAllSounds cleared
        /// that got a new instance within 300 frames.
        public static long VerifyStaleCalls, VerifyRestarted;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(AmbienceManager.Layer), "Stop", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(ReleasedSounds), nameof(LayerStop)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(LoopingSoundManager), "StopAllSounds", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(ReleasedSounds), nameof(StopAllSounds)) { priority = Priority.Last });
            if (!verify) return;
            harmony.Patch(AccessTools.Method(typeof(LoopingSoundManager), "RenderEveryTick", [typeof(float)]),
                postfix: new HarmonyMethod(typeof(ReleasedSounds), nameof(Rendered)));
            harmony.Patch(AccessTools.Method(typeof(EventDescription), "createInstance", [typeof(EventInstance).MakeByRefType()]),
                postfix: new HarmonyMethod(typeof(ReleasedSounds), nameof(Created)));
            var called = new HarmonyMethod(typeof(ReleasedSounds), nameof(Called));
            var instance = typeof(EventInstance);
            harmony.Patch(AccessTools.Method(instance, "isValid", Type.EmptyTypes), prefix: called);
            harmony.Patch(AccessTools.Method(instance, "start", Type.EmptyTypes), prefix: called);
            harmony.Patch(AccessTools.Method(instance, "stop", [typeof(STOP_MODE)]), prefix: called);
            harmony.Patch(AccessTools.Method(instance, "release", Type.EmptyTypes), prefix: called);
            harmony.Patch(AccessTools.Method(instance, "setPaused", [typeof(bool)]), prefix: called);
            harmony.Patch(AccessTools.Method(instance, "setVolume", [typeof(float)]), prefix: called);
            harmony.Patch(AccessTools.Method(instance, "set3DAttributes", [typeof(FMOD.ATTRIBUTES_3D)]), prefix: called);
            harmony.Patch(AccessTools.Method(instance, "setParameterByID", [typeof(PARAMETER_ID), typeof(float), typeof(bool)]), prefix: called);
            harmony.Patch(AccessTools.Method(instance, "setParameterByName", [typeof(string), typeof(float), typeof(bool)]), prefix: called);
            global::Debug.Log($"{Name}: verify mode, calls on the handles released here are counted and logged");
        }

        /// AmbienceManager.Layer.Stop, then the handle cleared.
        static bool LayerStop(AmbienceManager.Layer __instance)
        {
            if (__instance.soundEvent.isValid())
            {
                __instance.soundEvent.stop(STOP_MODE.ALLOWFADEOUT);
                __instance.soundEvent.release();
                if (verify) Released(__instance.soundEvent.handle);
                __instance.soundEvent.clearHandle();
                LayersCleared++;
            }
            __instance.isRunning = false;
            return false;
        }

        /// LoopingSoundManager.StopAllSounds, then each loop taken off the parameter updaters (as
        /// StopSound does) and its handle cleared; it stays flagged as playing, as in the game.
        static bool StopAllSounds(LoopingSoundManager __instance)
        {
            var list = __instance.sounds.GetDataList();
            int cleared = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Sound data = list[i];
                if (!data.IsPlaying) continue;
                data.ev.stop(STOP_MODE.IMMEDIATE);
                data.ev.release();
                var description = KFMOD.GetSoundEventDescription(data.path);
                foreach (var parameter in description.parameters)
                {
                    if (!__instance.parameterUpdaters.TryGetValue(parameter.name, out var updater)) continue;
                    updater.Remove(new LoopingSoundParameterUpdater.Sound
                    {
                        ev = data.ev,
                        path = data.path,
                        description = description,
                        transform = data.transform,
                        objectIsSelectedAndVisible = false,
                    });
                    UpdaterRemovals++;
                }
                if (verify)
                {
                    Released(data.ev.handle);
                    Follow(data);
                }
                data.ev.clearHandle();
                list[i] = data;
                cleared++;
            }
            LoopsCleared += cleared;
            if (verify) global::Debug.Log($"{Name}: StopAllSounds cleared {cleared} playing loop{(cleared == 1 ? "" : "s")} ({LoopsCleared} so far)");
            return false;
        }

        // ---- Verify mode. ----

        static readonly HashSet<IntPtr> released = new();
        static readonly HashSet<string> loggedCallers = new();

        static void Released(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            lock (released) released.Add(handle);
        }

        /// EventDescription.createInstance's postfix: FMOD handed out this handle again.
        static void Created(FMOD.RESULT __result, ref EventInstance instance)
        {
            if (__result != FMOD.RESULT.OK) return;
            lock (released) released.Remove(instance.handle);
        }

        static void Called(ref EventInstance __instance, MethodBase __originalMethod)
        {
            var handle = __instance.handle;
            if (handle == IntPtr.Zero) return;
            lock (released)
            {
                if (released.Count == 0 || !released.Contains(handle)) return;
            }
            long n = System.Threading.Interlocked.Increment(ref VerifyStaleCalls);
            var caller = new System.Diagnostics.StackTrace(2, false).GetFrame(0)?.GetMethod();
            string where = $"{caller?.DeclaringType?.FullName}.{caller?.Name}";
            bool first;
            lock (loggedCallers) first = loggedCallers.Add(__originalMethod.Name + " " + where);
            if (first || n <= 20)
                global::Debug.LogWarning($"{Name}: VERIFY FAILED: {__originalMethod.Name} on released handle 0x{(ulong)handle:x8} from {where} ({n} so far)");
        }

        /// A loop StopAllSounds cleared: its object (or, for one without, its position) and event.
        readonly struct Key : IEquatable<Key>
        {
            readonly Transform transform;
            readonly HashedString path;
            readonly Vector3 pos;

            public Key(in Sound s)
            {
                transform = s.transform;
                path = s.path;
                pos = s.transform == null ? s.pos : default;
            }

            public bool Equals(Key o) => ReferenceEquals(transform, o.transform) && path == o.path && pos == o.pos;
            public override bool Equals(object o) => o is Key k && Equals(k);
            public override int GetHashCode() => (transform == null ? 0 : transform.GetHashCode()) * 31 + path.GetHashCode();
        }

        sealed class Followed
        {
            public bool Culled, Restarted, Stopped;
            public string Path;
        }

        static readonly Dictionary<Key, Followed> followed = new();
        static int followUntil, followFrom;
        static bool answerLogged;

        static void Follow(in Sound data)
        {
            if (Time.frameCount > followUntil) followed.Clear();
            followFrom = Time.frameCount;
            followUntil = Time.frameCount + 300;
            followed[new Key(data)] = new Followed { Culled = data.IsCullingEnabled, Path = KFMOD.GetSoundEventDescription(data.path).path };
        }

        /// LoopingSoundManager.RenderEveryTick's postfix: the cleared loops that the game stops (as
        /// out of view) or gives a new instance.
        static void Rendered(LoopingSoundManager __instance)
        {
            if (followed.Count == 0) return;
            int frame = Time.frameCount;
            foreach (var s in __instance.sounds.GetDataList())
            {
                if (!followed.TryGetValue(new Key(s), out var f)) continue;
                if (!answerLogged && s.IsPlaying && s.ev.handle == IntPtr.Zero)
                {
                    // The game's own next call on a cleared loop, made here once to log FMOD's answer.
                    answerLogged = true;
                    var ev = s.ev;
                    global::Debug.Log($"{Name}: a call on a cleared loop's handle: FMOD answers {ev.set3DAttributes(default)}");
                }
                if (!s.IsPlaying && !f.Stopped)
                {
                    f.Stopped = true;
                    global::Debug.Log($"{Name}: {f.Path} stopped by the game {frame - followFrom} frames after StopAllSounds ({(f.Culled ? "out of view" : "never culled")})");
                }
                // A loop the game stopped first (out of view) and started again later (back in
                // view) runs on the game's own timing: only a new instance before that counts.
                else if (s.IsPlaying && s.ev.handle != IntPtr.Zero && !f.Restarted && !f.Stopped)
                {
                    f.Restarted = true;
                    VerifyRestarted++;
                    global::Debug.Log($"{Name}: {f.Path} has a new instance {frame - followFrom} frames after StopAllSounds ({(f.Culled ? "culled when out of view" : "never culled")})");
                }
            }
            if (frame < followUntil) return;
            int restarted = 0, stopped = 0;
            foreach (var f in followed.Values)
            {
                if (f.Restarted) restarted++;
                if (f.Stopped) stopped++;
            }
            global::Debug.Log($"{Name}: of {followed.Count} loops StopAllSounds cleared, within 300 frames {stopped} were stopped by the game and {restarted} got a new instance");
            followed.Clear();
        }
    }
}
