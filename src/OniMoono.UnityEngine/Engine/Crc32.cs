using System;

namespace OniMoono.Engine
{
    /// CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320), incremental.
    internal sealed class Crc32
    {
        // Slicing by 8: table k holds the CRC of a byte followed by k zero bytes.
        static readonly uint[] table = BuildTable();

        uint state = 0xFFFFFFFFu;

        static uint[] BuildTable()
        {
            var t = new uint[8 * 256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            for (int k = 1; k < 8; k++)
                for (int i = 0; i < 256; i++)
                    t[k * 256 + i] = t[(k - 1) * 256 + i] >> 8 ^ t[t[(k - 1) * 256 + i] & 0xFF];
            return t;
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            uint c = state;
            var t = table.AsSpan();
            while (data.Length >= 8)
            {
                uint lo = c ^ System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data);
                uint hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
                c = t[7 * 256 + (int)(lo & 0xFF)] ^ t[6 * 256 + (int)(lo >> 8 & 0xFF)] ^ t[5 * 256 + (int)(lo >> 16 & 0xFF)] ^ t[4 * 256 + (int)(lo >> 24)]
                    ^ t[3 * 256 + (int)(hi & 0xFF)] ^ t[2 * 256 + (int)(hi >> 8 & 0xFF)] ^ t[256 + (int)(hi >> 16 & 0xFF)] ^ t[(int)(hi >> 24)];
                data = data.Slice(8);
            }
            foreach (byte b in data) c = t[(int)((c ^ b) & 0xFF)] ^ (c >> 8);
            state = c;
        }

        public uint Current => ~state;

        /// The hash of a string's UTF-8 bytes, as Unity's name hashes (Animator.StringToHash,
        /// animation binding paths); "" hashes to 0.
        public static uint Of(string s)
            => string.IsNullOrEmpty(s) ? 0 : Compute(System.Text.Encoding.UTF8.GetBytes(s));

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = new Crc32();
            crc.Append(data);
            return crc.Current;
        }
    }
}
