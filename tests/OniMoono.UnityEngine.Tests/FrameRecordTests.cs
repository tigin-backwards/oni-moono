using OniMoono.Desktop.Gl;
using Xunit;

namespace OniMoono.Tests
{
    /// The device's upload records come from the recording frame's arena: reused only once the
    /// frame has been reset (after the GL thread ran it), each time with a new generation.
    public class FrameRecordTests
    {
        [Fact]
        public void FrameRecordsAreReusedOnlyAfterTheFrameIsReset()
        {
            var arena = new FrameArena();
            var first = arena.Record<MeshUpload>();
            var second = arena.Record<MeshUpload>();
            Assert.NotSame(first, second);
            int generation = first.Generation;
            arena.Reset();
            var again = arena.Record<MeshUpload>();
            Assert.Same(first, again);
            // A new generation: a command still holding the old one would know.
            Assert.NotEqual(generation, again.Generation);
            Assert.NotNull(arena.Record<TexelUpload>());
        }
    }
}
