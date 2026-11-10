using System.Buffers.Binary;
using System.Text;
using OniMoono.Assets.Files;
using OniMoono.Assets.IO;
using OniMoono.Assets.TypeTrees;

namespace GameStrip
{
    /// One object's reference to a range of a streamed resource file (.resS / .resource): a
    /// texture's or mesh's m_StreamData, an audio or video clip's m_Resource. OffsetAt and SizeAt
    /// are the positions of its offset and size fields in the serialized file's bytes, where Strip
    /// rewrites them.
    sealed record StreamRef(long PathId, int ClassId, string Name, bool Readable, string Path, long Offset, long Size, int OffsetAt, int SizeAt, bool Size64)
    {
        public string ClassName => ClassId switch
        {
            Classes.Texture2D => "Texture2D",
            Classes.Cubemap => "Cubemap",
            Classes.Texture3D => "Texture3D",
            Classes.Texture2DArray => "Texture2DArray",
            Classes.Mesh => "Mesh",
            Classes.AudioClip => "AudioClip",
            Classes.VideoClip => "VideoClip",
            _ => ClassId.ToString(),
        };
    }

    static class Classes
    {
        public const int Texture2D = 28, Mesh = 43, AudioClip = 83, Cubemap = 89, Texture3D = 117, Texture2DArray = 187, VideoClip = 329;

        /// Classes whose StreamingInfo is {offset UInt64, size UInt32, path}: textures and meshes.
        public static bool HasStreamingInfo(int id) => id is Texture2D or Cubemap or Texture3D or Texture2DArray or Mesh;

        /// Classes whose StreamedResource is {source, offset UInt64, size UInt64}.
        public static bool HasStreamedResource(int id) => id is AudioClip or VideoClip;
    }

    static class StreamRefs
    {
        /// The streamed references of a serialized file whose bytes are `bytes` (parsed from
        /// them, so object positions are positions in `bytes`).
        public static List<StreamRef> Find(SerializedFile file)
        {
            var refs = new List<StreamRef>();
            foreach (var info in file.Objects)
            {
                int classId = file.TypeOf(info).ClassId;
                if (!Classes.HasStreamingInfo(classId) && !Classes.HasStreamedResource(classId)) continue;
                var r = file.OpenObject(info);
                int start = r.AbsolutePosition;
                var f = TypeTreeReader.ReadNative(classId, r);
                string name = f.Contains("m_Name") ? f.String("m_Name") : "";
                bool readable = f.Contains("m_IsReadable") && f.Bool("m_IsReadable");
                string path;
                long offset, size;
                if (Classes.HasStreamingInfo(classId))
                {
                    var s = f.Struct("m_StreamData");
                    path = s.String("path");
                    offset = Convert.ToInt64(s["offset"]);
                    size = Convert.ToInt64(s["size"]);
                }
                else
                {
                    var s = f.Struct(classId == Classes.AudioClip ? "m_Resource" : "m_ExternalResources");
                    path = s.String("m_Source");
                    offset = Convert.ToInt64(s["m_Offset"]);
                    size = Convert.ToInt64(s["m_Size"]);
                }
                if (path.Length == 0) continue;
                var (offsetAt, sizeAt, size64) = Locate(file, info, start, classId, path, offset, size);
                refs.Add(new StreamRef(info.PathId, classId, name, readable, path, offset, size, offsetAt, sizeAt, size64));
            }
            return refs;
        }

        /// Where the reference's offset and size fields are: found by the path string, which
        /// they lie next to, and checked against the values the type tree read.
        static (int OffsetAt, int SizeAt, bool Size64) Locate(SerializedFile file, SerializedFile.ObjectInfo info, int start, int classId, string path, long offset, long size)
        {
            var r = file.OpenObject(info);
            var bytes = r.ReadSpan(info.ByteSize);
            var encoded = Encoding.UTF8.GetBytes(path);
            var needle = new byte[4 + encoded.Length];
            BinaryPrimitives.WriteInt32LittleEndian(needle, encoded.Length);
            encoded.CopyTo(needle, 4);
            int at = bytes.IndexOf(needle);
            if (at < 0 || bytes[(at + 1)..].IndexOf(needle) >= 0)
                throw new InvalidDataException($"{file.Name}:{info.PathId}: the stream path {path} is not in the object exactly once.");
            int offsetAt, sizeAt;
            bool size64;
            if (Classes.HasStreamingInfo(classId))
            {
                offsetAt = at - 12;
                sizeAt = at - 4;
                size64 = false;
                if (BinaryPrimitives.ReadInt64LittleEndian(bytes[offsetAt..]) != offset || BinaryPrimitives.ReadUInt32LittleEndian(bytes[sizeAt..]) != size)
                    throw new InvalidDataException($"{file.Name}:{info.PathId}: StreamingInfo does not lie before its path.");
            }
            else
            {
                offsetAt = (at + needle.Length + 3) & ~3;
                sizeAt = offsetAt + 8;
                size64 = true;
                if (BinaryPrimitives.ReadInt64LittleEndian(bytes[offsetAt..]) != offset || BinaryPrimitives.ReadInt64LittleEndian(bytes[sizeAt..]) != size)
                    throw new InvalidDataException($"{file.Name}:{info.PathId}: StreamedResource does not follow its source.");
            }
            return (start + offsetAt, start + sizeAt, size64);
        }
    }
}
