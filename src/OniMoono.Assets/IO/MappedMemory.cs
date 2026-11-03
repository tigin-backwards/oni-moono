using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace OniMoono.Assets.IO
{
    /// A range of a file mapped read-only for the life of the process, as memory: the windows of a
    /// bundle's data that point into its decompressed-bundle cache file (BundleCache) or into an
    /// uncompressed bundle file itself. Its pages are clean file pages, read as they are first
    /// touched; nothing writes through it (its span is writable only because Memory<byte> needs
    /// one: a write faults). Never unmapped, like MappedFile: a window handed out (a texture's
    /// texel source) may be read at any time.
    public sealed unsafe class MappedMemory : MemoryManager<byte>
    {
        readonly MemoryMappedFile file;
        readonly MemoryMappedViewAccessor view;
        public byte* Pointer { get; }
        public int Length { get; }

        /// Maps `length` bytes at `offset` of the file at `path`. The file is opened for reading
        /// and shared for reading and deleting (other launches map it too; a stale one may be
        /// deleted on Linux while mapped).
        public MappedMemory(string path, long offset, int length)
            : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), offset, length) { }

        /// Maps `length` bytes at `offset` of the file open in `stream`, which it takes over (the
        /// stream is closed when the mapping cannot be made; else the mapping keeps the file).
        public MappedMemory(FileStream stream, long offset, int length)
        {
            try
            {
                if (offset < 0 || length < 0) throw new ArgumentOutOfRangeException(nameof(length));
                if (offset + length > stream.Length) throw new InvalidDataException($"{stream.Name}: {length} bytes at {offset} are past its end ({stream.Length} bytes).");
                file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
            try
            {
                view = file.CreateViewAccessor(offset, length, MemoryMappedFileAccess.Read);
            }
            catch
            {
                file.Dispose();
                throw;
            }
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            Pointer = p + view.PointerOffset;
            Length = length;
        }

        public override Span<byte> GetSpan() => new(Pointer, Length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)Length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
            return new MemoryHandle(Pointer + elementIndex);
        }

        public override void Unpin() { }

        /// Kept mapped for the life of the process.
        protected override void Dispose(bool disposing) { }
    }
}
