using System;
using System.Globalization;
using System.Text;

namespace OniMoono.GameTweaks
{
    /// Game.simData's five pointers, in the order the game assigns them (Game.StepTheSim).
    internal struct SimDataPointers
    {
        public IntPtr EmittedMassEntries, ElementChunks, BuildingTemperatures, DiseaseEmittedInfos, DiseaseConsumedInfos;

        public bool SameAs(in SimDataPointers other) =>
            EmittedMassEntries == other.EmittedMassEntries && ElementChunks == other.ElementChunks
            && BuildingTemperatures == other.BuildingTemperatures && DiseaseEmittedInfos == other.DiseaseEmittedInfos
            && DiseaseConsumedInfos == other.DiseaseConsumedInfos;
    }

    /// What the game was doing when a check found a mismatch (filled by SimDataCheck from the
    /// game's singletons; anything it could not read is left as is).
    internal struct SimDataContext
    {
        public string Where;
        public int Frame;
        public double GameTime;
        public int Cycle;
        /// 1 paused, 0 running, -1 unknown.
        public int Paused;
        public int ActiveWorld;
        /// 1 yes, 0 no, -1 unknown.
        public int TimelapsePending, TimelapseRendering;
    }

    /// The sim-data check (SimDataCheck, #91) without the game's types, for the unit tests:
    /// what the last completed Game.StepTheSim got from the sim, how the steps and saves went,
    /// and the checks of Game.simData against it, with the log lines. A check allocates nothing
    /// unless it has a line to write.
    internal sealed class SimDataCheckCore
    {
        /// Wall time between two "more mismatches" lines.
        public const double ReportEverySeconds = 600;

        // What the last completed step got (Recorded), and for which Game.
        object game, seen;
        SimDataPointers expected;
        IntPtr result;
        int numElementChunkInfos;
        int gameSinceFrame = -1;

        // The steps.
        bool inStep;
        int stepStartFrame = -1, stepEndFrame = -1, stepThrewFrame = -1;
        long stepEndTicks;
        string threwType, threwMessage;
        public long Steps;

        // Saves (SaveLoader.Save), and the sim's cell messages (Sim.HandleMessage).
        bool inSave;
        int saveStartFrame = -1, saveEndFrame = -1;
        string cellsMessage;
        int cellsMessageFrame = -1;
        public long OutsidePrepares;
        int outsidePrepareFrame = -1;

        // Mismatches: frames found mismatched, and episodes (runs of them).
        public long MismatchFrames, Episodes;
        int lastMismatchFrame = int.MinValue, firstMismatchFrame = -1;
        long reportedFrames, reportedEpisodes, lastReportTicks;
        bool lastMatched = true;

        public bool InStep => inStep;

        /// What the last completed step got.
        public SimDataPointers Expected => expected;

        public void StepStarted(int frame)
        {
            inStep = true;
            stepStartFrame = frame;
        }

        /// A step returned normally with `result` (null: the sim gave nothing, the game assigned
        /// nothing, so what was expected still is).
        public void Recorded(object game, IntPtr result, int numElementChunkInfos, in SimDataPointers got, int frame, long ticks)
        {
            Steps++;
            if (result == IntPtr.Zero) return;
            if (!ReferenceEquals(game, this.game)) gameSinceFrame = frame;
            this.game = game;
            this.result = result;
            this.numElementChunkInfos = numElementChunkInfos;
            expected = got;
            stepEndFrame = frame;
            stepEndTicks = ticks;
        }

        /// The step ended, normally or with `thrown` (StepTheSim's finalizer).
        public void StepEnded(Exception thrown, int frame)
        {
            inStep = false;
            if (thrown == null) return;
            stepThrewFrame = frame;
            threwType = thrown.GetType().FullName;
            threwMessage = thrown.Message;
        }

        public void SaveStarted(int frame)
        {
            inSave = true;
            saveStartFrame = frame;
        }

        public void SaveEnded(int frame)
        {
            inSave = false;
            saveEndFrame = frame;
        }

        /// One of the sim's cell messages (SimData_InitializeFromCells and the like).
        public void CellsMessage(string message, int frame)
        {
            cellsMessage = message;
            cellsMessageFrame = frame;
        }

        /// A PrepareGameData outside StepTheSim while a colony runs; true for the first.
        public bool OutsidePrepare(int frame)
        {
            outsidePrepareFrame = frame;
            return ++OutsidePrepares == 1;
        }

        /// What the check of `current` (Game.Instance, null for none) found.
        public enum Verdict { Match, NoExpectation, First, Counted }

        /// Compares Game.simData's pointers with the last step's. A frame counts once however
        /// many checks it has. `First` the first mismatch of the session: write FirstLine.
        public Verdict Check(object current, in SimDataPointers now, int frame)
        {
            if (current == null || !ReferenceEquals(current, game)) return Verdict.NoExpectation;
            if (now.SameAs(expected))
            {
                lastMatched = true;
                return Verdict.Match;
            }
            if (frame == lastMismatchFrame) return Verdict.Counted;
            lastMismatchFrame = frame;
            MismatchFrames++;
            if (lastMatched) Episodes++;
            lastMatched = false;
            if (firstMismatchFrame >= 0) return Verdict.Counted;
            firstMismatchFrame = frame;
            reportedFrames = MismatchFrames;
            reportedEpisodes = Episodes;
            return Verdict.First;
        }

        /// At each frame's first check: whether the colony's Game is another than the last frame's
        /// (`changed`, also for the first one seen), and then the mismatches of the colony before
        /// since the last line (null when none). What was expected of a Game that is no longer
        /// current is forgotten; a new Game's first step may have come already (Game.OnPrefabInit).
        public string GameChanged(object current, int frame, out bool changed)
        {
            changed = !ReferenceEquals(current, seen);
            if (!changed) return null;
            bool had = seen != null;
            seen = current;
            if (!ReferenceEquals(game, current))
            {
                game = null;
                expected = default;
            }
            lastMatched = true;
            return had ? Pending(frame, "in the colony before this one") : null;
        }

        /// Every ReportEverySeconds of wall time: the mismatches since the last line, or null.
        public string Periodic(int frame, long ticks, long ticksPerSecond)
        {
            if (lastReportTicks == 0) lastReportTicks = ticks;
            if (ticks - lastReportTicks < (long)(ReportEverySeconds * ticksPerSecond)) return null;
            lastReportTicks = ticks;
            return Pending(frame, "in the last 10 minutes");
        }

        /// At the player's quit: the mismatches not yet reported, or null.
        public string AtExit(int frame) => Pending(frame, "at the quit");

        string Pending(int frame, string when)
        {
            if (MismatchFrames == reportedFrames) return null;
            long frames = MismatchFrames - reportedFrames, episodes = Episodes - reportedEpisodes;
            reportedFrames = MismatchFrames;
            reportedEpisodes = Episodes;
            return FormattableString.Invariant(
                $"[Moono] sim-data-check: {frames} more frame(s) with Game.simData not what the sim last returned ({episodes} new run(s)), {when} (frame {frame}); {MismatchFrames} frame(s) in {Episodes} run(s) since the first, at frame {firstMismatchFrame}");
        }

        static string Hex(IntPtr p) => "0x" + p.ToInt64().ToString("X", CultureInfo.InvariantCulture);

        static string YesNo(int value) => value < 0 ? "?" : value > 0 ? "yes" : "no";

        static string Ago(int then, int now) => then < 0 ? "never" : FormattableString.Invariant($"frame {then} ({now - then} frames ago)");

        /// The line for the first mismatch of the session.
        public string FirstLine(in SimDataContext context, in SimDataPointers now, long ticks, long ticksPerSecond)
        {
            var b = new StringBuilder(1024);
            var inv = CultureInfo.InvariantCulture;
            int frame = context.Frame;
            b.Append("[Moono] sim-data-check: MISMATCH (#91): Game.simData is not what the last Game.StepTheSim got from the sim, at frame ")
                .Append(frame.ToString(inv)).Append(" (checked before ").Append(context.Where).Append(')');
            b.Append("; game time ").Append(context.GameTime.ToString("F1", inv)).Append(" s, cycle ").Append(context.Cycle.ToString(inv))
                .Append(", paused ").Append(YesNo(context.Paused)).Append(", active world ").Append(context.ActiveWorld.ToString(inv));
            b.Append("; last completed StepTheSim: ").Append(Ago(stepEndFrame, frame));
            if (stepEndFrame >= 0)
                b.Append(", ").Append(((ticks - stepEndTicks) * 1000.0 / ticksPerSecond).ToString("F1", inv)).Append(" ms ago");
            b.Append(", result ").Append(Hex(result)).Append(", numElementChunkInfos ").Append(numElementChunkInfos.ToString(inv));
            b.Append("; this frame StepTheSim ").Append(stepStartFrame == frame ? (inStep ? "started, not ended" : stepEndFrame == frame ? "completed" : stepThrewFrame == frame ? "threw" : "returned nothing") : "did not run");
            b.Append("; last StepTheSim that threw: ");
            if (stepThrewFrame < 0) b.Append("none");
            else b.Append(Ago(stepThrewFrame, frame)).Append(", ").Append(threwType).Append(": ").Append(threwMessage);
            b.Append("; steps ").Append(Steps.ToString(inv)).Append(", this colony's since frame ").Append(gameSinceFrame.ToString(inv));
            b.Append("; save ").Append(inSave ? "in progress since frame " + saveStartFrame.ToString(inv) : "not in progress").Append(", last finished ").Append(Ago(saveEndFrame, frame));
            b.Append("; timelapser: screenshot pending ").Append(YesNo(context.TimelapsePending)).Append(", rendering ").Append(YesNo(context.TimelapseRendering));
            b.Append("; last sim cells message: ");
            if (cellsMessageFrame < 0) b.Append("none");
            else b.Append(cellsMessage).Append(" at ").Append(Ago(cellsMessageFrame, frame));
            b.Append("; PrepareGameData outside StepTheSim: ").Append(OutsidePrepares.ToString(inv));
            if (outsidePrepareFrame >= 0) b.Append(", last at ").Append(Ago(outsidePrepareFrame, frame));
            b.Append("; simData now/expected:");
            Pair(b, "emittedMassEntries", now.EmittedMassEntries, expected.EmittedMassEntries);
            Pair(b, "elementChunks", now.ElementChunks, expected.ElementChunks);
            Pair(b, "buildingTemperatures", now.BuildingTemperatures, expected.BuildingTemperatures);
            Pair(b, "diseaseEmittedInfos", now.DiseaseEmittedInfos, expected.DiseaseEmittedInfos);
            Pair(b, "diseaseConsumedInfos", now.DiseaseConsumedInfos, expected.DiseaseConsumedInfos);
            b.Length--;
            b.Append(". Further mismatches are only counted.");
            return b.ToString();
        }

        static void Pair(StringBuilder b, string name, IntPtr now, IntPtr expected)
        {
            b.Append(' ').Append(name).Append(' ').Append(Hex(now));
            if (now != expected) b.Append("!=").Append(Hex(expected));
            else b.Append(" (same)");
            b.Append(',');
        }
    }
}
