using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Always on (not a Moono Tweaks row), a diagnostic for #91: Game.simData's five pointers into
    /// the sim's game data (SimTemperatureTransfer reads element temperatures through
    /// `elementChunks`) are assigned only in Game.StepTheSim, from the GameDataUpdate that
    /// PrepareGameData returns. SimDLL keeps two such buffers and swaps them at each
    /// PrepareGameData, so a pointer the game keeps past that can point at freed memory: #91's
    /// access violation in OnGetTemperature (temperature overlay). This notices when simData stops
    /// matching what the sim last returned, and says when and in what context. It changes nothing
    /// the game reads, and repairs nothing.
    ///
    /// - **Record.** A postfix of StepTheSim (Priority.Last) takes the five pointers from the
    ///   method's own result, what PrepareGameData returned, with numElementChunkInfos. That is
    ///   free (Harmony hands the postfix the result) and exact: it is the struct the game assigns
    ///   simData from, whereas Sim.HandleMessage also returns PrepareGameData to worldgen, and
    ///   simData itself could already be what changed. A prefix
    ///   (Priority.First) and a finalizer (void, so Harmony rethrows the exception with its stack)
    ///   note whether a step started this frame, and what it threw.
    /// - **Check.** Before Game.Update (the game's update, which steps the sim) and before
    ///   Infrared.Update (the temperature and germ overlays, whose visualizers read simData), five
    ///   pointer compares; a frame counts once. No allocation unless there is a line to write.
    /// - **Context.** Prefixes of SaveLoader.Save and Sim.HandleMessage note saves and the sim's
    ///   cell messages (AllocateCells, the SimData_ and ClearUnoccupiedCells ones, after which the
    ///   sim's buffers can move), and a PrepareGameData from outside StepTheSim while a colony
    ///   runs (a swap of the buffers simData would not follow; logged once with its stack).
    /// - **Log.** The first mismatch of the process writes one line (SimDataCheckCore.FirstLine);
    ///   after it, mismatched frames are counted, written every 10 minutes, when another colony
    ///   starts and at the quit, when there are new ones. Player.log is written as each line is
    ///   logged, so the line survives a crash right after it.
    /// - **Cost.** About 0.6 µs a check and 1 µs a step on a late-game colony (Linux lane, release;
    ///   mostly cache misses on the objects read), nothing allocated after the first check.
    /// - **Stand-down.** Off for the session, with one line, when another Harmony owner patches
    ///   StepTheSim with a prefix, transpiler or finalizer (it could then step the sim or assign
    ///   simData elsewhere), checked at each new colony and every 4,096 frames; or when a check
    ///   throws.
    ///
    /// Test switch: with ONI_TWEAK_VERIFY=1, ONI_TEST_SIMDATA_SKIP=N puts the previous step's
    /// pointers back into simData after the Nth step (N ≥ 2), as if StepTheSim had skipped its
    /// assignment once, so the next check finds a mismatch.
    [GameMember(GameUse.Prefix | GameUse.Postfix | GameUse.Finalizer, "Sim/GameDataUpdate* Game::StepTheSim(System.Single)")]
    [GameMember(GameUse.Prefix, "System.Void Game::Update()")]
    [GameMember(GameUse.Prefix, "System.Void Infrared::Update()")]
    [GameMember(GameUse.Prefix | GameUse.Finalizer, "System.String SaveLoader::Save(System.String,System.Boolean,System.Boolean)")]
    [GameMember(GameUse.Prefix, "System.IntPtr Sim::HandleMessage(SimMessageHashes,System.Int32,System.Byte[])")]
    [GameMember(GameUse.Uses, "SimData Game::simData")]
    [GameMember(GameUse.Uses, "Sim/EmittedMassInfo* SimData::emittedMassEntries")]
    [GameMember(GameUse.Uses, "Sim/ElementChunkInfo* SimData::elementChunks")]
    [GameMember(GameUse.Uses, "Sim/BuildingTemperatureInfo* SimData::buildingTemperatures")]
    [GameMember(GameUse.Uses, "Sim/DiseaseEmittedInfo* SimData::diseaseEmittedInfos")]
    [GameMember(GameUse.Uses, "Sim/DiseaseConsumedInfo* SimData::diseaseConsumedInfos")]
    [GameMember(GameUse.Uses, "Timelapser Game::timelapser")]
    [GameMember(GameUse.Uses, "System.Boolean Timelapser::screenshotPending")]
    [GameMember(GameUse.Uses, "System.Boolean Timelapser::screenshotActive")]
    public static class SimDataCheck
    {
        public const string Name = "sim-data-check";

        static readonly SimDataCheckCore core = new SimDataCheckCore();
        static MethodBase stepTheSim;
        static bool off, checkForeign, warnedOutside;
        static int frames;
        static int skipAt;

        /// For tests (E2eDriver's state reads): whether it stood down, and the counts.
        public static bool Off => off;
        public static long MismatchFrames => core.MismatchFrames;
        public static long Steps => core.Steps;

        public static void Install(Harmony harmony)
        {
            stepTheSim = AccessTools.Method(typeof(Game), "StepTheSim") ?? throw new MissingMethodException("Game", "StepTheSim");
            if (Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1"
                && int.TryParse(Environment.GetEnvironmentVariable("ONI_TEST_SIMDATA_SKIP"), out int n))
                skipAt = Math.Max(2, n);
            harmony.Patch(stepTheSim,
                prefix: new HarmonyMethod(typeof(SimDataCheck), nameof(StepPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(SimDataCheck), nameof(StepPostfix)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(SimDataCheck), nameof(StepFinalizer)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Game), "Update"), prefix: new HarmonyMethod(typeof(SimDataCheck), nameof(GameUpdatePrefix)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Infrared), "Update"), prefix: new HarmonyMethod(typeof(SimDataCheck), nameof(InfraredPrefix)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.Save), new[] { typeof(string), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(SimDataCheck), nameof(SavePrefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(SimDataCheck), nameof(SaveFinalizer)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Sim), nameof(Sim.HandleMessage), new[] { typeof(SimMessageHashes), typeof(int), typeof(byte[]) }),
                prefix: new HarmonyMethod(typeof(SimDataCheck), nameof(HandleMessagePrefix)) { priority = Priority.First });
            UnityEngine.Application.quitting += AtQuit;
        }

        /// The player's quit (it ends the process without ProcessExit handlers).
        static void AtQuit()
        {
            try
            {
                if (core.AtExit(Frame) is { } line) global::Debug.LogWarning(line);
            }
            catch (Exception)
            {
                // Nothing to add to a quit.
            }
        }

        static int Frame => UnityEngine.Time.frameCount;

        static void StepPrefix()
        {
            if (!off) core.StepStarted(Frame);
        }

        static unsafe void StepPostfix(Game __instance, Sim.GameDataUpdate* __result)
        {
            if (off) return;
            var got = default(SimDataPointers);
            int chunks = 0;
            if (__result != null)
            {
                got.EmittedMassEntries = (IntPtr)__result->emittedMassEntries;
                got.ElementChunks = (IntPtr)__result->elementChunkInfos;
                got.BuildingTemperatures = (IntPtr)__result->buildingTemperatures;
                got.DiseaseEmittedInfos = (IntPtr)__result->diseaseEmittedInfos;
                got.DiseaseConsumedInfos = (IntPtr)__result->diseaseConsumedInfos;
                chunks = __result->numElementChunkInfos;
            }
            // The test switch: what the step before returned, put back after this step's assignment.
            var previous = core.Expected;
            core.Recorded(__instance, (IntPtr)__result, chunks, got, Frame, Stopwatch.GetTimestamp());
            if (skipAt > 0 && __result != null && core.Steps >= skipAt && previous.ElementChunks != IntPtr.Zero && !previous.SameAs(got))
            {
                skipAt = 0;
                Write(__instance.simData, previous);
                global::Debug.Log($"[Moono] {Name}: test switch: simData keeps the previous step's pointers after the step at frame {Frame}");
            }
        }

        static void StepFinalizer(Exception __exception)
        {
            if (off) return;
            core.StepEnded(__exception, Frame);
        }

        static void GameUpdatePrefix() => Check("Game.Update", true);

        static void InfraredPrefix() => Check("Infrared.Update", false);

        static void Check(string where, bool frameStart)
        {
            if (off) return;
            try
            {
                int frame = Frame;
                var game = Game.Instance;
                if (frameStart)
                {
                    if (core.GameChanged(game, frame, out bool changed) is { } ended) global::Debug.LogWarning(ended);
                    if (changed) checkForeign = true;
                    if (checkForeign || (++frames & 0xFFF) == 0)
                    {
                        checkForeign = false;
                        if (ForeignPatches.Find(stepTheSim, ProbeRepairPolicy.GuardedKinds.AllButPostfixes) is { } why)
                        {
                            StandDown(why);
                            return;
                        }
                    }
                    if (core.Periodic(frame, Stopwatch.GetTimestamp(), Stopwatch.Frequency) is { } more) global::Debug.LogWarning(more);
                }
                if (game == null) return;
                var data = game.simData;
                if (data == null) return;
                var now = Read(data);
                if (core.Check(game, now, frame) == SimDataCheckCore.Verdict.First)
                    global::Debug.LogWarning(core.FirstLine(Context(where, frame), now, Stopwatch.GetTimestamp(), Stopwatch.Frequency));
            }
            catch (Exception e)
            {
                StandDown("a check threw " + e);
            }
        }

        static void StandDown(string why)
        {
            off = true;
            global::Debug.Log($"[Moono] {Name}: off ({why})");
        }

        static unsafe SimDataPointers Read(SimData data) => new SimDataPointers
        {
            EmittedMassEntries = (IntPtr)data.emittedMassEntries,
            ElementChunks = (IntPtr)data.elementChunks,
            BuildingTemperatures = (IntPtr)data.buildingTemperatures,
            DiseaseEmittedInfos = (IntPtr)data.diseaseEmittedInfos,
            DiseaseConsumedInfos = (IntPtr)data.diseaseConsumedInfos,
        };

        static unsafe void Write(SimData data, SimDataPointers p)
        {
            data.emittedMassEntries = (Sim.EmittedMassInfo*)p.EmittedMassEntries;
            data.elementChunks = (Sim.ElementChunkInfo*)p.ElementChunks;
            data.buildingTemperatures = (Sim.BuildingTemperatureInfo*)p.BuildingTemperatures;
            data.diseaseEmittedInfos = (Sim.DiseaseEmittedInfo*)p.DiseaseEmittedInfos;
            data.diseaseConsumedInfos = (Sim.DiseaseConsumedInfo*)p.DiseaseConsumedInfos;
        }

        /// What the game was doing; each part read on its own, so one torn-down singleton leaves
        /// only its part unknown.
        static SimDataContext Context(string where, int frame)
        {
            var c = new SimDataContext { Where = where, Frame = frame, GameTime = -1, Cycle = -1, Paused = -1, ActiveWorld = -1, TimelapsePending = -1, TimelapseRendering = -1 };
            try
            {
                var clock = GameClock.Instance;
                if (clock != null)
                {
                    c.GameTime = clock.GetTime();
                    c.Cycle = clock.GetCycle();
                }
            }
            catch (Exception) { }
            try
            {
                var speed = SpeedControlScreen.Instance;
                if (speed != null) c.Paused = speed.IsPaused ? 1 : 0;
            }
            catch (Exception) { }
            try
            {
                var cluster = ClusterManager.Instance;
                if (cluster != null) c.ActiveWorld = cluster.activeWorldId;
            }
            catch (Exception) { }
            try
            {
                var timelapser = Game.Instance != null ? Game.Instance.timelapser : null;
                if (timelapser != null)
                {
                    c.TimelapsePending = timelapser.screenshotPending ? 1 : 0;
                    c.TimelapseRendering = timelapser.screenshotActive ? 1 : 0;
                }
            }
            catch (Exception) { }
            return c;
        }

        static void SavePrefix()
        {
            if (!off) core.SaveStarted(Frame);
        }

        static void SaveFinalizer()
        {
            if (!off) core.SaveEnded(Frame);
        }

        static void HandleMessagePrefix(SimMessageHashes sim_msg_id)
        {
            if (off) return;
            string cells = sim_msg_id switch
            {
                SimMessageHashes.AllocateCells => "AllocateCells",
                SimMessageHashes.ClearUnoccupiedCells => "ClearUnoccupiedCells",
                SimMessageHashes.SimData_InitializeFromCells => "SimData_InitializeFromCells",
                SimMessageHashes.SimData_ResizeAndInitializeVacuumCells => "SimData_ResizeAndInitializeVacuumCells",
                SimMessageHashes.SimData_FreeCells => "SimData_FreeCells",
                _ => null,
            };
            if (cells != null)
            {
                core.CellsMessage(cells, Frame);
                return;
            }
            if (sim_msg_id != SimMessageHashes.PrepareGameData || core.InStep || Game.Instance == null) return;
            if (core.OutsidePrepare(Frame) && !warnedOutside)
            {
                warnedOutside = true;
                global::Debug.LogWarning($"[Moono] {Name}: PrepareGameData sent outside Game.StepTheSim at frame {Frame}, while a colony runs (the sim swaps its game data buffers; simData keeps the old ones): {new StackTrace(1, false)}");
            }
        }
    }
}
