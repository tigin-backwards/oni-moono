using System;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fix-endless-sounds` (Bug fixes, on by default; it changes what the game does,
    /// so it is left out of `exact`): two rows of the game's audio sheets play a looping FMOD
    /// event as a plain one-shot, so each play starts an instance that never ends.
    ///
    /// GameAudioSheets.Initialize turns each sheet row into anim events (AudioSheets.CreateSound
    /// with the row's type, or the sheet's default type when the row has none). Type `SoundEvent`
    /// makes a SoundEvent that starts and releases an FMOD instance each time its frame plays;
    /// `LoopingSoundEvent` makes the same SoundEvent with `looping` set, which starts the sound
    /// through the object's LoopingSounds (once while it plays) and stops it when the animation
    /// stops (AnimEventManager.StopEvents → SoundEvent.Stop). The rows fixed here, in U59 and U60:
    ///
    /// - `squirrel_kanim` `eat_loop` → `Squirrel_eat_LP` (SFXTags_Creatures, type `SoundEvent`):
    ///   the adult Pip's eating loop. The baby's row (`baby_squirrel_kanim` `eat_loop` →
    ///   `SquirrelBaby_eat_LP`) is a `LoopingSoundEvent`. An eating Pip on screen starts one never
    ///   ending instance per loop of the animation (about 3.8 a second at speed 3); at about 3,250
    ///   live instances FMOD's Studio thread passes 100% CPU and the game stalls and falls silent
    ///   (docs/research/vole-audio.md, "C: the Pip eat loop").
    /// - `rocket_debris_combined_kanim` `landed` → `RocketDebris_steaming_LP` (SFXTags_Buildings,
    ///   no type: the sheet's default, `SoundEvent`): a self-destructed rocket's debris payload
    ///   steaming in its crater, a looping animation (RailGunPayload's `grounded.crater`). The
    ///   railgun payload's row for the same state (`railgun_capsule_kanim` `landed` →
    ///   `RailGunCapsule_steaming_LP`) is a `LoopingSoundEvent`.
    ///
    /// Here those rows are built as `LoopingSoundEvent`, as their siblings are: the game's own
    /// constructor with `looping` set. Both objects have LoopingSounds (BaseSquirrelConfig,
    /// DebrisPayloadConfig). What differs from the game: the sound starts when the animation does
    /// and stops when it leaves it, as the sheet's author meant, instead of a new copy starting
    /// on every loop and none ever stopping; while the animation plays, one copy plays.
    ///
    /// Each row changes only where it is still what this was written for: a GameAudioSheets row of
    /// that file and animation naming that event, built as `SoundEvent`, whose FMOD event is not a
    /// one-shot. Otherwise (a game update fixed the row, renamed it, or made the event a one-shot)
    /// it is left as it is, with a line in Player.log.
    [GameMember(GameUse.Prefix, "System.Void AudioSheets::CreateSound(System.String,System.String,System.String,System.Single,System.String,System.Int32,System.String)")]
    [GameMember(GameUse.Postfix, "System.Void GameAudioSheets::Initialize()")]
    public static class EndlessLoopSounds
    {
        public const string Name = "fix-endless-sounds";

        /// The rows: animation file, animation, event name (as the sheet names it).
        static readonly (string File, string Anim, string Sound)[] rows =
        {
            ("squirrel_kanim", "eat_loop", "Squirrel_eat_LP"),
            ("rocket_debris_combined_kanim", "landed", "RocketDebris_steaming_LP"),
        };
        static readonly int[] fixedCount = new int[rows.Length];
        static readonly string[] leftOut = new string[rows.Length];

        public static void Install(Harmony harmony)
        {
            var create = AccessTools.Method(typeof(AudioSheets), "CreateSound",
                new[] { typeof(string), typeof(string), typeof(string), typeof(float), typeof(string), typeof(int), typeof(string) });
            var initialize = AccessTools.Method(typeof(GameAudioSheets), nameof(GameAudioSheets.Initialize));
            var names = create?.GetParameters();
            if (create == null || initialize == null || initialize.DeclaringType != typeof(GameAudioSheets)
                || names[0].Name != "file_name" || names[1].Name != "anim_name" || names[2].Name != "type" || names[4].Name != "sound_name")
            {
                global::Debug.Log($"{Name}: left out, this build's AudioSheets is not the one it fixes");
                return;
            }
            harmony.Patch(create, prefix: new HarmonyMethod(typeof(EndlessLoopSounds), nameof(CreateSound)) { priority = Priority.Last });
            harmony.Patch(initialize, postfix: new HarmonyMethod(typeof(EndlessLoopSounds), nameof(Initialized)));
        }

        static void CreateSound(AudioSheets __instance, string file_name, string anim_name, ref string type, string sound_name)
        {
            if (!(__instance is GameAudioSheets)) return;
            for (int i = 0; i < rows.Length; i++)
            {
                if (file_name != rows[i].File || anim_name != rows[i].Anim || sound_name != rows[i].Sound) continue;
                if (type != "SoundEvent")
                {
                    leftOut[i] = $"its type is {type}";
                    return;
                }
                string why = Looping(sound_name);
                if (why != null)
                {
                    leftOut[i] = why;
                    return;
                }
                type = "LoopingSoundEvent";
                fixedCount[i]++;
                return;
            }
        }

        /// Null when the event is one FMOD doesn't count as a one-shot (it loops or sustains),
        /// else why the row is left alone.
        static string Looping(string sound_name)
        {
            string path = GlobalAssets.GetSound(sound_name, force_no_warning: true);
            if (string.IsNullOrEmpty(path)) return "its event is not in the game's sound table";
            try
            {
                var description = FMODUnity.RuntimeManager.GetEventDescription(path);
                if (description.isOneshot(out bool oneshot) != FMOD.RESULT.OK) return "FMOD could not say whether its event loops";
                return oneshot ? "its event is a one-shot now" : null;
            }
            catch (Exception e)
            {
                return $"its event could not be read: {e.GetType().Name}";
            }
        }

        static void Initialized()
        {
            for (int i = 0; i < rows.Length; i++)
            {
                string row = $"{rows[i].File} {rows[i].Anim} {rows[i].Sound}";
                if (fixedCount[i] > 0) global::Debug.Log($"{Name}: {row} plays as a looping sound");
                else global::Debug.Log($"{Name}: left out for {row}, " + (leftOut[i] ?? "the audio sheets built no such row (or its DLC is off)"));
                fixedCount[i] = 0;
                leftOut[i] = null;
            }
        }
    }
}
