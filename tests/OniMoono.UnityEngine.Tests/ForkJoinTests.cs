using System.Linq;
using System.Threading;
using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// The draw recorder's fork-join: every item of a run runs once, all before For returns, and
    /// helpers waking late never run an item of a later run twice or of an earlier one at all.
    public class ForkJoinTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        public void EachItemRunsOnceBeforeForReturns(int helpers)
        {
            var forkJoin = new ForkJoin(helpers);
            for (int run = 0; run < 2000; run++)
            {
                int count = 1 + run % 37;
                var hits = new int[count];
                int stamp = run;
                forkJoin.For(count, i =>
                {
                    Interlocked.Increment(ref hits[i]);
                    // A run's items see their own run.
                    Assert.Equal(run, stamp);
                    if (i % 5 == 0) Thread.SpinWait(200);
                });
                foreach (int h in hits) Assert.Equal(1, h);
            }
        }

        /// A run object serves the next For once nothing holds it: without helpers always, with
        /// them unless one is still on its way out of the last run.
        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        public void RunsAreUsedAgain(int helpers)
        {
            var forkJoin = new ForkJoin(helpers);
            int total = 0;
            for (int run = 0; run < 2000; run++) forkJoin.For(1 + run % 7, i => Interlocked.Increment(ref total));
            Assert.Equal(Enumerable.Range(0, 2000).Sum(run => 1 + run % 7), total);
            if (helpers == 0) Assert.Equal(1, forkJoin.RunsMade);
            else Assert.True(forkJoin.RunsMade < 2000, $"{forkJoin.RunsMade} runs made");
        }
    }
}
