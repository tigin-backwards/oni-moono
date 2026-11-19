using System;
using System.Collections.Generic;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The critter-needs-first game tweak's threat turns (src/OniMoono.GameTweaks/ThreatTurns.cs,
    /// compiled in here): a critter looking for a target is tested once its last turn is a gap
    /// old, at most one is asked for in 5 of the group's turns, and nothing is allocated a frame.
    public class ThreatTurnsTests
    {
        sealed class Critter
        {
            public string Name;
            public bool Ready = true;
            public int Tests, Asks;
            public override string ToString() => Name;
        }

        static readonly Func<Critter, bool> ready = c => { c.Tests++; return c.Ready; };
        static readonly Action<Critter> ask = c => c.Asks++;

        [Fact]
        public void ACritterIsTestedOnceItsLastTurnIsAGapOld()
        {
            var turns = new ThreatTurns<Critter>();
            var a = new Critter { Name = "a" };
            turns.Track(a, 10f);
            Assert.Equal(0, turns.Serve(9.9f, 10f, 5, ready, ask));
            Assert.Equal(0, a.Tests);
            Assert.Equal(1, turns.Serve(10f, 10f, 5, ready, ask));
            Assert.Equal(1, a.Asks);
            // Asked: not again before a gap has passed.
            Assert.Equal(0, turns.Serve(19.9f, 10f, 5, ready, ask));
            // A turn of its own at 15 moves it to 25.
            turns.Turned(a, 25f);
            Assert.Equal(0, turns.Serve(20f, 10f, 5, ready, ask));
            Assert.Equal(0, turns.Serve(24.9f, 10f, 5, ready, ask));
            Assert.Equal(1, turns.Serve(25f, 10f, 5, ready, ask));
            Assert.Equal(2, a.Asks);
            Assert.Equal(2, a.Tests);
        }

        [Fact]
        public void TurnsAlwaysWithinTheGapKeepItFromComingDue()
        {
            // Speed 1 at 30 fps or more: its turns come every gap or sooner, so it is never tested.
            var turns = new ThreatTurns<Critter>();
            var a = new Critter { Name = "a" };
            float now = 0f, gap = 3.3f;
            turns.Track(a, now + gap);
            for (int frame = 0; frame < 3000; frame++, now += 1f / 30f)
            {
                if (frame % 90 == 0) turns.Turned(a, now + gap);
                turns.Serve(now, gap, 5, ready, ask);
            }
            Assert.Equal(0, a.Tests);
        }

        [Fact]
        public void ACritterNotReadyIsTestedAgainAGapLater()
        {
            var turns = new ThreatTurns<Critter>();
            var a = new Critter { Name = "a", Ready = false };
            turns.Track(a, 1f);
            for (float now = 0f; now < 10.05f; now += 0.1f) turns.Serve(now, 2f, 5, ready, ask);
            // At 1, 3, 5, 7 and 9.
            Assert.Equal(5, a.Tests);
            Assert.Equal(0, a.Asks);
        }

        [Fact]
        public void UntrackedAndRemovedCrittersAreNotTested()
        {
            var turns = new ThreatTurns<Critter>();
            var a = new Critter { Name = "a" };
            var b = new Critter { Name = "b" };
            turns.Turned(a, 1f);
            Assert.Equal(0, turns.Count);
            turns.Track(b, 1f);
            turns.Remove(b);
            Assert.Equal(0, turns.Serve(5f, 1f, 5, ready, ask));
            Assert.Equal(0, a.Tests + b.Tests);
            // Tracked again later: a place of its own, the old one dropped.
            turns.Track(b, 6f);
            Assert.Equal(1, turns.Serve(6f, 1f, 5, ready, ask));
            Assert.Equal(1, b.Tests);
            // `ready` may drop the key (a critter whose monitor stopped).
            var c = new Critter { Name = "c" };
            var other = new ThreatTurns<Critter>();
            other.Track(c, 7f);
            Assert.Equal(0, other.Serve(7f, 1f, 5, x => { other.Remove(x); return false; }, ask));
            Assert.Equal(0, other.Count);
            Assert.Equal(0, other.Serve(100f, 1f, 5, ready, ask));
        }

        [Fact]
        public void ACritterWhoseTestThrowsIsDroppedOnce()
        {
            // #93: a colony left for the main menu frees its critters' state machines without
            // their states' exits, so they stay tracked; reading a freed one throws. Each must
            // cost one test, not an exception every frame, and the live critters still get turns.
            var turns = new ThreatTurns<Critter>();
            var freed = new List<Critter>();
            for (int i = 0; i < 10; i++)
            {
                freed.Add(new Critter { Name = "old" + i });
                turns.Track(freed[i], 0f);
            }
            var live = new Critter { Name = "live" };
            turns.Track(live, 0f);
            Func<Critter, bool> test = c =>
            {
                c.Tests++;
                if (c.Name.StartsWith("old")) throw new NullReferenceException();
                return c.Ready;
            };
            for (int frame = 0; frame < 20; frame++) turns.Serve(1f + frame, 1f, 5, test, ask);
            Assert.All(freed, c => Assert.Equal(1, c.Tests));
            Assert.Equal(10, turns.Dropped);
            Assert.IsType<NullReferenceException>(turns.LastError);
            Assert.Equal(1, turns.Count);
            Assert.True(live.Asks > 0);
        }

        [Fact]
        public void ClearForgetsTheLastGamesCritters()
        {
            // A new game: every critter of the last one is forgotten, none is tested again.
            var turns = new ThreatTurns<Critter>();
            var a = new Critter { Name = "a" };
            turns.Track(a, 0f);
            turns.Serve(1f, 10f, 5, c => throw new InvalidOperationException(), ask);
            var b = new Critter { Name = "b" };
            turns.Track(b, 0f);
            turns.Clear();
            Assert.Equal(0, turns.Count);
            Assert.Equal(0, turns.Dropped);
            Assert.Null(turns.LastError);
            Assert.Equal(0, turns.Serve(100f, 10f, 5, ready, ask));
            Assert.Equal(0, b.Tests);
            // Tracked again in the new game: a place of its own.
            turns.Track(b, 101f);
            Assert.Equal(1, turns.Serve(101f, 10f, 5, ready, ask));
            Assert.Equal(1, b.Asks);
        }

        [Fact]
        public void AtMostOneAskInFiveOfTheGroupsTurnsAndFourTestsAFrame()
        {
            var turns = new ThreatTurns<Critter>();
            var critters = new List<Critter>();
            for (int i = 0; i < 100; i++)
            {
                critters.Add(new Critter { Name = "k" + i });
                turns.Track(critters[i], 0f);
            }
            int asks = 0;
            for (int frame = 0; frame < 50; frame++) asks += turns.Serve(1f, 1000f, 5, ready, ask);
            Assert.Equal(50, asks);
            // brain-rate giving the group 1 turn a frame: one ask every 5 frames.
            asks = 0;
            for (int frame = 0; frame < 50; frame++) asks += turns.Serve(1f, 1000f, 1, ready, ask);
            Assert.Equal(10, asks);
            // None ready: at most 4 tests a frame, and no ask.
            foreach (var c in critters) { c.Ready = false; c.Tests = 0; }
            var idle = new ThreatTurns<Critter>();
            foreach (var c in critters) idle.Track(c, 0f);
            for (int frame = 0; frame < 10; frame++) Assert.Equal(0, idle.Serve(1f, 1000f, 5, ready, ask));
            int tests = 0;
            foreach (var c in critters) tests += c.Tests;
            Assert.Equal(40, tests);
        }

        [Fact]
        public void TheRoundRobinKeepsFourInFiveTurns()
        {
            // The game's critter group at speed 3 and 15 fps: 5 turns a frame, a 0.2 game second
            // frame, the priority queue served first. Half the critters look for a target and
            // always have one: the others still get a turn every N / 4 frames.
            const int N = 200, PerFrame = 5;
            const float Dt = 0.2f, Gap = N / 150f;
            var turns = new ThreatTurns<Critter>();
            var critters = new Critter[N];
            var last = new int[N];
            var index = new Dictionary<Critter, int>();
            for (int i = 0; i < N; i++)
            {
                critters[i] = new Critter { Name = "c" + i };
                index[critters[i]] = i;
                if (i % 2 == 0) turns.Track(critters[i], Gap);
            }
            var priority = new Queue<Critter>();
            Action<Critter> enqueue = c => { if (!priority.Contains(c)) priority.Enqueue(c); };
            int next = 0, worstOther = 0, seekerTurns = 0, otherTurns = 0, extra = 0;
            float now = 0f;
            const int Frames = 5000;
            for (int frame = 1; frame <= Frames; frame++, now += Dt)
            {
                for (int t = 0; t < PerFrame; t++)
                {
                    Critter c;
                    if (priority.Count > 0) { c = priority.Dequeue(); extra++; }
                    else { c = critters[next]; next = (next + 1) % N; }
                    int i = index[c];
                    if (frame > 2 * N)
                    {
                        if (i % 2 == 0) seekerTurns++;
                        else { otherTurns++; worstOther = Math.Max(worstOther, frame - last[i]); }
                    }
                    last[i] = frame;
                    if (i % 2 == 0) turns.Turned(c, now + Gap);
                }
                turns.Serve(now, Gap, PerFrame, ready, enqueue);
            }
            // At most one priority turn a frame: the round robin gets at least 4 of 5, so a round
            // of 200 takes at most 50 frames (and one more for where it falls in a frame).
            Assert.True(extra <= Frames, $"{extra} priority turns in {Frames} frames");
            Assert.True(worstOther <= N / (PerFrame - 1) + 1, $"another critter waited {worstOther} frames");
            // The seekers take the extra turns: as many as the others' plus one a frame at most.
            Assert.True(seekerTurns > otherTurns && seekerTurns <= otherTurns + Frames, $"seekers {seekerTurns} turns, others {otherTurns}");
        }

        [Fact]
        public void AFrameAllocatesNothing()
        {
            var turns = new ThreatTurns<Critter>();
            var critters = new Critter[500];
            for (int i = 0; i < critters.Length; i++)
            {
                critters[i] = new Critter { Name = "c" + i, Ready = i % 3 == 0 };
                turns.Track(critters[i], i * 0.01f);
            }
            float now = 0f;
            void Frames(int count)
            {
                for (int frame = 0; frame < count; frame++, now += 0.2f)
                {
                    for (int t = 0; t < 5; t++) turns.Turned(critters[(frame * 5 + t) % critters.Length], now + 3.3f);
                    turns.Serve(now, 3.3f, 5, ready, ask);
                }
            }
            Frames(2000);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Frames(10000);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
        }
    }
}
