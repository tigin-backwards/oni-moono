using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The critter-needs-first game tweak's due checks (src/OniMoono.GameTweaks/DueChecks.cs,
    /// compiled in here): one check a key, handed out once its time has come.
    public class DueChecksTests
    {
        [Fact]
        public void AKeyIsHandedOutOnceWhenItsTimeHasCome()
        {
            var checks = new DueChecks<string>();
            checks.Set("a", 10f);
            checks.Set("b", 12f);
            Assert.Empty(checks.Take(9.9f));
            Assert.Equal(new[] { "a" }, checks.Take(10f));
            Assert.Empty(checks.Take(11f));
            Assert.Equal(new[] { "b" }, checks.Take(30f));
            Assert.Equal(0, checks.Count);
            Assert.Empty(checks.Take(100f));
        }

        [Fact]
        public void ALaterSetReplacesTheKeysTime()
        {
            // Each meal moves the critter's check: only the last one counts.
            var checks = new DueChecks<string>();
            checks.Set("a", 10f);
            checks.Set("a", 13f);
            Assert.Equal(1, checks.Count);
            Assert.Empty(checks.Take(12f));
            Assert.Equal(new[] { "a" }, checks.Take(13f));
            // And an earlier one set after a pass is found.
            checks.Set("b", 50f);
            Assert.Empty(checks.Take(20f));
            checks.Set("c", 21f);
            Assert.Equal(new[] { "c" }, checks.Take(22f));
            Assert.Equal(new[] { "b" }, checks.Take(50f));
        }

        [Fact]
        public void SeveralDueAtOnceAndClear()
        {
            var checks = new DueChecks<string>();
            for (int i = 0; i < 100; i++) checks.Set("k" + i, i);
            Assert.Equal(50, checks.Take(49f).Count);
            Assert.Equal(50, checks.Count);
            checks.Clear();
            Assert.Equal(0, checks.Count);
            Assert.Empty(checks.Take(1000f));
            checks.Set("z", 5f);
            Assert.Equal(new[] { "z" }, checks.Take(5f));
        }

        [Fact]
        public void ASharedInstallRunsOnce()
        {
            // brain-rate and critter-needs-first both ask for the hooks: the first one installs.
            var install = new Once();
            int installs = 0;
            for (int i = 0; i < 3; i++)
                if (install.First()) installs++;
            Assert.Equal(1, installs);
            Assert.True(new Once().First());
        }
    }
}
