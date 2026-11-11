using System.IO;
using System.Text;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// save-read-ahead's skip over a save's header and type directory (SaveBody.Offset), against
    /// files written in the format SaveGame.GetHeader and KSerialization's directory read.
    public class SaveBodyTests
    {
        static void Text(BinaryWriter w, string s)
        {
            if (s == null) { w.Write(-1); return; }
            var bytes = Encoding.UTF8.GetBytes(s);
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        /// A save: header (version 1, compressed), a directory of two types whose members use
        /// every shape of type the directory has, then `body`.
        static byte[] Save(byte[] body, out int offset, int compression = 1, uint headerVersion = 1)
        {
            var m = new MemoryStream();
            var w = new BinaryWriter(m);
            var info = Encoding.UTF8.GetBytes("{\"numberOfCycles\":12}");
            w.Write(744825u);
            w.Write(info.Length);
            w.Write(headerVersion);
            if (headerVersion >= 1) w.Write(compression);
            w.Write(info);
            w.Write(2);
            // Game.Settings: an int, an enum, an array of user types, Dictionary<string, List<Vector2I>>.
            Text(w, "Game+Settings");
            w.Write(3);
            w.Write(1);
            Text(w, "count"); w.Write((byte)6);
            Text(w, "mode"); w.Write((byte)13); Text(w, "Game+Mode");
            Text(w, "items"); w.Write((byte)17); w.Write((byte)0); Text(w, "Item");
            Text(w, "cells"); w.Write((byte)(128 | 19)); w.Write((byte)2); w.Write((byte)12); w.Write((byte)(128 | 20)); w.Write((byte)1); w.Write((byte)14);
            // A user-defined generic type with one argument, and a null name.
            Text(w, "Holder");
            w.Write(1);
            w.Write(0);
            Text(w, "pair"); w.Write((byte)(128 | 0)); Text(w, "Box`1"); w.Write((byte)1); w.Write((byte)(64 | 6));
            w.Flush();
            offset = (int)m.Position;
            w.Write(body);
            return m.ToArray();
        }

        [Fact]
        public void FindsTheBodyAfterTheHeaderAndDirectory()
        {
            var body = new byte[] { 0x78, 0x01, 1, 2, 3 };
            var file = Save(body, out int offset);
            Assert.Equal(offset, SaveBody.Offset(file, out bool compressed));
            Assert.True(compressed);
            file = Save(body, out offset, compression: 0);
            Assert.Equal(offset, SaveBody.Offset(file, out compressed));
            Assert.False(compressed);
        }

        [Fact]
        public void HeaderVersionZeroHasNoCompressionField()
        {
            var file = Save(new byte[] { 9 }, out int offset, headerVersion: 0);
            Assert.Equal(offset, SaveBody.Offset(file, out bool compressed));
            Assert.False(compressed);
        }

        [Fact]
        public void TruncatedOrUnknownIsNotFound()
        {
            var file = Save(new byte[0], out int offset);
            for (int cut = 1; cut < offset; cut += 7)
                Assert.Equal(-1, SaveBody.Offset(file[..cut], out _));
            // A generic type the directory never writes (Colour, 23, marked generic).
            var bad = (byte[])file.Clone();
            int at = System.Array.IndexOf(bad, (byte)(128 | 19));
            bad[at] = 128 | 23;
            Assert.Equal(-1, SaveBody.Offset(bad, out _));
        }
    }
}
