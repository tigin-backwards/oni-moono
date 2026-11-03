using System;
using System.Collections.Generic;
using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// Bindless handles' residency (src/OniMoono.Desktop/Gl/GlBindless.cs, BindlessResidency):
    /// every handle is resident when a draw samples it, or the draw binds the texture instead;
    /// a handle is made non-resident once (never twice: GL's INVALID_OPERATION), and only after
    /// a fence placed after the last frame that may have sampled it has signalled; names with
    /// resident handles are deleted after that fence too.
    [Collection(nameof(BindlessResidencyTests))]
    public sealed class BindlessResidencyTests : IDisposable
    {
        /// GL as the residency code sees it: handles by (name, sampler), residency (Live), deleted
        /// names, fences signalled up to SignalledUpTo; FailResident makes residency fail.
        sealed class Calls : BindlessResidency.ICalls
        {
            public readonly HashSet<ulong> Live = new();
            public readonly List<uint> Deleted = new();
            public readonly List<string> Errors = new();
            public Func<ulong, bool> FailResident = _ => false;
            public long Fences, SignalledUpTo;
            public int ResidentCalls;

            public ulong Create(DeviceTexture t, uint sampler) => t.Id == 0 ? 0 : t.Id * 100UL + sampler;

            public bool Resident(ulong h)
            {
                ResidentCalls++;
                if (FailResident(h)) return false;
                if (!Live.Add(h)) Errors.Add($"{h} already resident");
                return true;
            }

            public void NonResident(ulong h) { if (!Live.Remove(h)) Errors.Add($"{h} not resident"); }

            public void Delete(uint name)
            {
                foreach (var h in Live)
                    if (h / 100 == name) Errors.Add($"name {name} deleted with {h} resident");
                Deleted.Add(name);
            }

            public long Fence() => ++Fences;
            public bool Signalled(long fence) => fence <= SignalledUpTo;
        }

        readonly Calls calls = new();
        readonly int idleFrames = BindlessResidency.IdleFrames, maxResident = BindlessResidency.MaxResident;

        public BindlessResidencyTests()
        {
            BindlessResidency.Reset();
            BindlessResidency.IdleFrames = 0;
            BindlessResidency.MaxResident = 0;
        }

        public void Dispose()
        {
            BindlessResidency.IdleFrames = idleFrames;
            BindlessResidency.MaxResident = maxResident;
            BindlessResidency.Reset();
        }

        static DeviceTexture Texture(uint id) => new() { Id = id };

        // A draw samples `t` through `sampler`: its handle, or 0 with bind set.
        ulong Sample(DeviceTexture t, uint sampler, out bool bind) => BindlessResidency.Handle(t, sampler, calls, out bind);

        ulong Sample(DeviceTexture t, uint sampler = 1)
        {
            ulong h = Sample(t, sampler, out bool bind);
            Assert.False(bind);
            Assert.Contains(h, calls.Live);
            return h;
        }

        /// Frames end; the GPU keeps up (every fence signalled) unless `gpuDone` is false.
        void Frames(int n, bool gpuDone = true)
        {
            for (int i = 0; i < n; i++)
            {
                if (gpuDone) calls.SignalledUpTo = calls.Fences;
                BindlessResidency.EndFrame(calls);
            }
        }

        /// GL deletes the name now (DeviceTexture.DeleteName) unless the residency code keeps it.
        void Delete(DeviceTexture t)
        {
            if (!BindlessResidency.Release(t, calls)) calls.Delete(t.Id);
            t.Id = 0;
        }

        [Fact]
        public void HandlesStayResidentUntilReleasedByDefault()
        {
            var t = Texture(1);
            Sample(t, 1);
            Sample(t, 2);
            Frames(1000);
            Assert.Equal(new HashSet<ulong> { 101, 102 }, calls.Live);
            Assert.Equal(1, BindlessResidency.ResidentCount);
            Delete(t);
            Frames(1);
            Frames(1);
            Assert.Empty(calls.Live);
            Assert.Equal([1u], calls.Deleted);
            Assert.Equal(0, BindlessResidency.ResidentCount);
            Assert.Equal(0, BindlessResidency.Waiting);
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void AReleasedNameWaitsForTheFenceOfTheFrameThatReleasedIt()
        {
            var t = Texture(2);
            Sample(t);
            // Replaced mid-frame (a re-upload of another size: a new name, sampled at once).
            Delete(t);
            t.Id = 3;
            Sample(t);
            Assert.Contains(201UL, calls.Live);
            Assert.Empty(calls.Deleted);
            // The GPU is behind: frames end, nothing is made non-resident or deleted.
            Frames(3, gpuDone: false);
            Assert.Contains(201UL, calls.Live);
            Assert.Empty(calls.Deleted);
            Assert.Equal(1, BindlessResidency.Waiting);
            // The fence placed when that frame ended has signalled.
            calls.SignalledUpTo = 1;
            Frames(1, gpuDone: false);
            Assert.Equal(new HashSet<ulong> { 301 }, calls.Live);
            Assert.Equal([2u], calls.Deleted);
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void AHandleThatFailsToBecomeResidentIsBoundInsteadAndTriedAgainLater()
        {
            var t = Texture(4);
            calls.FailResident = h => h == 401;
            Assert.Equal(0UL, Sample(t, 1, out bool bind));
            Assert.True(bind);
            Assert.Empty(calls.Live);
            // Not tried again on every draw.
            int tries = calls.ResidentCalls;
            Frames(10);
            Sample(t, 1, out bind);
            Assert.True(bind);
            Assert.Equal(tries, calls.ResidentCalls);
            // Another sampler's handle of a texture that failed waits too.
            Sample(t, 2, out bind);
            Assert.True(bind);
            Assert.Contains("1 failed residencies", BindlessResidency.TakeCounters());
            // After RetryFrames, tried again: resident now.
            calls.FailResident = _ => false;
            Frames(BindlessResidency.RetryFrames);
            Assert.Equal(401UL, Sample(t, 1));
            Assert.Equal(1, BindlessResidency.ResidentCount);
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void IdleTexturesGoNonResidentAfterAFenceAndResidentAgainWhenSampled()
        {
            BindlessResidency.IdleFrames = 32;
            var used = Texture(5);
            var unused = Texture(6);
            Sample(unused);
            for (int f = 0; f < 100; f++)
            {
                Sample(used);
                Frames(1);
            }
            Assert.Equal(new HashSet<ulong> { 501 }, calls.Live);
            Assert.True(unused.Idle);
            Assert.Contains("1 made non-resident", BindlessResidency.TakeCounters());
            // Sampled again: the same handle, resident before the draw.
            Assert.Equal(601UL, Sample(unused));
            Assert.False(unused.Idle);
            Assert.Equal(2, BindlessResidency.ResidentCount);
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void AnIdleTextureSampledBeforeItsFenceStaysResident()
        {
            BindlessResidency.IdleFrames = 16;
            var t = Texture(7);
            Sample(t);
            // Queued to go non-resident at the sweep; the GPU is behind.
            Frames(BindlessResidency.SweepFrames * 2, gpuDone: false);
            Assert.True(t.Evicting);
            Assert.Contains(701UL, calls.Live);
            Sample(t);
            Assert.False(t.Evicting);
            calls.SignalledUpTo = calls.Fences;
            Frames(1, gpuDone: false);
            Assert.Contains(701UL, calls.Live);
            Assert.False(t.Idle);
            // Idle again later: a new wait, which the old fence does not end.
            Frames(BindlessResidency.SweepFrames * 2, gpuDone: false);
            Assert.True(t.Evicting);
            calls.SignalledUpTo = 1;
            Frames(1, gpuDone: false);
            Assert.Contains(701UL, calls.Live);
            Frames(1);
            Assert.True(t.Idle);
            Assert.Empty(calls.Live);
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void NotIdleWithinTheFramesAllowed()
        {
            BindlessResidency.IdleFrames = 32;
            var t = Texture(8);
            Sample(t);
            Frames(32);
            Assert.False(t.Idle || t.Evicting);
            Frames(BindlessResidency.SweepFrames + 1);
            Assert.True(t.Idle);
        }

        [Fact]
        public void AnIdleTextureReleasedIsDeletedAtOnceAndNotMadeNonResidentTwice()
        {
            BindlessResidency.IdleFrames = 16;
            var t = Texture(9);
            Sample(t);
            Frames(100);
            Assert.True(t.Idle);
            Delete(t);
            Assert.Equal([9u], calls.Deleted);
            Assert.False(t.Idle);
            Assert.Empty(t.Handles);
            // Recreated under a new name and sampled: a new handle, resident.
            t.Id = 10;
            Sample(t);
            Assert.Equal(new HashSet<ulong> { 1001 }, calls.Live);
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void ATextureReleasedWhileWaitingToGoIdleIsDeletedAfterItsFence()
        {
            BindlessResidency.IdleFrames = 16;
            var t = Texture(11);
            Sample(t);
            Frames(BindlessResidency.SweepFrames * 2, gpuDone: false);
            Assert.True(t.Evicting);
            Delete(t);
            Assert.Empty(calls.Deleted);
            Frames(1);
            Assert.Empty(calls.Deleted);
            Frames(1);
            Assert.Equal([11u], calls.Deleted);
            Assert.Empty(calls.Live);
            Assert.Equal(0, BindlessResidency.Waiting);
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void AFailedResidencyCapsTheResidentCountAtWhereItFailed()
        {
            // The driver refuses one handle with 100 textures resident; later ones succeed.
            calls.FailResident = h => h == 101 * 100 + 1;
            var textures = new List<DeviceTexture>();
            for (uint i = 1; i <= 150; i++)
            {
                var t = Texture(i);
                textures.Add(t);
                Sample(t, 1, out _);
                Frames(1);
            }
            Frames(BindlessResidency.SweepFrames + 1);
            // Kept under 100 from then on, the least recently sampled made non-resident.
            Assert.InRange(BindlessResidency.ResidentCount, 87, 100);
            Assert.True(textures[0].Idle);
            Assert.False(textures[149].Idle);
            Assert.Contains("at most 100", BindlessResidency.TakeCounters());
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void PastTheCapTheLeastRecentlySampledGoNonResident()
        {
            BindlessResidency.MaxResident = 8;
            var textures = new List<DeviceTexture>();
            for (uint i = 0; i < 12; i++)
            {
                var t = Texture(20 + i);
                textures.Add(t);
                Sample(t);
                Frames(1);
            }
            Frames(BindlessResidency.SweepFrames);
            Frames(1);
            // Down to 7 (7/8 of the cap), the oldest first.
            Assert.Equal(7, BindlessResidency.ResidentCount);
            for (int i = 0; i < 5; i++) Assert.True(textures[i].Idle);
            for (int i = 5; i < 12; i++) Assert.False(textures[i].Idle);
            // Sampled again, an evicted one is resident before its draw.
            Assert.Equal(2001UL, Sample(textures[0]));
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void AFailedWakeLeavesNoHandleResident()
        {
            BindlessResidency.IdleFrames = 16;
            var t = Texture(40);
            Sample(t, 1);
            Sample(t, 2);
            Frames(100);
            Assert.True(t.Idle);
            calls.FailResident = h => h == 4002;
            Sample(t, 1, out bool bind);
            Assert.True(bind);
            Assert.True(t.Idle);
            Assert.Empty(calls.Live);
            Assert.Empty(calls.Errors);
        }
    }
}
