using System.Buffers.Binary;
using System.Text;

namespace GameStrip
{
    /// Writes a UnityFS archive with stored (uncompressed) blocks, from the original's header
    /// (its format and version strings) and new node contents.
    static class BundleWriter
    {
        /// Unity's usual block size for stored and LZ4 archives.
        const int BlockSize = 128 * 1024;
        /// The directory and block info sit together after the header.
        const uint BlocksAndDirectoryInfoCombined = 0x40;

        public static byte[] Write(byte[] original, List<(string Path, uint Flags, byte[] Data)> nodes)
        {
            // The original's header: signature, format, player version and revision (C strings).
            int at = 0;
            string Next()
            {
                int end = Array.IndexOf(original, (byte)0, at);
                var s = Encoding.UTF8.GetString(original, at, end - at);
                at = end + 1;
                return s;
            }
            if (Next() != "UnityFS") throw new InvalidDataException("not a UnityFS archive");
            uint format = BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(at));
            at += 4;
            string player = Next(), revision = Next();

            long total = nodes.Sum(n => (long)n.Data.Length);
            int blocks = checked((int)((total + BlockSize - 1) / BlockSize));
            var info = new MemoryStream();
            var w = new BinaryWriter(info);
            w.Write(new byte[16]); // hash of the uncompressed data: unused by readers
            WriteBE(w, blocks);
            for (long left = total; left > 0; left -= BlockSize)
            {
                int size = (int)Math.Min(BlockSize, left);
                WriteBE(w, size);
                WriteBE(w, size);
                WriteBE16(w, 0);
            }
            WriteBE(w, nodes.Count);
            long offset = 0;
            foreach (var (path, flags, data) in nodes)
            {
                WriteBE64(w, offset);
                WriteBE64(w, data.Length);
                WriteBE(w, (int)flags);
                w.Write(Encoding.UTF8.GetBytes(path));
                w.Write((byte)0);
                offset += data.Length;
            }
            var infoBytes = info.ToArray();

            var output = new MemoryStream();
            var o = new BinaryWriter(output);
            o.Write("UnityFS\0"u8);
            WriteBE(o, (int)format);
            o.Write(Encoding.UTF8.GetBytes(player));
            o.Write((byte)0);
            o.Write(Encoding.UTF8.GetBytes(revision));
            o.Write((byte)0);
            long sizeAt = output.Position;
            WriteBE64(o, 0); // total size, below
            WriteBE(o, infoBytes.Length);
            WriteBE(o, infoBytes.Length);
            WriteBE(o, (int)BlocksAndDirectoryInfoCombined);
            if (format >= 7) Pad(o, 16);
            o.Write(infoBytes);
            foreach (var (_, _, data) in nodes) o.Write(data);
            var result = output.ToArray();
            BinaryPrimitives.WriteInt64BigEndian(result.AsSpan((int)sizeAt), result.Length);
            return result;
        }

        static void Pad(BinaryWriter w, int alignment)
        {
            while (w.BaseStream.Position % alignment != 0) w.Write((byte)0);
        }

        static void WriteBE(BinaryWriter w, int v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, v);
            w.Write(b);
        }

        static void WriteBE16(BinaryWriter w, short v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(b, v);
            w.Write(b);
        }

        static void WriteBE64(BinaryWriter w, long v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, v);
            w.Write(b);
        }
    }
}
