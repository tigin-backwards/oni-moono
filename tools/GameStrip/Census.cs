using OniMoono.Assets.Files;

namespace GameStrip
{
    /// The install's serialized files, standalone and in bundles, with their streamed references.
    sealed record DataFile(string Relative, string? BundleNode, SerializedFile File, byte[] Bytes, List<StreamRef> Refs);

    static class Install
    {
        /// The serialized files of the data folder (by relative path from the install).
        public static IEnumerable<string> SerializedFiles(string game)
        {
            var data = Path.Combine(game, "OxygenNotIncluded_Data");
            foreach (var path in Directory.EnumerateFiles(data).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (name is "globalgamemanagers" or "globalgamemanagers.assets" || name.StartsWith("level", StringComparison.Ordinal) || name.EndsWith(".assets", StringComparison.Ordinal))
                    yield return Path.GetRelativePath(game, path);
            }
            foreach (var name in new[] { "unity default resources", "unity_builtin_extra" })
                if (File.Exists(Path.Combine(data, "Resources", name))) yield return Path.Combine("OxygenNotIncluded_Data", "Resources", name);
        }

        /// The AssetBundles under StreamingAssets (UnityFS archives, found by their signature).
        public static IEnumerable<string> Bundles(string game)
        {
            var streaming = Path.Combine(game, "OxygenNotIncluded_Data", "StreamingAssets");
            foreach (var path in Directory.EnumerateFiles(streaming, "*", new EnumerationOptions { RecurseSubdirectories = true }).Order(StringComparer.Ordinal))
            {
                using var f = File.OpenRead(path);
                var head = new byte[8];
                if (f.Read(head) == 8 && head.AsSpan().SequenceEqual("UnityFS\0"u8)) yield return Path.GetRelativePath(game, path);
            }
        }

        /// A file's size, through symbolic links.
        public static long Length(string path)
        {
            var info = new FileInfo(path);
            return info.LinkTarget != null ? ((FileInfo)info.ResolveLinkTarget(true)!).Length : info.Length;
        }

        public static DataFile Load(string game, string relative)
        {
            var bytes = File.ReadAllBytes(Path.Combine(game, relative));
            var file = SerializedFile.Parse(Path.GetFileName(relative), bytes);
            return new DataFile(relative, null, file, bytes, StreamRefs.Find(file));
        }

        public static IEnumerable<DataFile> LoadBundle(string game, string relative, UnityFsArchive archive)
        {
            foreach (var node in archive.Nodes)
            {
                if (node.Path.EndsWith(".resS", StringComparison.Ordinal) || node.Path.EndsWith(".resource", StringComparison.Ordinal)) continue;
                var bytes = archive.Read(node);
                var file = SerializedFile.Parse(node.Path, bytes);
                yield return new DataFile(relative, node.Path, file, bytes, StreamRefs.Find(file));
            }
        }
    }

    static class Census
    {
        public static void Run(string game)
        {
            // resource file -> class -> (kept bytes, stripped bytes, objects)
            var totals = new SortedDictionary<string, SortedDictionary<string, long[]>>(StringComparer.Ordinal);
            var resourceSizes = new SortedDictionary<string, long>(StringComparer.Ordinal);
            var kept = new List<(string Where, StreamRef Ref)>();

            // serialized file -> class -> (bytes, objects)
            var objects = new SortedDictionary<string, Dictionary<string, long[]>>(StringComparer.Ordinal);
            var inlineReadable = new List<string>();

            void Count(string where, DataFile f)
            {
                var classes = objects[where + (f.BundleNode != null ? ":" + f.BundleNode : "")] = new();
                foreach (var info in f.File.Objects)
                {
                    int id = f.File.TypeOf(info).ClassId;
                    var name = OniMoono.Assets.TypeTrees.TypeTreeDatabase.TryGet(id, out var c) ? c.Name : id.ToString();
                    if (!classes.TryGetValue(name, out var t)) classes[name] = t = new long[2];
                    t[0] += info.ByteSize;
                    t[1]++;
                    if (id == Classes.Texture2D)
                    {
                        var tex = OniMoono.Assets.TypeTrees.TypeTreeReader.ReadNative(id, f.File.OpenObject(info));
                        if (tex.Bool("m_IsReadable") || tex.Struct("m_StreamData").String("path").Length == 0)
                            inlineReadable.Add($"{info.ByteSize,12:N0}  {tex.String("m_Name")} {tex.Int("m_Width")}x{tex.Int("m_Height")} readable={tex.Bool("m_IsReadable")} streamed={tex.Struct("m_StreamData").String("path").Length > 0} ({where})");
                    }
                }
                foreach (var r in f.Refs)
                {
                    var key = where + " -> " + r.Path;
                    if (!totals.TryGetValue(key, out var byClass)) totals[key] = byClass = new(StringComparer.Ordinal);
                    if (!byClass.TryGetValue(r.ClassName, out var t)) byClass[r.ClassName] = t = new long[4];
                    bool keep = Selection.Keeps(r);
                    t[keep ? 0 : 1] += r.Size;
                    t[keep ? 2 : 3]++;
                    if (keep) kept.Add((where, r));
                }
            }

            foreach (var relative in Install.SerializedFiles(game))
            {
                var f = Install.Load(game, relative);
                Count(relative, f);
            }
            foreach (var path in Directory.EnumerateFiles(Path.Combine(game, "OxygenNotIncluded_Data")))
                if (path.EndsWith(".resS", StringComparison.Ordinal) || path.EndsWith(".resource", StringComparison.Ordinal))
                    resourceSizes[Path.GetFileName(path)] = Install.Length(path);
            foreach (var relative in Install.Bundles(game))
            {
                var archive = UnityFsArchive.Open(Path.Combine(game, relative));
                foreach (var node in archive.Nodes)
                    if (node.Path.EndsWith(".resS", StringComparison.Ordinal) || node.Path.EndsWith(".resource", StringComparison.Ordinal))
                        resourceSizes[relative + ":" + node.Path] = node.Size;
                foreach (var f in Install.LoadBundle(game, relative, archive)) Count(relative, f);
                Console.WriteLine($"bundle {relative}: {Install.Length(Path.Combine(game, relative)):N0} bytes on disk, nodes {string.Join(", ", archive.Nodes.Select(n => $"{n.Path} {n.Size:N0}"))}");
            }

            Console.WriteLine();
            Console.WriteLine("streamed payloads (kept bytes / objects, stripped bytes / objects):");
            foreach (var (key, byClass) in totals)
            {
                Console.WriteLine(key);
                foreach (var (cls, t) in byClass)
                    Console.WriteLine($"  {cls,-16} kept {t[0],14:N0} / {t[2],5}   stripped {t[1],14:N0} / {t[3],5}");
            }
            Console.WriteLine();
            Console.WriteLine("resource files:");
            foreach (var (name, size) in resourceSizes) Console.WriteLine($"  {name,-60} {size,14:N0}");
            Console.WriteLine();
            Console.WriteLine("serialized objects by class (bytes / objects), classes over 1 MB:");
            foreach (var (file, byClass) in objects)
            {
                Console.WriteLine($"  {file}: {byClass.Values.Sum(t => t[0]):N0}");
                foreach (var (cls, t) in byClass.OrderByDescending(kv => kv.Value[0]).Where(kv => kv.Value[0] >= 1 << 20))
                    Console.WriteLine($"    {cls,-24} {t[0],14:N0} / {t[1]}");
            }
            Console.WriteLine();
            Console.WriteLine($"textures with inline texels or readable ({inlineReadable.Count}):");
            foreach (var line in inlineReadable.OrderDescending(StringComparer.Ordinal)) Console.WriteLine("  " + line);
            Console.WriteLine();
            Console.WriteLine($"kept payloads ({kept.Count}):");
            foreach (var (where, r) in kept.OrderByDescending(k => k.Ref.Size))
                Console.WriteLine($"  {r.Size,12:N0}  {r.ClassName} {r.Name}  ({where})");
        }
    }
}
