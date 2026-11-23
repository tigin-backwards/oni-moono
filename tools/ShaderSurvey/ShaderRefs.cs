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

    /// With shader names after DATADIR, each named shader's referencing objects are also
    /// described, two levels deep (what references the shader, and what references that): a
    /// Material by its name, a MonoBehaviour by its script's class and its GameObject's name, a
    /// GameObject by its name, the ResourceManager by the Resources paths that name the object,
    /// a ShaderNameRegistry or GraphicsSettings by its class.
    public static int Run(string dataDir, TextWriter o, IReadOnlyList<string>? detail = null)
    {
        var files = Load(dataDir, out int bundles);
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
        if (detail is { Count: > 0 }) { Detail(files, shaders.Select(x => (x.FileName, x.PathId, x.Name)).ToList(), detail, o); return 0; }
        o.WriteLine($"# {shaders.Count} shaders; {files.Count} serialized files ({bundles} bundles under StreamingAssets)");
        for (int s = 0; s < shaders.Count; s++)
            o.WriteLine($"{shaders[s].Name}\t{shaders[s].Where}#{shaders[s].PathId}\t{(hits[s].Count == 0 ? "none" : string.Join(" ", hits[s].Select(kv => $"{kv.Key}:{kv.Value}")))}");
        return 0;
    }

    /// `script-uses DATADIR CLASS...`: every MonoBehaviour of the data (files and bundles, as
    /// Run reads them) whose script's class (namespace.class, or the class alone) is one of
    /// CLASS, with its GameObject and that object's root; a class with none says so. Whether a
    /// component exists in the shipped scenes and prefabs is what this answers, not whether
    /// code adds one at run time.
    public static int ScriptUses(string dataDir, IReadOnlyList<string> classes, TextWriter o)
    {
        var files = Load(dataDir, out _);
        var found = classes.ToDictionary(c => c, _ => 0);
        foreach (var (_, file) in files)
            foreach (var info in file.Objects)
            {
                if (file.TypeOf(info).ClassId != 114) continue;
                var r = file.OpenObject(info);
                var go = new PPtr(r.ReadInt32(), r.ReadInt64());
                r.ReadByte();
                r.Align(4);
                var script = new PPtr(r.ReadInt32(), r.ReadInt64());
                string full = NameAt(files, file, script, 115, f => f.String("m_Namespace").Length > 0 ? f.String("m_Namespace") + "." + f.String("m_ClassName") : f.String("m_ClassName"));
                string bare = full[(full.LastIndexOf('.') + 1)..];
                foreach (var c in classes)
                    if (c == full || c == bare)
                    {
                        found[c]++;
                        o.WriteLine($"{c}\t{(go.IsNull ? "(no GameObject)" : $"GameObject '{GameObjectName(files, file, go)}'")} ({Path.GetFileName(file.Name)}#{info.PathId})");
                    }
            }
        foreach (var (c, n) in found)
            if (n == 0) o.WriteLine($"{c}\tnone");
        return 0;
    }

    /// `materials DATADIR`: every Material of the data with its shader's name, one per line.
    public static int Materials(string dataDir, TextWriter o)
    {
        var files = Load(dataDir, out _);
        var shaderNames = new Dictionary<(string, long), string>();
        foreach (var (_, file) in files)
            foreach (var info in file.Objects)
                if (file.TypeOf(info).ClassId == 48)
                {
                    var type = file.TypeOf(info);
                    var fields = type.Tree != null ? TypeTreeReader.ReadObject(type.Tree, file.OpenObject(info), TypeTreeDatabase.TrailingBytes(48)) : TypeTreeReader.ReadNative(48, file.OpenObject(info));
                    shaderNames[(Path.GetFileName(file.Name).ToLowerInvariant(), info.PathId)] = Data.NameOf(fields);
                }
        foreach (var (_, file) in files)
            foreach (var info in file.Objects)
            {
                if (file.TypeOf(info).ClassId != 21) continue;
                var f = TypeTreeReader.ReadFields(TypeTreeDatabase.Get(21).Tree!, file.OpenObject(info));
                var shader = f.PPtr("m_Shader");
                var fn = FileOf(file, shader.FileId)?.ToLowerInvariant();
                string name = shader.IsNull ? "(none)" : fn != null && shaderNames.TryGetValue((fn, shader.PathId), out var n) ? n : $"? ({fn}#{shader.PathId})";
                o.WriteLine($"{f.String("m_Name")}\t{name}\t{Path.GetFileName(file.Name)}#{info.PathId}");
            }
        return 0;
    }

    static void Detail(List<(string Name, SerializedFile File)> files, List<(string FileName, long PathId, string Name)> shaders, IReadOnlyList<string> names, TextWriter o)
    {
        foreach (var name in names)
            foreach (var (fileName, pathId, shaderName) in shaders)
            {
                if (shaderName != name) continue;
                o.WriteLine($"{shaderName}\t{fileName}#{pathId}");
                foreach (var (file, info) in Referrers(files, fileName, pathId))
                {
                    o.WriteLine($"  {Describe(files, file, info, fileName, pathId)}");
                    var ownName = Path.GetFileName(file.Name);
                    int cls = file.TypeOf(info).ClassId;
                    // The second level: what holds a material or a MonoBehaviour's GameObject.
                    // Byte matches of a small path ID in a texture, text or UI component are
                    // mostly not PPtrs: more than three of one kind are given as a count.
                    if (cls == 21)
                    {
                        var seconds = Referrers(files, ownName, info.PathId).Select(x => Describe(files, x.File, x.Info, ownName, info.PathId)).ToList();
                        foreach (var group in seconds.GroupBy(Kind))
                            if (group.Count() > 3) o.WriteLine($"    {group.Count()} x {group.Key} (byte matches, mostly not PPtrs)");
                            else foreach (var line in group) o.WriteLine($"    {line}");
                    }
                }
            }
    }

    /// Every object of every file whose bytes hold a PPtr to (fileName, pathId), as Run counts them.
    static IEnumerable<(SerializedFile File, SerializedFile.ObjectInfo Info)> Referrers(List<(string Name, SerializedFile File)> files, string fileName, long pathId)
    {
        foreach (var (_, file) in files)
        {
            int id = -1;
            if (string.Equals(Path.GetFileName(file.Name), fileName, StringComparison.OrdinalIgnoreCase)) id = 0;
            else
                for (int e = 0; e < file.Externals.Count; e++)
                    if (string.Equals(Path.GetFileName(file.Externals[e].Path), fileName, StringComparison.OrdinalIgnoreCase)) id = e + 1;
            if (id < 0) continue;
            foreach (var info in file.Objects)
            {
                if (id == 0 && info.PathId == pathId) continue;
                var bytes = file.OpenObject(info).ReadSpan(info.ByteSize);
                for (int at = 0; at + 12 <= bytes.Length; at += 4)
                    if (BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(at + 4, 8)) == pathId && BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at, 4)) == id)
                    {
                        yield return (file, info);
                        break;
                    }
            }
        }
    }

    static string Describe(List<(string Name, SerializedFile File)> files, SerializedFile file, SerializedFile.ObjectInfo info, string targetFile, long targetId)
    {
        int cls = file.TypeOf(info).ClassId;
        string where = $"{Path.GetFileName(file.Name)}#{info.PathId}";
        string className = TypeTreeDatabase.TryGet(cls, out var ci) ? ci.Name : cls.ToString();
        try
        {
            switch (cls)
            {
                case 21:
                {
                    var f = TypeTreeReader.ReadFields(TypeTreeDatabase.Get(21).Tree!, file.OpenObject(info));
                    return $"Material '{f.String("m_Name")}' ({where})";
                }
                case 1:
                {
                    var f = TypeTreeReader.ReadFields(TypeTreeDatabase.Get(1).Tree!, file.OpenObject(info));
                    return $"GameObject '{f.String("m_Name")}' ({where})";
                }
                case 114:
                {
                    var r = file.OpenObject(info);
                    var go = new PPtr(r.ReadInt32(), r.ReadInt64());
                    r.ReadByte();
                    r.Align(4);
                    var script = new PPtr(r.ReadInt32(), r.ReadInt64());
                    int n = r.ReadInt32();
                    var own = System.Text.Encoding.UTF8.GetString(r.ReadSpan(n));
                    string scriptName = NameAt(files, file, script, 115, f => f.String("m_ClassName"));
                    string goName = GameObjectName(files, file, go);
                    return $"MonoBehaviour {scriptName} on GameObject '{goName}'{(own.Length > 0 ? $" named '{own}'" : "")} ({where})";
                }
                case 147:
                {
                    var f = TypeTreeReader.ReadFields(TypeTreeDatabase.Get(147).Tree!, file.OpenObject(info));
                    var paths = new List<string>();
                    foreach (var e in f.Array("m_Container"))
                    {
                        var pair = (Fields)e!;
                        var ptr = pair.PPtr("second");
                        if (ptr.PathId == targetId && FileOf(file, ptr.FileId) is string fn && string.Equals(fn, targetFile, StringComparison.OrdinalIgnoreCase))
                            paths.Add(pair.String("first"));
                    }
                    return paths.Count > 0 ? $"ResourceManager, Resources path(s) {string.Join(", ", paths.Select(x => $"'{x}'"))}" : "ResourceManager (a dependency, no path)";
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidCastException or ArgumentException or IndexOutOfRangeException)
        {
            return $"{className} ({where}, unread: {e.Message})";
        }
        // A component (its first field m_GameObject): its GameObject.
        if (TypeTreeDatabase.TryGet(cls, out var c) && c.Tree is { Children.Count: > 0 } tree && tree.Children[0].Name == "m_GameObject")
        {
            var r = file.OpenObject(info);
            var go = new PPtr(r.ReadInt32(), r.ReadInt64());
            return $"{className} on GameObject '{GameObjectName(files, file, go)}' ({where})";
        }
        return $"{className} ({where})";
    }

    /// A description's kind: its class, and for a MonoBehaviour its script.
    static string Kind(string description)
    {
        int on = description.IndexOf(" on GameObject", StringComparison.Ordinal);
        if (on > 0) return description[..on];
        int paren = description.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? description[..paren] : description;
    }

    /// A GameObject's name, with its root's (through its Transform's m_Father) when it has a parent.
    static string GameObjectName(List<(string Name, SerializedFile File)> files, SerializedFile from, PPtr go)
    {
        var (file, info) = Resolve(files, from, go);
        if (file == null) return $"? ({FileOf(from, go.FileId)}#{go.PathId}, not loaded)";
        try
        {
            var f = TypeTreeReader.ReadFields(TypeTreeDatabase.Get(1).Tree!, file.OpenObject(info!));
            string name = f.String("m_Name");
            var components = f.Array("m_Component");
            if (components.Length == 0) return name;
            // The first component is the Transform (or RectTransform).
            var transform = ((Fields)components[0]!).PPtr("component");
            string root = name;
            for (int depth = 0; depth < 64; depth++)
            {
                var (tf, ti) = Resolve(files, file, transform);
                if (tf == null) break;
                var t = TypeTreeReader.ReadFields(TypeTreeDatabase.Get(tf.TypeOf(ti!).ClassId).Tree!, tf.OpenObject(ti!));
                var father = t.PPtr("m_Father");
                if (father.IsNull)
                {
                    var (gf, gi) = Resolve(files, tf, t.PPtr("m_GameObject"));
                    if (gf != null) root = TypeTreeReader.ReadFields(TypeTreeDatabase.Get(1).Tree!, gf.OpenObject(gi!)).String("m_Name");
                    break;
                }
                transform = father;
                file = tf;
            }
            return root == name ? name : $"{name}' under '{root}";
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidCastException or ArgumentException or IndexOutOfRangeException)
        {
            return $"? ({FileOf(from, go.FileId)}#{go.PathId}, unread)";
        }
    }

    static (SerializedFile? File, SerializedFile.ObjectInfo? Info) Resolve(List<(string Name, SerializedFile File)> files, SerializedFile from, PPtr ptr)
    {
        var fn = FileOf(from, ptr.FileId);
        foreach (var (_, f) in files)
            if (string.Equals(Path.GetFileName(f.Name), fn, StringComparison.OrdinalIgnoreCase) && f.ObjectsById.TryGetValue(ptr.PathId, out var info))
                return (f, info);
        return (null, null);
    }

    static string? FileOf(SerializedFile file, int fileId) => fileId == 0 ? Path.GetFileName(file.Name) : fileId - 1 < file.Externals.Count ? Path.GetFileName(file.Externals[fileId - 1].Path) : null;

    static string NameAt(List<(string Name, SerializedFile File)> files, SerializedFile from, PPtr ptr, int classId, Func<Fields, string> name)
    {
        var fn = FileOf(from, ptr.FileId);
        foreach (var (_, f) in files)
            if (string.Equals(Path.GetFileName(f.Name), fn, StringComparison.OrdinalIgnoreCase) && f.ObjectsById.TryGetValue(ptr.PathId, out var info))
            {
                try { return name(TypeTreeReader.ReadFields(TypeTreeDatabase.Get(classId).Tree!, f.OpenObject(info))); }
                catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidCastException or ArgumentException) { return $"? ({fn}#{ptr.PathId})"; }
            }
        return $"? ({fn}#{ptr.PathId}, not loaded)";
    }

    static List<(string Name, SerializedFile File)> Load(string dataDir, out int bundles)
    {
        var files = new List<(string Name, SerializedFile File)>();
        foreach (var name in dataFiles)
        {
            var path = Path.Combine(dataDir, name);
            if (File.Exists(path)) files.Add((name, SerializedFile.Open(path)));
        }
        var streaming = Path.Combine(dataDir, "StreamingAssets");
        bundles = 0;
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
        return files;
    }

    static bool IsUnityFs(string path)
    {
        using var f = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8];
        return f.Read(head) == 8 && head.SequenceEqual("UnityFS\0"u8);
    }
}
