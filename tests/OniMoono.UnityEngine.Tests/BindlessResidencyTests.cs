using System;
using System.Collections.Generic;
using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// Bindless handles' residency (src/OniMoono.Desktop/Gl/GlBindless.cs, BindlessResidency):
    /// every handle is resident when a draw samples its texture, made non-resident once (never
    /// twice: GL's INVALID_OPERATION), and idle ones only with ONI_BINDLESS_IDLE_FRAMES.
    [Collection(nameof(BindlessResidencyTests))]
    public sealed class BindlessResidencyTests : IDisposable
    {
        sealed class Calls : BindlessResidency.ICalls
        {
            public readonly HashSet<ulong> Resident = new();
            public readonly List<string> Errors = new();
            void BindlessResidency.ICalls.Resident(ulong h) { if (!Resident.Add(h)) Errors.Add($"{h} already resident"); }
            void BindlessResidency.ICalls.NonResident(ulong h) { if (!Resident.Remove(h)) Errors.Add($"{h} not resident"); }
        }

        readonly Calls calls = new();
        readonly int idleFrames = BindlessResidency.IdleFrames;

        public BindlessResidencyTests() => BindlessResidency.Reset();

        public void Dispose()
        {
            BindlessResidency.IdleFrames = idleFrames;
            BindlessResidency.Reset();
        }

        // What GlBindless.Handle does with a texture a draw samples through `sampler`.
        DeviceTexture Sample(DeviceTexture t, uint sampler, ulong handle)
        {
            BindlessResidency.Sampled(t, calls);
            if (t.Handles != null && t.Handles.ContainsKey(sampler)) return t;
            ((BindlessResidency.ICalls)calls).Resident(handle);
            (t.Handles ??= new())[sampler] = handle;
            BindlessResidency.Added(t);
            return t;
        }

        void Frames(int n)
        {
            for (int i = 0; i < n; i++) BindlessResidency.EndFrame(calls);
        }

        [Fact]
        public void HandlesStayResidentUntilReleasedByDefault()
        {
            BindlessResidency.IdleFrames = 0;
            var t = Sample(new DeviceTexture(), 1, 10);
            Sample(t, 2, 11);
            Frames(1000);
            Assert.Equal(new HashSet<ulong> { 10, 11 }, calls.Resident);
            Assert.Equal(1, BindlessResidency.ResidentCount);
            BindlessResidency.Release(t, calls);
            Assert.Empty(calls.Resident);
            Assert.Equal(0, BindlessResidency.ResidentCount);
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void IdleTexturesAreMadeNonResidentAndResidentAgainWhenSampled()
        {
            BindlessResidency.IdleFrames = 32;
            var used = new DeviceTexture();
            var unused = Sample(new DeviceTexture(), 1, 20);
            for (int f = 0; f < 100; f++)
            {
                Sample(used, 1, 30);
                Frames(1);
            }
            Assert.Equal(new HashSet<ulong> { 30 }, calls.Resident);
            Assert.True(unused.Idle);
            Assert.Contains("1 made non-resident", BindlessResidency.TakeCounters());
            // Sampled again: the same handle, resident before the draw.
            Sample(unused, 1, 20);
            Assert.Equal(new HashSet<ulong> { 20, 30 }, calls.Resident);
            Assert.False(unused.Idle);
            Assert.Equal(2, BindlessResidency.ResidentCount);
            Assert.Empty(calls.Errors);
        }

        [Fact]
        public void NotIdleWithinTheFramesAllowed()
        {
            BindlessResidency.IdleFrames = 32;
            var t = Sample(new DeviceTexture(), 1, 40);
            Frames(32);
            Assert.False(t.Idle);
            Frames(BindlessResidency.SweepFrames + 1);
            Assert.True(t.Idle);
        }

        [Fact]
        public void AnIdleTextureReleasedIsNotMadeNonResidentTwice()
        {
            BindlessResidency.IdleFrames = 16;
            var t = Sample(new DeviceTexture(), 1, 50);
            Frames(100);
            Assert.True(t.Idle);
            BindlessResidency.Release(t, calls);
            Assert.False(t.Idle);
            Assert.Empty(t.Handles);
            Assert.Empty(calls.Errors);
            // Recreated under a new name and sampled: a new handle, resident.
            Sample(t, 1, 51);
            Assert.Equal(new HashSet<ulong> { 51 }, calls.Resident);
            Assert.Empty(calls.Errors);
        }
    }
}
