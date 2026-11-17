using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace OniMoono.GameTweaks
{
    /// Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value)), the bytes the game writes for
    /// a colony summary, made straight into blocks the caller keeps: no string of the whole
    /// JSON, no StringBuilder chunks, no byte array of it, and no buffer grown by copying
    /// (JsonUtf8Tests).
    public static class JsonUtf8
    {
        /// Empties `buffer` and writes the JSON into it, as Newtonsoft's SerializeObject makes it
        /// (JsonSerializer.CreateDefault, a JsonTextWriter with the serializer's formatting,
        /// closed when done), in UTF-8 without a byte-order mark.
        public static void Serialize(object value, Blocks buffer)
        {
            buffer.Clear();
            var serializer = JsonSerializer.CreateDefault();
            using var text = new InvariantStreamWriter(buffer);
            using (var json = new JsonTextWriter(text) { CloseOutput = false })
            {
                json.Formatting = serializer.Formatting;
                serializer.Serialize(json, value, null);
            }
        }

        /// A write-only stream over blocks of 64 KB (under the large object heap's threshold),
        /// kept from one use to the next: once grown to the largest JSON it allocates nothing.
        public sealed class Blocks : Stream
        {
            const int Size = 1 << 16;
            readonly List<byte[]> blocks = new List<byte[]>();
            long length;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => length;
            public override long Position { get => length; set => throw new NotSupportedException(); }

            public void Clear() => length = 0;

            public override void Write(byte[] source, int offset, int count)
            {
                while (count > 0)
                {
                    int block = (int)(length / Size), at = (int)(length % Size);
                    if (block == blocks.Count) blocks.Add(new byte[Size]);
                    int n = Math.Min(count, Size - at);
                    Buffer.BlockCopy(source, offset, blocks[block], at, n);
                    offset += n;
                    count -= n;
                    length += n;
                }
            }

            /// The bytes, block by block, into `output`.
            public void WriteTo(Stream output)
            {
                long left = length;
                for (int i = 0; left > 0; i++)
                {
                    int n = (int)Math.Min(left, Size);
                    output.Write(blocks[i], 0, n);
                    left -= n;
                }
            }

            /// Whether the bytes are `expected`'s.
            public bool SameAs(byte[] expected)
            {
                if (expected.Length != length) return false;
                for (int i = 0, at = 0; at < expected.Length; i++, at += Size)
                {
                    int n = Math.Min(Size, expected.Length - at);
                    if (!expected.AsSpan(at, n).SequenceEqual(blocks[i].AsSpan(0, n))) return false;
                }
                return true;
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// A StreamWriter that leaves the stream open, with SerializeObject's StringWriter's
        /// invariant culture. Encoding.UTF8 and this encoding both replace an unpaired surrogate
        /// with U+FFFD.
        sealed class InvariantStreamWriter : StreamWriter
        {
            public InvariantStreamWriter(Stream stream) : base(stream, new UTF8Encoding(false), 1 << 14, leaveOpen: true) { }

            public override IFormatProvider FormatProvider => System.Globalization.CultureInfo.InvariantCulture;
        }
    }
}
