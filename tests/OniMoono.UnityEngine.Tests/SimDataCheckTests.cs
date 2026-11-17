using System;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The sim-data-check diagnostic (#91; src/OniMoono.GameTweaks/SimDataCheckCore.cs, compiled
    /// in here): what a step records, what a check finds, and the lines it writes.
    public class SimDataCheckTests
    {
        const long PerSecond = 1000;

        static SimDataPointers Buffer(long b) => new SimDataPointers
        {
            EmittedMassEntries = (IntPtr)(b + 0x10),
            ElementChunks = (IntPtr)(b + 0x20),
            BuildingTemperatures = (IntPtr)(b + 0x30),
            DiseaseEmittedInfos = (IntPtr)(b + 0x40),
            DiseaseConsumedInfos = (IntPtr)(b + 0x50),
        };

        static SimDataContext Context(int frame) => new SimDataContext
        {
            Where = "Game.Update", Frame = frame, GameTime = 1234.5, Cycle = 3, Paused = 0, ActiveWorld = 2, TimelapsePending = 0, TimelapseRendering = 1,
        };

        static SimDataCheckCore Stepped(object game, SimDataPointers got, int frame)
        {
            var core = new SimDataCheckCore();
            core.StepStarted(frame);
            core.Recorded(game, (IntPtr)0x1000, 5124, got, frame, frame * 16L);
            core.StepEnded(null, frame);
            return core;
        }

        [Fact]
        public void TheLastStepsPointersMatch()
        {
            var game = new object();
            var core = Stepped(game, Buffer(0xA000), 10);
            Assert.Equal(SimDataCheckCore.Verdict.Match, core.Check(game, Buffer(0xA000), 11));
            // The next step returns the other buffer, and the game assigned it.
            core.Recorded(game, (IntPtr)0x2000, 5124, Buffer(0xB000), 12, 12 * 16L);
            Assert.Equal(SimDataCheckCore.Verdict.Match, core.Check(game, Buffer(0xB000), 13));
            Assert.Equal(0, core.MismatchFrames);
        }

        [Fact]
        public void NoExpectationBeforeAStepOrForAnotherGame()
        {
            var core = new SimDataCheckCore();
            var game = new object();
            Assert.Equal(SimDataCheckCore.Verdict.NoExpectation, core.Check(game, default, 1));
            core.Recorded(game, (IntPtr)0x1000, 1, Buffer(0xA000), 2, 2);
            Assert.Equal(SimDataCheckCore.Verdict.NoExpectation, core.Check(new object(), default, 3));
            Assert.Equal(SimDataCheckCore.Verdict.NoExpectation, core.Check(null, default, 3));
        }

        [Fact]
        public void AStepThatReturnedNothingKeepsWhatWasExpected()
        {
            var game = new object();
            var core = Stepped(game, Buffer(0xA000), 10);
            core.Recorded(game, IntPtr.Zero, 0, default, 11, 11);
            Assert.Equal(SimDataCheckCore.Verdict.Match, core.Check(game, Buffer(0xA000), 11));
            Assert.Equal(2, core.Steps);
        }

        [Fact]
        public void FirstMismatchOnceThenCountedByFrameAndRun()
        {
            var game = new object();
            var core = Stepped(game, Buffer(0xB000), 10);
            Assert.Equal(SimDataCheckCore.Verdict.First, core.Check(game, Buffer(0xA000), 11));
            // A second check in the same frame counts nothing more.
            Assert.Equal(SimDataCheckCore.Verdict.Counted, core.Check(game, Buffer(0xA000), 11));
            Assert.Equal(SimDataCheckCore.Verdict.Counted, core.Check(game, Buffer(0xA000), 12));
            Assert.Equal(SimDataCheckCore.Verdict.Match, core.Check(game, Buffer(0xB000), 13));
            Assert.Equal(SimDataCheckCore.Verdict.Counted, core.Check(game, Buffer(0xA000), 20));
            Assert.Equal(3, core.MismatchFrames);
            Assert.Equal(2, core.Episodes);
        }

        [Fact]
        public void TheFirstLineSaysWhenAndWhat()
        {
            var game = new object();
            var core = new SimDataCheckCore();
            core.Recorded(game, (IntPtr)0x900, 1, Buffer(0xA000), 1, 16);
            core.StepStarted(5);
            core.StepEnded(new InvalidOperationException("boom"), 5);
            core.SaveStarted(7);
            core.SaveEnded(8);
            core.CellsMessage("SimData_ResizeAndInitializeVacuumCells", 9);
            core.StepStarted(10);
            core.Recorded(game, (IntPtr)0x1000, 5124, Buffer(0xB000), 10, 160);
            core.StepEnded(null, 10);
            var now = Buffer(0xB000);
            now.ElementChunks = (IntPtr)0xA020;
            Assert.Equal(SimDataCheckCore.Verdict.First, core.Check(game, now, 12));
            string line = core.FirstLine(Context(12), now, 192, PerSecond);
            Assert.StartsWith("[Moono] sim-data-check: MISMATCH (#91): ", line);
            Assert.Contains("at frame 12 (checked before Game.Update)", line);
            Assert.Contains("game time 1234.5 s, cycle 3, paused no, active world 2", line);
            Assert.Contains("last completed StepTheSim: frame 10 (2 frames ago), 32.0 ms ago, result 0x1000, numElementChunkInfos 5124", line);
            Assert.Contains("this frame StepTheSim did not run", line);
            Assert.Contains("last StepTheSim that threw: frame 5 (7 frames ago), System.InvalidOperationException: boom", line);
            Assert.Contains("save not in progress, last finished frame 8 (4 frames ago)", line);
            Assert.Contains("timelapser: screenshot pending no, rendering yes", line);
            Assert.Contains("last sim cells message: SimData_ResizeAndInitializeVacuumCells at frame 9", line);
            Assert.Contains("elementChunks 0xA020!=0xB020,", line);
            Assert.Contains("emittedMassEntries 0xB010 (same)", line);
            Assert.EndsWith("diseaseConsumedInfos 0xB050 (same). Further mismatches are only counted.", line);
            Assert.DoesNotContain("\n", line);
        }

        [Fact]
        public void CountsAreWrittenEveryTenMinutesAndWhenAnotherColonyStarts()
        {
            var game = new object();
            var core = Stepped(game, Buffer(0xB000), 10);
            Assert.Null(core.Periodic(10, 1, PerSecond));
            Assert.Equal(SimDataCheckCore.Verdict.First, core.Check(game, Buffer(0xA000), 11));
            // The first line covers the first mismatch: nothing more to say yet.
            Assert.Null(core.Periodic(11, 1 + 600 * PerSecond, PerSecond));
            core.Check(game, Buffer(0xA000), 12);
            Assert.Null(core.Periodic(12, 2 + 600 * PerSecond, PerSecond));
            string more = core.Periodic(13, 1 + 1200 * PerSecond, PerSecond);
            Assert.NotNull(more);
            Assert.Contains("1 more frame(s)", more);
            Assert.Null(core.GameChanged(game, 14, out bool first));
            Assert.True(first);
            Assert.Null(core.GameChanged(game, 14, out bool again));
            Assert.False(again);
            core.Check(game, Buffer(0xA000), 15);
            string ended = core.GameChanged(null, 16, out _);
            Assert.Contains("in the colony before this one", ended);
            Assert.Equal(SimDataCheckCore.Verdict.NoExpectation, core.Check(game, Buffer(0xA000), 17));
            Assert.Null(core.AtExit(18));
        }

        [Fact]
        public void PrepareGameDataOutsideAStepIsCounted()
        {
            var core = new SimDataCheckCore();
            Assert.True(core.OutsidePrepare(3));
            Assert.False(core.OutsidePrepare(4));
            Assert.Equal(2, core.OutsidePrepares);
        }
    }
}
