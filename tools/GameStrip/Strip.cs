using System.Buffers.Binary;
using System.Text;
using OniMoono.Assets.Files;

namespace GameStrip
{
    /// Writes the stripped copy (Program.cs).
    static class Strip
    {
        /// Kept payloads start at multiples of this in a rewritten resource file, as Unity aligns them.
        const int PayloadAlignment = 16;

        public static int Run(string game, string output)
        {
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            {
                Console.Error.WriteLine($"{output} is not empty");
                return 2;
            }
            var serialized = Install.SerializedFiles(game).ToHashSet(StringComparer.Ordinal);
            var bundles = Install.Bundles(game).ToHashSet(StringComparer.Ordinal);
            var report = new Report();

            // Resource files of the data folder: what each serialized file keeps of them, written
            // once all are read (several files may share one).
            var resources = new Dictionary<string, Payloads>(StringComparer.Ordinal);
            var patched = new List<DataFile>();
            foreach (var relative in serialized.Order(StringComparer.Ordinal))
            {
                var f = Install.Load(game, relative);
                foreach (var r in f.Refs)
                {
                    var file = Path.Combine(Path.GetDirectoryName(relative)!, r.Path);
                    if (!resources.TryGetValue(file, out var p)) resources[file] = p = new Payloads(Path.Combine(game, file));
                    Place(f, r, p, report);
                }
                patched.Add(f);
            }

            foreach (var relative in Files(game, ""))
            {
                var target = Path.Combine(output, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var source = Path.Combine(game, relative);
                if (serialized.Contains(relative)) File.WriteAllBytes(target, patched.First(f => f.Relative == relative).Bytes);
                else if (resources.TryGetValue(relative, out var payloads)) File.WriteAllBytes(target, payloads.Write());
                else if (IsResourceFile(relative)) File.WriteAllBytes(target, []); // nothing references it
                else if (bundles.Contains(relative)) File.WriteAllBytes(target, StripBundle(game, relative, report));
                else if (relative.EndsWith(".bank", StringComparison.Ordinal)) File.WriteAllBytes(target, Banks.Strip(File.ReadAllBytes(source), relative));
                else File.Copy(source, target);
                report.Sizes.Add((relative, Install.Length(source), new FileInfo(target).Length));
            }
            report.Write(Path.Combine(output, "OxygenNotIncluded_Data", Report.FileName));
            report.Print();
            return 0;
        }

        static bool IsResourceFile(string relative) => relative.EndsWith(".resS", StringComparison.Ordinal) || relative.EndsWith(".resource", StringComparison.Ordinal);

        /// The install's files to copy (relative paths), through symbolic links, without those
        /// Selection skips.
        static IEnumerable<string> Files(string game, string relative)
        {
            var dir = Path.Combine(game, relative);
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.Ordinal))
            {
                var rel = Path.GetRelativePath(game, entry);
                if (Selection.Skips(rel)) continue;
                if (Directory.Exists(entry))
                    foreach (var f in Files(game, rel)) yield return f;
                else yield return rel;
            }
        }

        /// Keeps or strips one payload, rewriting the reference in the file's bytes: a kept payload
        /// at its place in the new resource file, a stripped one as offset 0, size 0 (the path
        /// stays, so the engine's no-content mode knows the texels are missing, not inline).
        static void Place(DataFile f, StreamRef r, Payloads p, Report report)
        {
            long offset = 0, size = 0;
            if (Selection.Keeps(r))
            {
                offset = p.Keep(r.Offset, r.Size);
                size = r.Size;
            }
            BinaryPrimitives.WriteInt64LittleEndian(f.Bytes.AsSpan(r.OffsetAt), offset);
            if (r.Size64) BinaryPrimitives.WriteInt64LittleEndian(f.Bytes.AsSpan(r.SizeAt), size);
            else BinaryPrimitives.WriteUInt32LittleEndian(f.Bytes.AsSpan(r.SizeAt), checked((uint)size));
            report.Count(r, size != 0, f.Relative);
        }

        /// The payloads kept of one resource file, and where each lands in its rewritten copy.
        sealed class Payloads(string source)
        {
            readonly Dictionary<(long, long), long> placed = new();
            readonly List<(long From, long Size, long To)> ranges = new();
            long length;

            public byte[]? Source { get; init; }

            public long Keep(long offset, long size)
            {
                if (placed.TryGetValue((offset, size), out var at)) return at;
                at = (length + PayloadAlignment - 1) / PayloadAlignment * PayloadAlignment;
                ranges.Add((offset, size, at));
                placed[(offset, size)] = at;
                length = at + size;
                return at;
            }

            public byte[] Write()
            {
                var result = new byte[length];
                if (ranges.Count == 0) return result;
                if (Source != null)
                    foreach (var (from, size, to) in ranges) Source.AsSpan(checked((int)from), checked((int)size)).CopyTo(result.AsSpan(checked((int)to)));
                else
                {
                    using var s = File.OpenRead(source);
                    foreach (var (from, size, to) in ranges)
                    {
                        s.Position = from;
                        s.ReadExactly(result, checked((int)to), checked((int)size));
                    }
                }
                return result;
            }
        }

        /// A bundle with its serialized files patched and its resource nodes rewritten, stored
        /// uncompressed (the archive's compression would only cost time: the copy is compressed
        /// as a whole).
        static byte[] StripBundle(string game, string relative, Report report)
        {
            var bytes = File.ReadAllBytes(Path.Combine(game, relative));
            var archive = UnityFsArchive.Parse(bytes);
            var files = Install.LoadBundle(game, relative, archive).ToList();
            var resources = new Dictionary<string, Payloads>(StringComparer.Ordinal);
            foreach (var f in files)
                foreach (var r in f.Refs)
                {
                    var node = r.Path[(r.Path.LastIndexOf('/') + 1)..];
                    if (!resources.TryGetValue(node, out var p))
                        resources[node] = p = new Payloads("") { Source = archive.Read(archive.Find(node)) };
                    Place(f, r, p, report);
                }
            var nodes = new List<(string Path, uint Flags, byte[] Data)>();
            foreach (var node in archive.Nodes)
            {
                byte[] data = files.FirstOrDefault(f => f.BundleNode == node.Path)?.Bytes
                    ?? (resources.TryGetValue(node.Path, out var p) ? p.Write() : IsResourceFile(node.Path) ? [] : archive.Read(node));
                nodes.Add((node.Path, node.Flags, data));
            }
            return BundleWriter.Write(bytes, nodes);
        }
    }

    /// What the copy kept and dropped, written beside its data as moono-stripped.txt.
    sealed class Report
    {
        public const string FileName = "moono-stripped.txt";
        readonly SortedDictionary<string, long[]> byClass = new(StringComparer.Ordinal);
        public readonly List<string> Kept = new();
        public readonly List<(string Relative, long Before, long After)> Sizes = new();

        public void Count(StreamRef r, bool kept, string where)
        {
            if (!byClass.TryGetValue(r.ClassName, out var t)) byClass[r.ClassName] = t = new long[4];
            t[kept ? 0 : 1] += r.Size;
            t[kept ? 2 : 3]++;
            if (kept) Kept.Add($"{r.ClassName} {r.Name} ({where}, {r.Size:N0} bytes)");
        }

        IEnumerable<string> Lines()
        {
            yield return "A stripped copy of Oxygen Not Included for headless test runs (Moono's tools/GameStrip).";
            yield return "Run it with ONI_NO_CONTENT=1. Not for playing, and not to be published.";
            yield return "";
            yield return "streamed payloads: kept bytes / objects, stripped bytes / objects";
            foreach (var (cls, t) in byClass) yield return $"  {cls,-16} {t[0],14:N0} / {t[2],5}   {t[1],14:N0} / {t[3],5}";
            yield return $"kept payloads ({Kept.Count}):";
            foreach (var k in Kept) yield return "  " + k;
            yield return $"files: {Sizes.Count}, {Sizes.Sum(s => s.Before):N0} bytes before, {Sizes.Sum(s => s.After):N0} after; the largest after:";
            foreach (var (relative, before, after) in Sizes.OrderByDescending(s => s.After).Take(25))
                yield return $"  {after,14:N0}  (was {before,14:N0})  {relative}";
        }

        public void Write(string path) => File.WriteAllLines(path, Lines(), new UTF8Encoding(false));

        public void Print()
        {
            foreach (var line in Lines()) Console.WriteLine(line);
        }
    }
}
