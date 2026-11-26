using OniMoono.Desktop.D3d11;
using Xunit;

namespace OniMoono.Tests
{
    /// Where the Direct3D 11 device's input layouts read each of the original's 14 elements
    /// (D3d11Device.SourceOf). A channel the program reads but the mesh lacks reads a constant, as
    /// the original's own slot-1 default does, not the R8_UNORM filler at slot 0: that reads the
    /// first byte of the vertex's position, and Klei/DLC4/DemoliorBackground, which reads COLOR0
    /// of a mesh without colours, drew black with it (d3d11-plan.md, "The orbital background").
    public class D3d11LayoutTests
    {
        [Theory]
        [InlineData(true, true, true, "Channel")] // the mesh has it
        [InlineData(true, false, true, "Channel")] // has it, unread
        [InlineData(false, true, true, "Constant")] // read, lacked: a constant
        [InlineData(false, true, false, "Constant")] // the same without positions
        [InlineData(false, false, true, "Filler")] // unread, lacked: the original's filler
        [InlineData(false, false, false, "None")] // no slot 0 to point a filler at
        public void SourceOfEachElement(bool fed, bool read, bool slot0, string source)
            => Assert.Equal(source, D3d11Device.SourceOf(fed, read, slot0).ToString());
    }
}
