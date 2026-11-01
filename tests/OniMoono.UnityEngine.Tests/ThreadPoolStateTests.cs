using OniMoono.Engine;
using Xunit;

namespace OniMoono.Tests
{
    /// The pool's counts that TexelCopies.Fence logs when a copy waits too long are read
    /// from CoreCLR's internals: a runtime that renames them shows here, not in a hang's log.
    public class ThreadPoolStateTests
    {
        [Fact]
        public void ReadsThePoolsOwnCounts()
        {
            string state = ThreadPoolState.Describe();
            Assert.DoesNotContain("unreadable", state);
            foreach (var part in (string[])["NumProcessingWork ", "NumExistingThreads ", "NumThreadsGoal ", "numRequestedWorkers ", "outstanding request ", "local queues "])
                Assert.Matches(part + "[0-9]", state);
        }
    }
}
