using System;
using System.IO;

namespace OniMoono.GameTweaks
{
    /// Where a save file's compressed body starts (save-read-ahead). Plain .NET with no game
    /// types (the tests compile it in).
    public static class SaveBody
    {
        /// Where the compressed body starts: after the header SaveGame.GetHeader reads and the
        /// type directory KSerialization.Manager.DeserializeDirectory reads (skipped, not parsed
        /// into types). -1 when it cannot be found; the load compares the bytes it is given
        /// whole, so a wrong offset only means the game's own path.
        public static int Offset(byte[] b, out bool compressed)
        {
            compressed = false;
            try
            {
                int pos = 0;
                int Int() { int v = BitConverter.ToInt32(b, pos); pos += 4; return v; }
                void Text()
                {
                    int n = Int();
                    if (n > 0) pos = checked(pos + n);
                    if (pos > b.Length) throw new EndOfStreamException();
                }
                void Type(int depth)
                {
                    if (depth > 64) throw new InvalidDataException("type nesting");
                    byte info = b[pos++];
                    int value = info & 63;
                    if ((info & 128) != 0)
                    {
                        if (value == 0) Text();
                        else if (value < 18 || value > 22) throw new InvalidDataException("unknown generic type");
                        int args = b[pos++];
                        for (int i = 0; i < args; i++) Type(depth + 1);
                    }
                    else if (value == 0 || value == 13) Text();
                    else if (value == 17) Type(depth + 1);
                }
                pos = 8;
                uint headerVersion = (uint)Int();
                int headerSize = BitConverter.ToInt32(b, 4);
                int compression = 0;
                if (headerVersion >= 1) compression = Int();
                pos = checked(pos + headerSize);
                int types = Int();
                for (int t = 0; t < types; t++)
                {
                    Text();
                    int fields = Int(), properties = Int();
                    if (fields < 0 || properties < 0) throw new InvalidDataException("member count");
                    for (int m = 0; m < fields + properties; m++)
                    {
                        Text();
                        Type(0);
                    }
                }
                if (pos > b.Length) return -1;
                compressed = compression != 0;
                return pos;
            }
            catch (Exception e) when (e is ArgumentException || e is IndexOutOfRangeException || e is EndOfStreamException || e is InvalidDataException || e is OverflowException)
            {
                return -1;
            }
        }
    }
}
