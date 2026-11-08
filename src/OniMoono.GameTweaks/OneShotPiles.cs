using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fix-sound-piles` (Bug fixes, on by default; it changes what the game does, so
    /// it is left out of `exact`): of the copies of one sound that start in one cell in one frame,
    /// only the first PerCell play.
    ///
    /// Every positional one-shot the game plays goes through KFMOD.BeginOneShot(EventReference,
    /// Vector3, float), which makes an FMOD instance, places it and sets its volume (the caller
    /// then starts and releases it, KFMOD.EndOneShot). Critters piled in one cell play their
    /// landing sound together: 1,500 Shove Voles in one cell start about 2,900 instances a second
    /// of `Rock_creature_land`, an event Klei's bank caps at 5 instances that steals the quietest,
    /// so each start plays and stops another. FMOD's Studio thread can't keep up, its command queue
    /// fills and the game freezes in an FMOD call; the original freezes the same way (issue #51;
    /// docs/research/vole-audio.md).
    ///
    /// Here a one-shot over PerCell of the same event, in the same cell (the grid cell of the
    /// position the game passes, which is the sound's own on screen), in the same frame, is not
    /// made: BeginOneShot returns no instance, as it does when FMOD isn't up, and the callers
    /// that check for one go on as for a sound that played (SoundEvent.PlayOneShot reports it
    /// played, so the game logs no "invalid sound" warning for it). Only events FMOD counts as
    /// 3D one-shots are counted, played anywhere but the origin (where the game plays its UI,
    /// menu and screen sounds): looping, 2D and UI sounds are never skipped. What differs from
    /// the game: where that many copies start together, fewer play (a pile sounds quieter and
    /// thinner); and events that read the game's `objectCount` parameter (KFMOD.CreateInstance's
    /// parameter updaters) see the copies made, not the copies asked for. The first copies of
    /// each frame start when they would have.
    ///
    /// Player.log gets a line at most once a minute while it skips any, with how many and the
    /// event skipped most.
    [GameMember(GameUse.Prefix | GameUse.Replaces, "FMOD.Studio.EventInstance KFMOD::BeginOneShot(FMODUnity.EventReference,UnityEngine.Vector3,System.Single)", "580ac3fbe6cd7569 linux-744825 win-744825", "ebdf4f6651143526 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Boolean SoundEvent::PlayOneShot(FMODUnity.EventReference,UnityEngine.Vector3,System.Single)")]
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Boolean SoundEvent::PlayOneShot(System.String,AnimEventManager/EventPlayerData,EffectorValues,System.Single,System.Boolean)")]
    public static class OneShotPiles
    {
        public const string Name = "fix-sound-piles";

        /// How many copies of one event start in one cell in one frame. ONI_SOUND_PILE_LIMIT sets
        /// another (measurements).
        public static readonly int PerCell = Limit();

        static int Limit() => int.TryParse(Environment.GetEnvironmentVariable("ONI_SOUND_PILE_LIMIT"), out int n) && n > 0 ? n : 3;

        readonly struct Key : IEquatable<Key>
        {
            readonly FMOD.GUID guid;
            readonly int x, y;
            public Key(FMOD.GUID guid, int x, int y) { this.guid = guid; this.x = x; this.y = y; }
            public bool Equals(Key o) => guid.Equals(o.guid) && x == o.x && y == o.y;
            public override bool Equals(object o) => o is Key k && Equals(k);
            public override int GetHashCode() => ((guid.Data1 * 31 + guid.Data2) * 31 + guid.Data4) * 31 + x * 8191 + y;
        }

        /// Per event: its path when it is a 3D one-shot outside the UI bank, else null.
        static readonly Dictionary<FMOD.GUID, string> counted = new Dictionary<FMOD.GUID, string>();
        static readonly Dictionary<Key, int> started = new Dictionary<Key, int>();
        static int frame = -1;
        /// Skips so far, for SoundEvent.PlayOneShot's postfix (main thread: the game's sounds start there).
        static int skips;
        /// The one-shots skipped this run (tests: E2eDriver's state reads).
        public static int Skipped => skips;
        static readonly Dictionary<string, int> skippedByEvent = new Dictionary<string, int>();
        static float logAt = -1f;

        public static void Install(Harmony harmony)
        {
            var begin = AccessTools.Method(typeof(KFMOD), nameof(KFMOD.BeginOneShot), new[] { typeof(FMODUnity.EventReference), typeof(Vector3), typeof(float) });
            var playAt = AccessTools.Method(typeof(SoundEvent), nameof(SoundEvent.PlayOneShot), new[] { typeof(FMODUnity.EventReference), typeof(Vector3), typeof(float) });
            var playFor = AccessTools.Method(typeof(SoundEvent), nameof(SoundEvent.PlayOneShot),
                new[] { typeof(string), typeof(AnimEventManager.EventPlayerData), typeof(EffectorValues), typeof(float), typeof(bool) });
            if (begin == null || begin.ReturnType != typeof(FMOD.Studio.EventInstance) || playAt?.ReturnType != typeof(bool) || playFor?.ReturnType != typeof(bool))
            {
                global::Debug.Log($"{Name}: left out, this build's KFMOD.BeginOneShot or SoundEvent.PlayOneShot is not the one it was written for");
                return;
            }
            harmony.Patch(begin, prefix: new HarmonyMethod(typeof(OneShotPiles), nameof(BeginOneShot)) { priority = Priority.First });
            foreach (var play in new[] { playAt, playFor })
                harmony.Patch(play, prefix: new HarmonyMethod(typeof(OneShotPiles), nameof(PlayPrefix)), postfix: new HarmonyMethod(typeof(OneShotPiles), nameof(PlayPostfix)));
        }

        static bool BeginOneShot(FMODUnity.EventReference event_ref, Vector3 position, ref FMOD.Studio.EventInstance __result)
        {
            if (event_ref.IsNull || position == Vector3.zero) return true;
            string path = Counted(event_ref.Guid);
            if (path == null) return true;
            int now = Time.frameCount;
            if (now != frame)
            {
                frame = now;
                started.Clear();
            }
            var key = new Key(event_ref.Guid, Mathf.FloorToInt(position.x), Mathf.FloorToInt(position.y));
            started.TryGetValue(key, out int n);
            if (n < PerCell)
            {
                started[key] = n + 1;
                return true;
            }
            skips++;
            skippedByEvent.TryGetValue(path, out int s);
            skippedByEvent[path] = s + 1;
            Report();
            __result = default;
            return false;
        }

        static string Counted(FMOD.GUID guid)
        {
            if (counted.TryGetValue(guid, out string path)) return path;
            path = null;
            try
            {
                var description = FMODUnity.RuntimeManager.GetEventDescription(guid);
                if (description.isOneshot(out bool oneshot) == FMOD.RESULT.OK && oneshot && description.is3D(out bool is3D) == FMOD.RESULT.OK && is3D
                    && description.getPath(out string p) == FMOD.RESULT.OK && p != null && !p.StartsWith("event:/UI/", StringComparison.OrdinalIgnoreCase))
                    path = p;
            }
            catch (Exception)
            {
                // Not an event FMOD knows: the game's own path fails it as it would.
            }
            counted[guid] = path;
            return path;
        }

        static void Report()
        {
            float t = Time.realtimeSinceStartup;
            if (logAt < 0f) logAt = t;
            if (t < logAt) return;
            int total = 0;
            string most = null;
            int mostCount = 0;
            foreach (var e in skippedByEvent)
            {
                total += e.Value;
                if (e.Value > mostCount) { most = e.Key; mostCount = e.Value; }
            }
            global::Debug.Log($"{Name}: skipped {total} one-shot(s) over {PerCell} of one event in one cell in one frame; most: {most} ({mostCount})");
            skippedByEvent.Clear();
            logAt = t + 60f;
        }

        static void PlayPrefix(out int __state) => __state = skips;

        static void PlayPostfix(int __state, ref bool __result)
        {
            if (!__result && skips != __state) __result = true;
        }
    }
}
