using System.Collections.Generic;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// Unity's player arguments in a launch line (src/OniMoono.Host/UnityArguments.cs, compiled in
    /// here): numbers are read or noted, others are skipped with their documented values.
    public class UnityArgumentsTests
    {
        [Fact]
        public void ANumberIsRead()
        {
            var notes = new List<string>();
            string[] args = ["-monitor", "2", "-x"];
            int i = 0;
            Assert.Equal(2, UnityArguments.Int(args, ref i, notes));
            Assert.Equal(1, i);
            Assert.Empty(notes);
        }

        [Fact]
        public void AMalformedOrMissingNumberIsNoted()
        {
            var notes = new List<string>();
            string[] args = ["-monitor", "two", "-screen-fullscreen", "2", "-screen-width"];
            int i = 0;
            Assert.Null(UnityArguments.Int(args, ref i, notes));
            Assert.Equal(1, i);
            i = 2;
            Assert.Null(UnityArguments.Int(args, ref i, notes, min: 0, max: 1));
            i = 4;
            Assert.Null(UnityArguments.Int(args, ref i, notes, min: 1));
            Assert.Equal(4, i);
            Assert.Equal(3, notes.Count);
        }

        [Fact]
        public void AnIgnoredArgumentTakesItsDocumentedValue()
        {
            var notes = new List<string>();
            string[] args = ["-screen-quality", "Beautiful", "-popupwindow", "-force-d3d11"];
            int i = 0;
            UnityArguments.Ignore(args, ref i, notes);
            Assert.Equal(1, i);
            i = 2;
            UnityArguments.Ignore(args, ref i, notes);
            Assert.Equal(2, i);
            Assert.Equal(2, notes.Count);
        }

        [Theory]
        [InlineData("-force-glcore", true, 0)]
        [InlineData("-force-opengl", true, 0)]
        [InlineData("-force-vulkan", true, 1)]
        [InlineData("-FORCE-VULKAN", true, 1)]
        [InlineData("-force-d3d11", false, 0)]
        [InlineData("-force-glcore45", false, 0)]
        [InlineData("--renderer", false, 0)]
        public void TheRendererOptionsAreRead(string arg, bool read, int noted)
        {
            var notes = new List<string>();
            Assert.Equal(read, UnityArguments.Renderer(arg, notes));
            Assert.Equal(noted, notes.Count);
        }

        [Fact]
        public void OursAreTheDoubleDashOnes()
        {
            Assert.False(UnityArguments.IsUnitys("--frames"));
            Assert.True(UnityArguments.IsUnitys("-popupwindow"));
            Assert.True(UnityArguments.IsUnitys("delayed"));
        }
    }
}
