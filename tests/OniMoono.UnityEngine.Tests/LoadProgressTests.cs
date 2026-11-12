using System;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using Xunit;

namespace OniMoono.Tests
{
    /// The save load's bar (LoadProgress, GlLoadAnimation.Advance): each phase takes its share of
    /// the bar by the time it took last load; a phase's part follows what is counted, or the
    /// time against the time expected, and stays below its end; the bar drawn never goes back.
    public class LoadProgressTests
    {
        static readonly double[] Seconds = [1, 0.5, 1.5, 1, 3, 1, 2];

        [Fact]
        public void PhasesAreWeightedByTheirExpectedTime()
        {
            // 10 s in all: each phase takes its seconds' tenth of the bar.
            Assert.Equal(0.0, LoadProgress.Value(0, 0, null, Seconds), 6);
            Assert.Equal(0.15, LoadProgress.Value(LoadProgress.Decompression, 0, 0.0, Seconds), 6);
            Assert.Equal(0.15 + 0.15 * 0.5, LoadProgress.Value(LoadProgress.Decompression, 99, 0.5, Seconds), 6);
            Assert.Equal(0.4 + 0.3 * 0.5, LoadProgress.Value(LoadProgress.Objects, 0, 0.5, Seconds), 6);
            Assert.Equal(0.8, LoadProgress.Value(LoadProgress.Starts, 0, 0.0, Seconds), 6);
        }

        [Fact]
        public void APhaseNeverReachesItsEndBeforeTheNext()
        {
            double end = LoadProgress.Value(1, 0, 1.0, Seconds), next = LoadProgress.Value(2, 0, 0.0, Seconds);
            Assert.True(end < next);
            Assert.True(LoadProgress.Value(1, 1000, null, Seconds) < next);
            Assert.True(LoadProgress.Value(LoadProgress.Starts, 0, 1.0, Seconds) < 1.0);
        }

        [Fact]
        public void TimeEasesToFourFifthsByTheExpectedTime()
        {
            Assert.InRange(LoadProgress.Ease(1), 0.79, 0.81);
            Assert.Equal(0, LoadProgress.Ease(0));
            Assert.True(LoadProgress.Ease(10) < 0.995);
            for (double x = 0; x < 10; x += 0.1) Assert.True(LoadProgress.Ease(x + 0.1) >= LoadProgress.Ease(x));
        }

        [Fact]
        public void TheBarRisesAcrossEveryPhaseWithTime()
        {
            double last = 0;
            for (int p = 0; p < LoadProgress.Phases; p++)
                for (double t = 0; t <= 10; t += 0.25)
                {
                    double v = LoadProgress.Value(p, t, null, Seconds);
                    Assert.True(v >= last, $"phase {p} at {t} s: {v} after {last}");
                    last = v;
                }
        }

        [Fact]
        public void ADefaultOrBrokenProfileStillGivesABar()
        {
            Assert.InRange(LoadProgress.Value(2, 1, null, LoadProgress.DefaultSeconds), 0.01, 0.99);
            Assert.Equal(0, LoadProgress.Value(2, 1, null, [0, 0, 0, 0, 0, 0, 0]));
        }

        [Fact]
        public void TheBarDrawnNeverGoesBack()
        {
            double shown = 0;
            var random = new Random(7);
            for (int i = 0; i < 1000; i++)
            {
                double next = GlLoadAnimation.Advance(shown, random.NextDouble(), 1 / 30.0);
                Assert.True(next >= shown);
                Assert.True(next <= 1);
                shown = next;
            }
            Assert.Equal(0.5, GlLoadAnimation.Advance(0.5, 0.2, 1));
            Assert.InRange(GlLoadAnimation.Advance(0, 1, 0.25), 0.6, 0.65);
        }
    }
}

namespace OniMoono.Tests
{
    /// The kept load times (LoadTimes): a save's last load, by path and size, survives a restart;
    /// the least recently loaded go past the capacity; a broken file starts anew.
    public class LoadTimesTests
    {
        [Fact]
        public void TheLastLoadOfASaveIsKeptAcrossARestart()
        {
            var dir = System.IO.Directory.CreateTempSubdirectory("loadtimes").FullName;
            var path = System.IO.Path.Combine(dir, "oni-moono", "load-times.json");
            var times = new OniMoono.Host.GameTweaks.LoadTimes(path);
            string key = OniMoono.Host.GameTweaks.LoadTimes.Key("/saves/a.sav", 123);
            Assert.Null(times.Find(key));
            times.Write(times.Record(key, [0.5, 0.1, 0.4, 1, 6, 0.5, 9]));
            times.Write(times.Record(key, [0.4, 0.1, 0.4, 1, 5, 0.5, 8]));
            var again = new OniMoono.Host.GameTweaks.LoadTimes(path);
            Assert.Equal(new[] { 0.4, 0.1, 0.4, 1, 5, 0.5, 8 }, again.Find(key));
            Assert.Null(again.Find(OniMoono.Host.GameTweaks.LoadTimes.Key("/saves/a.sav", 124)));
            System.IO.Directory.Delete(dir, true);
        }

        [Fact]
        public void TheLeastRecentlyLoadedGoFirst()
        {
            var dir = System.IO.Directory.CreateTempSubdirectory("loadtimes").FullName;
            var times = new OniMoono.Host.GameTweaks.LoadTimes(System.IO.Path.Combine(dir, "t.json"));
            for (int i = 0; i <= OniMoono.Host.GameTweaks.LoadTimes.Capacity; i++) times.Record($"k{i}", [1, 1, 1, 1, 1, 1, 1]);
            Assert.Null(times.Find("k0"));
            Assert.NotNull(times.Find("k1"));
            times.Record("bad", [1, -1, 1, 1, 1, 1, 1]);
            Assert.Null(times.Find("bad"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "t.json"), "{not json");
            Assert.Null(new OniMoono.Host.GameTweaks.LoadTimes(System.IO.Path.Combine(dir, "t.json")).Find("k1"));
            System.IO.Directory.Delete(dir, true);
        }
    }
}
