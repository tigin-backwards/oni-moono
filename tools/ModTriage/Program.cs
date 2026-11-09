// ModTriage: assigns each Workshop mod its play-testing classes and flags from metadata alone
// (docs/research/mod-play-testing.md §3). Like ApiCheck, it reads assemblies with
// System.Reflection.Metadata and never loads them.
//
// usage: ModTriage [--ws DIR] [--out DIR] [--compat FILE] [--max-rank N] [--game DIR] [--build N]
//                  [--doc FILE] [ID...]
//        ModTriage --fingerprint PLib.dll...    print PLib's type names for plib-types.txt
//
// Without IDs, the mods are the ranked rows of docs/MOD_COMPATIBILITY.md up to --max-rank (750)
// that have a folder in out/ws. It writes out/modtriage/ID.json per mod and prints one table line
// per mod; --doc FILE writes the table into FILE below its "<!-- table -->" line instead.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModTriage;

var repo = FindRepo();
string ws = Path.Combine(repo, "out", "ws"), outDir = Path.Combine(repo, "out", "modtriage");
string compat = Path.Combine(repo, "docs", "MOD_COMPATIBILITY.md");
string gameDir = Environment.GetEnvironmentVariable("ONI_GAME_DIR") is { Length: > 0 } g ? g
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux");
string? doc = null;
int maxRank = 750;
int? build = null;
bool sharedMode = false;
var ids = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--ws": ws = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--compat": compat = args[++i]; break;
        case "--max-rank": maxRank = int.Parse(args[++i]); break;
        case "--game": gameDir = args[++i]; break;
        case "--build": build = int.Parse(args[++i]); break;
        case "--doc": doc = args[++i]; break;
        case "--fingerprint": return Fingerprint(args[(i + 1)..]);
        case "--shared-namespaces": sharedMode = true; break;
        case "-h" or "--help":
            Console.WriteLine("usage: ModTriage [--ws DIR] [--out DIR] [--compat FILE] [--max-rank N] [--game DIR] [--build N] [--doc FILE] [ID...]");
            return 0;
        default: ids.Add(args[i]); break;
    }
}

var managed = Path.Combine(gameDir, "OxygenNotIncluded_Data", "Managed");
using var game = Directory.Exists(managed) ? GameIndex.Load(managed) : GameIndex.Empty();
if (game.IsEmpty) Console.Error.WriteLine($"ModTriage: no game at {gameDir}; base chains stop at the mod, and patch targets map by name only");
build ??= game.Build;
if (sharedMode) return SharedNamespaces(ws, build, game);

var rows = ReadCompat(compat);
var mods = ids.Count > 0
    ? ids.Select(id => rows.FirstOrDefault(r => r.Id == id) ?? new CompatRow(null, id, null)).ToList()
    : rows.Where(r => r.Rank is { } rank && rank <= maxRank).ToList();

Directory.CreateDirectory(outDir);
var json = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DictionaryKeyPolicy = null,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};
var results = new List<(CompatRow row, ModFolder mod, Triage triage)>();
int missing = 0;
foreach (var row in mods)
{
    var root = Path.Combine(ws, row.Id);
    if (!Directory.Exists(root)) { missing++; continue; }
    var mod = ModFolder.Scan(row.Id, root, build, Fingerprints.Default, game);
    var triage = Classifier.Classify(mod, game);
    results.Add((row, mod, triage));
    File.WriteAllText(Path.Combine(outDir, row.Id + ".json"), JsonSerializer.Serialize(Record(row, mod, triage, ws, build), json) + "\n");
}

var table = Table(results);
if (doc != null)
{
    const string marker = "<!-- table -->";
    var text = File.Exists(doc) ? File.ReadAllText(doc) : "";
    var at = text.IndexOf(marker, StringComparison.Ordinal);
    var head = at >= 0 ? text[..(at + marker.Length)] : text.TrimEnd() + "\n\n" + marker;
    File.WriteAllText(doc, head + "\n\n" + table);
}
else Console.Write(table);
Console.Error.WriteLine($"ModTriage: {results.Count} mods triaged into {outDir}; {missing} listed mods have no folder in {ws}; game build {build?.ToString() ?? "unknown"}");
return 0;

static string FindRepo()
{
    for (var dir = Directory.GetCurrentDirectory(); dir != null; dir = Path.GetDirectoryName(dir))
        if (File.Exists(Path.Combine(dir, "OniMoono.slnx"))) return dir;
    return Directory.GetCurrentDirectory();
}

static List<CompatRow> ReadCompat(string path)
{
    var list = new List<CompatRow>();
    if (!File.Exists(path)) return list;
    var re = new Regex(@"^\| (\d+|–) \| \[(.*)\]\(https://steamcommunity\.com/sharedfiles/filedetails/\?id=(\d+)\) \|");
    foreach (var line in File.ReadLines(path))
    {
        var m = re.Match(line);
        if (m.Success) list.Add(new CompatRow(int.TryParse(m.Groups[1].Value, out var r) ? r : null, m.Groups[3].Value, m.Groups[2].Value));
    }
    return list;
}

static object Record(CompatRow row, ModFolder mod, Triage t, string ws, int? build)
{
    var own = mod.Assemblies.Where(a => a.WholeLibrary == Library.None).ToList();
    static SortedDictionary<string, SortedSet<string>> Groups(IEnumerable<CodeFacts> code)
    {
        var refs = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var m in code.SelectMany(c => c.RefMembers))
        {
            var type = m[..m.IndexOf("::", StringComparison.Ordinal)];
            if (Classifier.Group(type) is not { } group) continue;
            if (!refs.TryGetValue(group, out var set)) refs[group] = set = new(StringComparer.Ordinal);
            set.Add(m.Replace("::", "."));
        }
        foreach (var type in code.SelectMany(c => c.RefTypes))
            if (Classifier.Group(type) is { } group && !refs.ContainsKey(group)) refs[group] = new(StringComparer.Ordinal) { type };
        return refs;
    }
    // The base types §3.2 names, with the own types that derive from them (UtilLibs' left out).
    string[] notable = ["KScreen", "KModalScreen", "SideScreenContent", "InterfaceTool", "DragTool", "FilteredDragTool", "BrushTool",
        "IBuildingConfig", "IMultiBuildingConfig", "IEntityConfig", "IMultiEntityConfig", "KMod.UserMod2", "OverlayModes+Mode"];
    var bases = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
    foreach (var o in own.SelectMany(a => a.Types).Where(o => !o.Helper))
        foreach (var b in o.Bases.Concat(o.Interfaces).Distinct())
        {
            var key = notable.Contains(b) ? b : Names.Simple(b) == "FScreen" ? "FScreen" : null;
            if (key == null) continue;
            if (!bases.TryGetValue(key, out var list)) bases[key] = list = [];
            if (list.Count < 20) list.Add(o.Name);
        }
    return new
    {
        Id = mod.Id,
        Rank = row.Rank,
        Title = row.Title ?? mod.Title,
        StaticId = mod.StaticId,
        GameBuild = build,
        Version = Path.GetRelativePath(ws, mod.Chosen).Replace('\\', '/'),
        VersionNote = mod.ChosenNote,
        ModInfo = mod.Info,
        Content = mod.Content,
        Classes = t.Classes,
        Flags = t.Flags,
        Assemblies = mod.Assemblies.Select(a => new
        {
            File = Path.GetFileName(a.File),
            a.Name,
            Library = a.WholeLibrary == Library.None ? null : a.WholeLibrary.ToString(),
            Types = a.TypeCount,
            OwnTypes = a.Types.Count(o => !o.Helper),
            HelperTypes = a.Types.Count(o => o.Helper) is var h and > 0 ? h : (int?)null,
            Excluded = a.Excluded.Count == 0 ? null : a.Excluded,
            ExcludedNamespaces = a.ExcludedNamespaces.Count == 0 ? null : a.ExcludedNamespaces,
        }),
        Namespaces = own.SelectMany(a => a.Types).GroupBy(o => (o.Helper ? "helper:" : "") + Names.Namespace(o.Name))
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        BaseTypes = bases,
        Refs = Groups(own.Select(a => a.Mod)),
        HelperRefs = own.Any(a => a.Types.Any(o => o.Helper)) ? Groups(own.Select(a => a.Helper)) : null,
        Plib = own.SelectMany(a => a.Mod.PLibRefs).Distinct().Order(StringComparer.Ordinal),
        Patches = t.Patches,
        UnresolvedPatchCalls = own.Sum(a => a.UnresolvedPatchCalls),
    };
}

static string Table(List<(CompatRow row, ModFolder mod, Triage triage)> results)
{
    var sb = new System.Text.StringBuilder();
    // The distribution first: per class, and per class set.
    var perClass = Triage.Order.Select(c => (c, n: results.Count(r => r.triage.Classes.ContainsKey(c.ToString())))).ToList();
    sb.AppendLine($"**{results.Count} mods.** Per class (a mod counts once in each of its classes): "
        + string.Join(", ", perClass.Select(x => $"{x.c} {x.n}")) + ".");
    sb.AppendLine($"Flags: touches_ui {results.Count(r => r.triage.Flags.ContainsKey("touches_ui"))}, "
        + $"asset_bundle_ui {results.Count(r => r.triage.Flags.ContainsKey("asset_bundle_ui"))}, "
        + $"plib_options {results.Count(r => r.triage.Flags.ContainsKey("plib_options"))}, "
        + $"user_menu {results.Count(r => r.triage.Flags.ContainsKey("user_menu"))}.");
    sb.AppendLine();
    sb.AppendLine("| Class set | Mods |");
    sb.AppendLine("|---|---|");
    var sets = results.GroupBy(r => r.triage.ClassString).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
    foreach (var g in sets.Where(g => g.Count() >= 3)) sb.AppendLine($"| {g.Key} | {g.Count()} |");
    var rare = sets.Where(g => g.Count() < 3).ToList();
    if (rare.Count > 0) sb.AppendLine($"| {rare.Count} other sets | {rare.Sum(g => g.Count())} |");
    sb.AppendLine();
    sb.AppendLine("| Rank | ID | Mod | Classes | Flags | Patch targets by class |");
    sb.AppendLine("|---|---|---|---|---|---|");
    foreach (var (row, mod, t) in results)
    {
        var flags = string.Join(" ", new[] { "touches_ui", "asset_bundle_ui" }.Where(t.Flags.ContainsKey));
        var patches = string.Join("; ", t.Patches.Where(p => !p.Helper).GroupBy(p => p.Class).OrderBy(g => Triage.Order.IndexOf(g.Key[0])).Select(g =>
        {
            var names = g.Select(p => Names.OuterSimple(p.Type) + (p.Method is null ? "" : "." + p.Method)).Distinct().ToList();
            var shown = string.Join(", ", names.Take(3));
            return $"{g.Key} {names.Count}: {shown}{(names.Count > 3 ? ", …" : "")}";
        }));
        var title = (row.Title ?? mod.Title ?? row.Id).Replace("|", "/");
        if (title.Length > 48) title = title[..47] + "…";
        sb.AppendLine($"| {row.Rank?.ToString() ?? "–"} | {row.Id} | {title} | {t.ClassString} | {(flags.Length == 0 ? "–" : flags)} | {(patches.Length == 0 ? "–" : patches)} |");
    }
    return sb.ToString();
}

/// The namespaces at least three mods in the folder define (once merged libraries are removed),
/// with the median number of top-level types each puts there, for shared-namespaces.txt.
static int SharedNamespaces(string ws, int? build, GameIndex game)
{
    var plibOnly = new Fingerprints(Fingerprints.Default.PLib);
    var counts = new Dictionary<string, List<int>>(StringComparer.Ordinal);
    foreach (var dir in Directory.GetDirectories(ws).Order(StringComparer.Ordinal))
    {
        var mod = ModFolder.Scan(Path.GetFileName(dir), dir, build, plibOnly, game);
        var perMod = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in mod.Assemblies.Where(a => a.WholeLibrary == Library.None))
            foreach (var (ns, n) in a.OwnTopLevel)
                if (n > 0 && !Fingerprints.NeverShared(ns) && !a.EntryNamespaces.Contains(ns)) perMod[ns] = perMod.GetValueOrDefault(ns) + n;
        foreach (var (ns, n) in perMod)
        {
            if (!counts.TryGetValue(ns, out var list)) counts[ns] = list = [];
            list.Add(n);
        }
    }
    Console.WriteLine("# Namespaces that at least three Workshop mods in out/ws define, with the median number of");
    Console.WriteLine("# top-level types they hold (median<TAB>namespace): an author's shared code, merged into each");
    Console.WriteLine("# of their mods. From `ModTriage --shared-namespaces`; regenerate when out/ws grows.");
    foreach (var (ns, list) in counts.Where(kv => kv.Value.Count >= 3).OrderBy(kv => kv.Key, StringComparer.Ordinal))
    {
        list.Sort();
        Console.WriteLine($"{list[list.Count / 2]}\t{ns}");
    }
    return 0;
}

/// PLib's type names in the given DLLs, or in every DLL under the given folders: separate PLib
/// DLLs and merged copies alike, since only the PeterHan.PLib namespaces are read.
static int Fingerprint(string[] paths)
{
    var names = new SortedSet<(string area, string name)>();
    var dlls = paths.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.dll", SearchOption.AllDirectories) : [p]);
    foreach (var path in dlls)
    {
        using var pe = new PEReader(File.OpenRead(path));
        if (!pe.HasMetadata) continue;
        MetadataReader md;
        try { md = pe.GetMetadataReader(); }
        catch (BadImageFormatException) { continue; }
        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);
            if (!td.GetDeclaringType().IsNil) continue;
            var ns = md.GetString(td.Namespace);
            var name = md.GetString(td.Name);
            if (!ns.StartsWith("PeterHan.PLib", StringComparison.Ordinal) || name.StartsWith('<')) continue;
            var area = ns == "PeterHan.PLib" ? "Core" : ns["PeterHan.PLib.".Length..].Split('.')[0];
            names.Add((area, name));
        }
    }
    foreach (var (area, name) in names) Console.WriteLine($"{area}\t{name}");
    return 0;
}

sealed record CompatRow(int? Rank, string Id, string? Title);
