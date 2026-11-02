using System.Collections.Generic;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using Xunit;

namespace OniMoono.Tests
{
    /// The texel upload pool (UploadBuffers): buffers returned together go back to the pool as
    /// buffers returned one at a time do.
    public class UploadBuffersTests
    {
        [Fact]
        public void ReturnAllPoolsEachBuffer()
        {
            const long size = 123_457; // a size class of this test's own
            var a = UploadBuffers.Rent(size);
            var b = UploadBuffers.Rent(size);
            var returned = new List<PixelMemory> { a, b };
            UploadBuffers.ReturnAll(returned);
            Assert.Empty(returned);
            var again = new HashSet<PixelMemory> { UploadBuffers.Rent(size), UploadBuffers.Rent(size) };
            Assert.Contains(a, again);
            Assert.Contains(b, again);
            foreach (var m in again) UploadBuffers.Return(m);
        }
    }
}
