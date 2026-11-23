using System.Buffers.Binary;
using OniMoono.Assets.Files;
using OniMoono.Assets.TypeTrees;

namespace ShaderSurvey;

/// `shader-refs DATADIR`: which objects of the game data reference each shader (d3d11-plan.md,
/// done criterion 1: the shaders the game never uses). A reference to an object is a PPtr, an
/// int32 file ID and an int64 path ID one after the other (4-byte aligned in the object's
/// bytes): file ID 0 for the object's own file, else one past the index of the shader's file in
/// the referencing file's externals. Every object of every serialized file of the data folder,
/// and of every UnityFS bundle under StreamingAssets, is searched for each shader's two values
/// at every 4-byte offset; each hit is counted by the referencing object's class ID (21
/// Material, 48 Shader, 114 MonoBehaviour, 30 GraphicsSettings, ...). A byte match that is not a
/// PPtr can only add hits, never hide one, so a shader with no hit is referenced by no object.
/// One line per shader: name, its file and path ID, then `CLASS:COUNT` pairs, or `none`.
static class ShaderRefs
{
    static readonly string[] dataFiles = ["level0", "level1", "level2", "globalgamemanagers", "globalgamemanagers.assets", "resources.assets", "sharedassets0.assets",
        "sharedassets1.assets", "sharedassets2.assets", "Resources/unity default resources", "Resources/unity_builtin_extra"];

    public static int Run(string dataDir, TextWriter o)
    {
        var files = new List<(string Name, SerializedFile File)>();
        foreach (var name in dataFiles)
        {
            var path = Path.Combine(dataDir, name);
            if (File.Exists(path)) files.Add((name, SerializedFile.Open(path)));
        }
        var streaming = Path.Combine(dataDir, "StreamingAssets");
        int bundles = 0;
        if (Directory.Exists(streaming))
            foreach (var path in Directory.EnumerateFiles(streaming, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (!IsUnityFs(path)) continue;
                bundles++;
                var archive = UnityFsArchive.Open(path);
                foreach (var node in archive.Nodes)
                {
                    if (node.Path.EndsWith(".resS", StringComparison.Ordinal) || node.Path.EndsWith(".resource", StringComparison.Ordinal)) continue;
                    try
                    {
                        files.Add((Path.GetRelativePath(dataDir, path) + ":" + node.Path, SerializedFile.Parse(node.Path, archive.Read(node))));
                    }
                    catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
                    {
                        // not a serialized file
                    }
                }
            }
        // Every shader: the file it sits in (by its name as externals give it), its path ID, its name.
        var shaders = new List<(string FileName, long PathId, string Name, string Where)>();
        foreach (var (name, file) in files)
            foreach (var info in file.Objects)
            {
                var type = file.TypeOf(info);
                if (type.ClassId != 48) continue;
                var fields = type.Tree != null ? TypeTreeReader.ReadObject(type.Tree, file.OpenObject(info), TypeTreeDatabase.TrailingBytes(48)) : TypeTreeReader.ReadNative(48, file.OpenObject(info));
                shaders.Add((Path.GetFileName(file.Name), info.PathId, Data.NameOf(fields), name));
            }
        var hits = shaders.Select(_ => new SortedDictionary<int, int>()).ToArray();
        foreach (var (name, file) in files)
        {
            // Each shader's file ID as this file writes it (-1: this file cannot reach it).
            var ids = new int[shaders.Count];
            for (int s = 0; s < shaders.Count; s++)
            {
                ids[s] = -1;
                if (string.Equals(Path.GetFileName(file.Name), shaders[s].FileName, StringComparison.OrdinalIgnoreCase)) ids[s] = 0;
                else
                    for (int e = 0; e < file.Externals.Count; e++)
                        if (string.Equals(Path.GetFileName(file.Externals[e].Path), shaders[s].FileName, StringComparison.OrdinalIgnoreCase))
                            ids[s] = e + 1;
            }
            var byPathId = new Dictionary<long, List<int>>();
            for (int s = 0; s < shaders.Count; s++)
                if (ids[s] >= 0)
                {
                    if (!byPathId.TryGetValue(shaders[s].PathId, out var list)) byPathId[shaders[s].PathId] = list = new();
                    list.Add(s);
                }
            foreach (var info in file.Objects)
            {
                int classId = file.TypeOf(info).ClassId;
                var bytes = file.OpenObject(info).ReadSpan(info.ByteSize);
                for (int at = 0; at + 12 <= bytes.Length; at += 4)
                {
                    long pathId = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(at + 4, 8));
                    if (!byPathId.TryGetValue(pathId, out var candidates)) continue;
                    int fileId = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at, 4));
                    foreach (var s in candidates)
                    {
                        if (ids[s] != fileId) continue;
                        // A shader's own bytes hold its own path ID in no PPtr of note.
                        if (classId == 48 && fileId == 0 && info.PathId == shaders[s].PathId) continue;
                        hits[s].TryGetValue(classId, out int n);
                        hits[s][classId] = n + 1;
                    }
                }
            }
        }
        o.WriteLine($"# {shaders.Count} shaders; {files.Count} serialized files ({bundles} bundles under StreamingAssets)");
        for (int s = 0; s < shaders.Count; s++)
            o.WriteLine($"{shaders[s].Name}\t{shaders[s].Where}#{shaders[s].PathId}\t{(hits[s].Count == 0 ? "none" : string.Join(" ", hits[s].Select(kv => $"{kv.Key}:{kv.Value}")))}");
        return 0;
    }

    static bool IsUnityFs(string path)
    {
        using var f = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8];
        return f.Read(head) == 8 && head.SequenceEqual("UnityFS\0"u8);
    }
}
