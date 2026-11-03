using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace OniMoono.Assets.IO
{
    /// Sequential reader over a byte buffer or a mapped file, little- or big-endian.
    public sealed unsafe class DataReader
    {
        readonly byte[]? buffer;
        // A mapped file's bytes instead of `buffer` (MappedFile).
        readonly byte* pointer;
        readonly int start;
        readonly int end;
        int position;

        public bool BigEndian { get; set; }

        public DataReader(byte[] buffer) : this(buffer, 0, buffer.Length) { }

        public DataReader(byte[] buffer, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(length));
            this.buffer = buffer;
            start = offset;
            end = offset + length;
            position = offset;
        }

        /// A window of a mapped file.
        public DataReader(MappedFile file, int offset, int length)
        {
            if (offset < 0 || length < 0 || (long)offset + length > file.Length) throw new ArgumentOutOfRangeException(nameof(length));
            pointer = file.Pointer;
            start = offset;
            end = offset + length;
            position = offset;
        }

        /// Position relative to the start of this reader's window.
        public int Position
        {
            get => position - start;
            set
            {
                if (value < 0 || start + value > end) throw new ArgumentOutOfRangeException(nameof(value));
                position = start + value;
            }
        }

        public int Length => end - start;
        public int Remaining => end - position;
        public int AbsolutePosition => position;

        ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || position + count > end)
                throw new EndOfStreamException($"Read of {count} bytes at {Position} past the end ({Length}).");
            var span = buffer != null ? new ReadOnlySpan<byte>(buffer, position, count) : new ReadOnlySpan<byte>(pointer + position, count);
            position += count;
            return span;
        }

        public void Skip(int count) => Take(count);

        /// Advances to the next multiple of `alignment` relative to the window start.
        public void Align(int alignment = 4)
        {
            int rel = position - start;
            int pad = (alignment - rel % alignment) % alignment;
            if (pad > 0) Take(pad);
        }

        public byte ReadByte() => Take(1)[0];
        public sbyte ReadSByte() => (sbyte)Take(1)[0];
        public bool ReadBool() => Take(1)[0] != 0;
        public short ReadInt16() => BigEndian ? BinaryPrimitives.ReadInt16BigEndian(Take(2)) : BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        public ushort ReadUInt16() => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public int ReadInt32() => BigEndian ? BinaryPrimitives.ReadInt32BigEndian(Take(4)) : BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public uint ReadUInt32() => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public long ReadInt64() => BigEndian ? BinaryPrimitives.ReadInt64BigEndian(Take(8)) : BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public ulong ReadUInt64() => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Take(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public float ReadSingle() => BigEndian ? BinaryPrimitives.ReadSingleBigEndian(Take(4)) : BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public double ReadDouble() => BigEndian ? BinaryPrimitives.ReadDoubleBigEndian(Take(8)) : BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public byte[] ReadBytes(int count) => Take(count).ToArray();

        public ReadOnlySpan<byte> ReadSpan(int count) => Take(count);

        /// Null-terminated UTF-8 string.
        public string ReadCString()
        {
            var rest = buffer != null ? new ReadOnlySpan<byte>(buffer, position, end - position) : new ReadOnlySpan<byte>(pointer + position, end - position);
            int length = rest.IndexOf((byte)0);
            if (length < 0) throw new EndOfStreamException("Unterminated string.");
            var s = Encoding.UTF8.GetString(rest[..length]);
            position += length + 1;
            return s;
        }

        /// Unity's serialized string: int32 byte length, UTF-8 bytes (alignment is the caller's).
        public string ReadLengthPrefixedString()
        {
            int length = ReadInt32();
            return Encoding.UTF8.GetString(Take(length));
        }
    }

}
