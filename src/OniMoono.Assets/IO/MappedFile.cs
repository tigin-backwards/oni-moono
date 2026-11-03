using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace OniMoono.Assets.IO
{
    /// A file mapped read-only into memory for the life of the process: its pages are read as
    /// they are first touched, instead of the whole file up front (sharedassets0.assets is
    /// 391 MB, of which loading reads a part). Dispose unmaps it (a bundle that turned out not
    /// to be read from its file).
    public sealed unsafe class MappedFile : IDisposable
    {
        readonly MemoryMappedFile file;
        readonly MemoryMappedViewAccessor view;
        public byte* Pointer { get; }
        public long Length { get; }

        public MappedFile(string path)
        {
            // The file's own length (FileInfo gives a symbolic link's).
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Length = stream.Length;
            try
            {
                file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            }
            catch
            {
                stream.Dispose(); // an empty file cannot be mapped
                throw;
            }
            view = file.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            Pointer = p + view.PointerOffset;
        }

        public void Dispose()
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Dispose();
            file.Dispose();
        }
    }
}
