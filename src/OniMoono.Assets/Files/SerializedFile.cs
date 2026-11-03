using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OniMoono.Assets.IO;
using OniMoono.Assets.TypeTrees;

namespace OniMoono.Assets.Files
{
    /// A Unity SerializedFile (format version 22: scenes, .assets, bundle CABs; mods' bundles back
    /// to 17, Unity 2018.3).
    public sealed class SerializedFile
    {
        public sealed record SerializedType(int ClassId, bool IsStripped, short ScriptTypeIndex, TypeTreeNode? Tree);
        public sealed record ObjectInfo(long PathId, long ByteStart, int ByteSize, int TypeIndex);
        public sealed record External(string Path, Guid Guid, int Type);
        public sealed record ScriptTypeRef(int FileIndex, long PathId);

        public string Name { get; }
        public int Version { get; private set; }
        public string UnityVersion { get; private set; } = "";
        public int TargetPlatform { get; private set; }
        public bool EnableTypeTree { get; private set; }
        public List<SerializedType> Types { get; } = new();
        public List<ObjectInfo> Objects { get; } = new();
        public Dictionary<long, ObjectInfo> ObjectsById { get; } = new();
        public List<ScriptTypeRef> ScriptTypes { get; } = new();
        public List<External> Externals { get; } = new();

        // A bundle node's bytes: a window on its archive's decompressed data (UnityFsArchive.Slice).
        readonly ArraySegment<byte> bytes;
        readonly MappedFile? mapped;
        long dataOffset;
        bool bigEndianData;

        SerializedFile(string name, MappedFile mapped)
        {
            Name = name;
            this.mapped = mapped;
        }

        SerializedFile(string name, ArraySegment<byte> bytes)
        {
            Name = name;
            this.bytes = bytes;
        }

        /// A file on disk, mapped (files of 2 GB or more are not serialized files' sizes).
        public static SerializedFile Open(string path)
        {
            var mapped = new MappedFile(path);
            if (mapped.Length >= int.MaxValue) return Parse(Path.GetFileName(path), File.ReadAllBytes(path));
            var f = new SerializedFile(Path.GetFileName(path), mapped);
            f.ReadMetadata();
            return f;
        }

        DataReader Reader(int offset, int length)
        {
            if (mapped != null) return new DataReader(mapped, offset, length);
            if (offset < 0 || length < 0 || offset + length > bytes.Count) throw new ArgumentOutOfRangeException(nameof(length));
            return new DataReader(bytes.Array!, bytes.Offset + offset, length);
        }

        public static SerializedFile Parse(string name, byte[] bytes) => Parse(name, new ArraySegment<byte>(bytes));

        public static SerializedFile Parse(string name, ArraySegment<byte> bytes)
        {
            var f = new SerializedFile(name, bytes);
            f.ReadMetadata();
            return f;
        }

        void ReadMetadata()
        {
            var r = Reader(0, mapped != null ? checked((int)mapped.Length) : bytes.Count);
            r.BigEndian = true;
            r.ReadUInt32(); // metadata size (legacy)
            r.ReadUInt32(); // file size (legacy)
            Version = (int)r.ReadUInt32();
            dataOffset = r.ReadUInt32();
            // 17 to 21 (Unity 2018.3-2020.x: mods' older bundles) keep the 32-bit header; 22 added
            // 64-bit sizes after it. Type trees and reference types read the same from 20 on; before
            // 20 a file has no reference types and its types no dependencies, and before 19 a type
            // tree node has no reference type hash.
            if (Version < 17) throw new InvalidDataException($"{Name}: SerializedFile version {Version} is not supported (17+).");
            bigEndianData = r.ReadByte() != 0;
            r.Skip(3);
            if (Version >= 22)
            {
                r.ReadUInt32();          // metadata size
                r.ReadInt64();           // file size
                dataOffset = r.ReadInt64();
                r.ReadInt64();           // reserved
            }
            r.BigEndian = bigEndianData;

            UnityVersion = r.ReadCString();
            TargetPlatform = r.ReadInt32();
            EnableTypeTree = r.ReadBool();

            int typeCount = r.ReadInt32();
            for (int i = 0; i < typeCount; i++) Types.Add(ReadType(r, isRefType: false));

            int objectCount = r.ReadInt32();
            for (int i = 0; i < objectCount; i++)
            {
                r.Align(4);
                long pathId = r.ReadInt64();
                long byteStart = Version >= 22 ? r.ReadInt64() : r.ReadUInt32();
                var info = new ObjectInfo(pathId, byteStart, (int)r.ReadUInt32(), r.ReadInt32());
                Objects.Add(info);
                ObjectsById[info.PathId] = info;
            }

            int scriptCount = r.ReadInt32();
            for (int i = 0; i < scriptCount; i++)
            {
                int fileIndex = r.ReadInt32();
                r.Align(4);
                ScriptTypes.Add(new ScriptTypeRef(fileIndex, r.ReadInt64()));
            }

            int externalCount = r.ReadInt32();
            for (int i = 0; i < externalCount; i++)
            {
                r.ReadCString(); // asset path (unused in players)
                var guid = new Guid(r.ReadSpan(16));
                int type = r.ReadInt32();
                Externals.Add(new External(r.ReadCString(), guid, type));
            }

            if (Version >= 20)
            {
                int refTypeCount = r.ReadInt32();
                for (int i = 0; i < refTypeCount; i++) ReadType(r, isRefType: true);
            }
            r.ReadCString(); // user information
        }

        SerializedType ReadType(DataReader r, bool isRefType)
        {
            int classId = r.ReadInt32();
            bool stripped = r.ReadBool();
            short scriptIndex = r.ReadInt16();
            if (isRefType || classId == 114 || classId < 0 || scriptIndex >= 0) r.Skip(16); // script id
            r.Skip(16); // old type hash
            TypeTreeNode? tree = null;
            if (EnableTypeTree)
            {
                tree = ReadTypeTree(r, nodeHash: Version >= 19);
                if (isRefType)
                {
                    r.ReadCString(); r.ReadCString(); r.ReadCString(); // class, namespace, assembly
                }
                else if (Version >= 20)
                {
                    int depCount = r.ReadInt32();
                    r.Skip(depCount * 4);
                }
            }
            return new SerializedType(classId, stripped, scriptIndex, tree);
        }

        /// Blob format: node count, string buffer size, fixed-size nodes, string buffer. Name and
        /// type offsets with the top bit set index Unity's common strings table.
        static TypeTreeNode ReadTypeTree(DataReader r, bool nodeHash)
        {
            int nodeCount = r.ReadInt32();
            int stringSize = r.ReadInt32();
            var raw = new (ushort version, byte level, byte typeFlags, uint typeOffset, uint nameOffset, int byteSize, int index, uint metaFlag)[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                raw[i] = (r.ReadUInt16(), r.ReadByte(), r.ReadByte(), r.ReadUInt32(), r.ReadUInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadUInt32());
                if (nodeHash) r.ReadUInt64(); // ref type hash
            }
            var strings = r.ReadBytes(stringSize);
            string Str(uint offset)
            {
                if ((offset & 0x80000000) != 0) return CommonStrings.Get((int)(offset & 0x7FFFFFFF));
                int end = Array.IndexOf(strings, (byte)0, (int)offset);
                return Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset);
            }
            var stack = new Stack<(TypeTreeNode node, int level)>();
            TypeTreeNode root = null!;
            foreach (var n in raw)
            {
                var node = new TypeTreeNode { TypeName = Str(n.typeOffset), Name = Str(n.nameOffset), ByteSize = n.byteSize, Version = n.version, TypeFlags = n.typeFlags, MetaFlag = n.metaFlag };
                while (stack.Count > 0 && stack.Peek().level >= n.level) stack.Pop();
                if (stack.Count == 0) root = node;
                else stack.Peek().node.Children.Add(node);
                stack.Push((node, n.level));
            }
            return root;
        }

        /// A reader over one object's data.
        public DataReader OpenObject(ObjectInfo info)
        {
            var r = Reader(checked((int)(dataOffset + info.ByteStart)), info.ByteSize);
            r.BigEndian = bigEndianData;
            return r;
        }

        public SerializedType TypeOf(ObjectInfo info) => Types[info.TypeIndex];
    }
}
