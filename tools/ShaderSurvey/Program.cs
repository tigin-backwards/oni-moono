// Surveys the compiled shader programs of a Unity player data folder, for every graphics API the
// build carries (docs/research/macos-feasibility.md §1):
//   summary DATADIR             each shader's platforms (ShaderCompilerPlatform: entries/bytes) and
//                               player subprograms by GPU program type, then totals
//   variants DATADIR            one line per player subprogram: shader, subshader/pass, stage,
//                               GPU program type, keywords, program hash and length, parameter blob
//   dump DATADIR NAME|* DIR PLATFORM
//                               every program and parameter blob of PLATFORM (14 Metal, 15 GLCore)
//                               as files, with map.txt naming each subprogram's blobs
//   params DATADIR NAME PLATFORM
//                               each subprogram's parameters (common and blob merged), as text
//   check-metal DATADIR         Metal reflection against the MSL: every texture, sampler and
//                               constant buffer binding the parameter blob and the pass's common
//                               parameters give must be the slot the MSL declares, and every MSL
//                               slot must be accounted for; every vertex input of the attribute
//                               table must be the attribute the MSL declares for its channel
//   compare-metal DATADIR       Metal parameters against GLCore's (MetalCompare.cs)
//   d3d11 DATADIR               every D3D11 (platform 4) program read and checked against its
//                               parameters, inputs and Vulkan variants (D3d11Survey.cs)
//   d3d11-bundles WSDIR         the same for the mod bundles under WSDIR (read only)
//   shader-refs DATADIR         each shader with the objects (by class ID) whose bytes hold a
//                               reference to it, data files and StreamingAssets bundles (ShaderRefs.cs);
//                               with shader names after DATADIR, those shaders' referencing objects
//                               described two levels deep
//   materials DATADIR           every material with its shader's name (ShaderRefs.cs)
//   script-uses DATADIR CLASS...  each MonoBehaviour of those script classes in the data, with
//                               its GameObject and root (ShaderRefs.cs)
//   dxbc DATADIR                each Direct3D 11 (platform 4) subprogram's DXBC container checksum
//                               (bytes 4..19 after the magic), with shader, pass, stage and keywords:
//                               names the shaders a running player creates by their bytecode
//   states DATADIR              each pass's fixed-function state as serialized: cull, ZTest,
//                               ZWrite, Offset factor and units (constant, or [property])
// It reads the shipped release type trees (OniMoono.Assets), so it needs no engine or device.
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OniMoono.Assets.Files;
using OniMoono.Assets.Shaders;
using OniMoono.Assets.TypeTrees;
using ShaderSurvey;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ShaderSurvey summary|variants|check-metal|compare-metal|d3d11 DATADIR | ShaderSurvey d3d11-bundles WSDIR | ShaderSurvey dump DATADIR NAME|* DIR PLATFORM | ShaderSurvey params DATADIR NAME PLATFORM | ShaderSurvey shader-refs DATADIR");
    return 2;
}
string mode = args[0], dataDir = args[1];

switch (mode)
{
    case "summary": Summary(); return 0;
    case "states": States(); return 0;
    case "dxbc": Dxbc(); return 0;
    case "variants": Variants(); return 0;
    case "dump": Dump(args[2], args[3], uint.Parse(args[4])); return 0;
    case "params": Params(args[2], uint.Parse(args[3])); return 0;
    case "check-metal": return CheckMetal();
    case "vulkan": return VulkanSurvey.Run(dataDir, args[2], Console.Out);
    case "bundles": return BundleSurvey.Run(dataDir, Console.Out);
    case "d3d11": return D3d11Survey.RunGame(dataDir, Console.Out);
    case "d3d11-bundles": return D3d11Survey.RunBundles(dataDir, Console.Out);
    case "script-uses": return ShaderRefs.ScriptUses(dataDir, args.Skip(2).ToList(), Console.Out);
    case "materials": return ShaderRefs.Materials(dataDir, Console.Out);
    case "shader-refs": return ShaderRefs.Run(dataDir, Console.Out, args.Skip(2).ToList());
    case "compare-metal": return MetalCompare.Run(Data.Shaders(dataDir), Console.Out, args.Length > 2 ? args[2] : null);
    default: Console.Error.WriteLine("unknown mode " + mode); return 2;
}

void Dxbc()
{
    foreach (var (_, shader) in Data.Shaders(dataDir))
    {
        var blobs = Data.Table(shader, 4);
        if (blobs.Count == 0) continue;
        // The Direct3D 11 program types are the ones GpuProgramTypes does not place (the game's
        // shaders carry only Direct3D 11, GLCore and Vulkan programs).
        foreach (var x in Data.Subprograms(shader).Where(x => x.Sub.Platform == 0))
        {
            if (!blobs.Contains(x.Sub.BlobIndex)) continue;
            var bytes = blobs.Table!.Get(x.Sub.BlobIndex).ToArray();
            int at = bytes.AsSpan().IndexOf("DXBC"u8);
            string sum = at >= 0 && at + 20 <= bytes.Length ? Convert.ToHexString(bytes, at + 4, 16).ToLowerInvariant() : "-";
            Console.WriteLine($"{sum}\t{Data.NameOf(shader)}\t{x.Id}\t{x.Sub.Stage}\ttype={x.Sub.GpuProgramType}\t{Data.KeywordKey(x.Sub.Keywords)}");
        }
    }
}

void States()
{
    static string V(Fields f) { var name = f.String("name"); return name.Length == 0 || name == "<noninit>" ? f.Float("val").ToString("R") : $"[{name}]={f.Float("val"):R}"; }
    foreach (var (file, shader) in Data.Shaders(dataDir))
    {
        int s = 0;
        foreach (var sub in shader.Struct("m_ParsedForm").Array("m_SubShaders").Cast<Fields>())
        {
            int p = 0;
            foreach (var pass in sub.Array("m_Passes").Cast<Fields>())
            {
                var st = pass.Struct("m_State");
                Console.WriteLine($"{file}\t{Data.NameOf(shader)}\ts{s}p{p}\tcull={V(st.Struct("culling"))} ztest={V(st.Struct("zTest"))} zwrite={V(st.Struct("zWrite"))} offsetFactor={V(st.Struct("offsetFactor"))} offsetUnits={V(st.Struct("offsetUnits"))}");
                p++;
            }
            s++;
        }
    }
}

void Summary()
{
    var totals = new SortedDictionary<string, int>(StringComparer.Ordinal);
    int count = 0;
    foreach (var (file, shader) in Data.Shaders(dataDir))
    {
        count++;
        var platforms = shader.Get<uint[]>("platforms");
        var info = platforms.Select(p => { var b = Data.Table(shader, p); return $"{p}:{b.Count}e/{b.Bytes}B"; });
        var types = Data.Subprograms(shader).GroupBy(x => x.Sub.GpuProgramType).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}").ToArray();
        Console.WriteLine($"{file}\t{Data.NameOf(shader)}\tplatforms={string.Join(" ", info)}\tsubprograms={string.Join(" ", types)}");
        foreach (var p in platforms) totals[$"platform{p}"] = totals.GetValueOrDefault($"platform{p}") + 1;
        foreach (var x in Data.Subprograms(shader)) totals[$"type{x.Sub.GpuProgramType}"] = totals.GetValueOrDefault($"type{x.Sub.GpuProgramType}") + 1;
    }
    Console.WriteLine($"total shaders={count} " + string.Join(" ", totals.Select(kv => kv.Key + "=" + kv.Value)));
}

void Variants()
{
    foreach (var (_, shader) in Data.Shaders(dataDir))
    {
        var tables = new Dictionary<uint, Data.Platform>();
        foreach (var x in Data.Subprograms(shader))
        {
            uint platform = x.Sub.Platform;
            if (!tables.TryGetValue(platform, out var blobs)) tables[platform] = blobs = Data.Table(shader, platform);
            string hash = "-";
            int length = 0;
            if (blobs.Contains(x.Sub.BlobIndex))
            {
                var bytes = blobs.Table!.Get(x.Sub.BlobIndex);
                hash = Convert.ToHexString(SHA1.HashData(bytes))[..12];
                length = bytes.Length;
            }
            Console.WriteLine($"{Data.NameOf(shader)}\t{x.Id}\t{x.Sub.Stage}\t{x.Sub.GpuProgramType}\t{Data.KeywordKey(x.Sub.Keywords)}\t{hash}\t{length}\t{x.Sub.ParameterBlobIndex}");
        }
    }
}

void Dump(string name, string outDir, uint platform)
{
    foreach (var (_, shader) in Data.Shaders(dataDir))
    {
        if (name != "*" && Data.NameOf(shader) != name) continue;
        var blobs = Data.Table(shader, platform);
        if (blobs.Count == 0) continue;
        var dir = Path.Combine(outDir, string.Join("_", Data.NameOf(shader).Split(Path.GetInvalidFileNameChars().Append('/').ToArray())));
        Directory.CreateDirectory(dir);
        for (int i = 0; i < blobs.Count; i++)
            if (blobs.Contains(i)) File.WriteAllBytes(Path.Combine(dir, $"blob{i}.bin"), blobs.Table!.Get(i).ToArray());
        File.WriteAllLines(Path.Combine(dir, "map.txt"), Data.Subprograms(shader).Where(x => x.Sub.Platform == platform)
            .Select(x => $"{x.Id} {x.Sub.Stage} type={x.Sub.GpuProgramType} blob={x.Sub.BlobIndex} parameters={x.Sub.ParameterBlobIndex} keywords={Data.KeywordKey(x.Sub.Keywords)}"));
        Console.WriteLine($"{Data.NameOf(shader)}: {blobs.Count} blobs -> {dir}");
    }
}

void Params(string name, uint platform)
{
    foreach (var (_, shader) in Data.Shaders(dataDir))
    {
        if (Data.NameOf(shader) != name) continue;
        var blobs = Data.Table(shader, platform);
        foreach (var x in Data.Subprograms(shader).Where(x => x.Sub.Platform == platform))
        {
            Console.WriteLine($"{x.Id} {x.Sub.Stage} type={x.Sub.GpuProgramType} blob={x.Sub.BlobIndex} parameters={x.Sub.ParameterBlobIndex} [{Data.KeywordKey(x.Sub.Keywords)}]");
            var p = Data.Parameters(x, blobs);
            foreach (var cb in p.ConstantBuffers)
            {
                Console.WriteLine($"  cbuffer '{cb.Name}' size={cb.Size}");
                foreach (var m in cb.Members)
                    Console.WriteLine($"    {m.Name} type={m.Type} {m.Rows}x{m.Columns}{(m.IsMatrix ? " matrix" : "")} array={m.ArraySize} @{m.Offset}");
            }
            foreach (var t in p.Textures) Console.WriteLine($"  texture {t.Name} t{t.Index} s{t.SamplerIndex} dim={t.Dimension}{(t.MultiSampled ? " ms" : "")}");
            foreach (var b in p.ConstantBufferBindings) Console.WriteLine($"  cbuffer-binding {b.Name} b{b.Index} array={b.ArraySize}");
            foreach (var b in p.StructuredBuffers) Console.WriteLine($"  buffer {b.Name} b{b.Index} array={b.ArraySize}");
        }
    }
}

int CheckMetal()
{
    int programs = 0, failures = 0;
    var slot = new Regex(@"(?:texture\w*<[^>]*>|depth\w*<[^>]*>|sampler|constant \w+&|(?:const )?device \w+\s*\*)\s*(\w+)\s*\[\[\s*(texture|sampler|buffer)\s*\((\d+)\)\s*\]\]");
    var attribute = new Regex(@"\b(\w+?)(\d*)\s*\[\[\s*attribute\s*\((\d+)\)\s*\]\]");
    foreach (var (_, shader) in Data.Shaders(dataDir))
    {
        var blobs = Data.Table(shader, ShaderCompilerPlatform.Metal);
        if (blobs.Count == 0) continue;
        foreach (var x in Data.Subprograms(shader).Where(x => x.Sub.Platform == ShaderCompilerPlatform.Metal))
        {
            programs++;
            if (!blobs.Contains(x.Sub.BlobIndex) || (x.Sub.ParameterBlobIndex >= 0 && !blobs.Contains(x.Sub.ParameterBlobIndex)))
            {
                failures++;
                Console.WriteLine($"{Data.NameOf(shader)} {x.Id} {x.Sub.Stage} [{Data.KeywordKey(x.Sub.Keywords)}]: blob {x.Sub.BlobIndex} or {x.Sub.ParameterBlobIndex} not among the platform's {blobs.Count}");
                continue;
            }
            string? error = null;
            MetalProgram program;
            try { program = MetalProgram.Read(blobs.Table!.Get(x.Sub.BlobIndex)); }
            catch (InvalidDataException e)
            {
                failures++;
                Console.WriteLine($"{Data.NameOf(shader)} {x.Id} {x.Sub.Stage} [{Data.KeywordKey(x.Sub.Keywords)}]: {e.Message}");
                continue;
            }
            if (program.GpuProgramType != x.Sub.GpuProgramType) error ??= $"program type {program.GpuProgramType}, subprogram {x.Sub.GpuProgramType}";
            if (Data.KeywordKey(program.Keywords) != Data.KeywordKey(x.Sub.Keywords)) error ??= $"program keywords [{Data.KeywordKey(program.Keywords)}]";
            var declared = new Dictionary<(string Kind, int Slot), string>();
            foreach (Match m in slot.Matches(program.Source)) declared[(m.Groups[2].Value, int.Parse(m.Groups[3].Value))] = m.Groups[1].Value;
            var expected = new Dictionary<(string Kind, int Slot), string>();
            ShaderParameters parameters;
            try { parameters = Data.Parameters(x, blobs); }
            catch (InvalidDataException e) { parameters = new ShaderParameters(); error ??= e.Message; }
            foreach (var t in parameters.Textures)
            {
                expected[("texture", t.Index)] = t.Name;
                // A negative sampler index: the texture is only read (texture.read), no sampler.
                if (t.SamplerIndex >= 0) expected[("sampler", t.SamplerIndex)] = t.Name;
            }
            foreach (var b in parameters.ConstantBufferBindings.Concat(parameters.StructuredBuffers)) expected[("buffer", b.Index)] = b.Name;
            foreach (var (key, value) in expected)
                if (key.Kind == "sampler") { if (!declared.TryGetValue(key, out var d) || !d.StartsWith("sampler", StringComparison.Ordinal)) error ??= $"no sampler({key.Slot}) for {value}"; }
                else if (!declared.TryGetValue(key, out var d) || d != value) error ??= $"{key.Kind}({key.Slot}) is {(d ?? "undeclared")} in the MSL, {value} in the parameters";
            foreach (var (key, value) in declared)
                if (!expected.ContainsKey(key)) error ??= $"MSL {key.Kind}({key.Slot}) {value} has no parameter";
            // Vertex inputs: the attribute table against the MSL's [[ attribute(n) ]] semantics.
            var attributes = new Dictionary<int, int>();
            foreach (Match m in attribute.Matches(program.Source))
                attributes[int.Parse(m.Groups[3].Value)] = Data.ChannelOf(m.Groups[1].Value, m.Groups[2].Value);
            var table = program.Inputs.ToDictionary(i => i.Attribute, i => i.Channel);
            foreach (var (n, channel) in attributes)
                if (!table.TryGetValue(n, out var c) || c != channel) error ??= $"MSL attribute({n}) is channel {channel}, the attribute table has {(table.ContainsKey(n) ? c : "none")}";
            foreach (var (n, channel) in table)
            {
                if (!attributes.ContainsKey(n)) error ??= $"attribute table input {channel} -> {n} is not declared in the MSL";
                if ((program.ChannelMask & (1u << channel)) == 0) error ??= $"attribute table channel {channel} is not in the channel mask {program.ChannelMask:x}";
            }
            if (!program.IsVertex && (program.Inputs.Length != 0 || program.ChannelMask != 0)) error ??= "a fragment program with vertex inputs";
            if (error == null) continue;
            failures++;
            Console.WriteLine($"{Data.NameOf(shader)} {x.Id} {x.Sub.Stage} [{Data.KeywordKey(x.Sub.Keywords)}]: {error}");
        }
    }
    Console.WriteLine($"{programs} Metal subprograms checked, {failures} inconsistent");
    return failures == 0 ? 0 : 1;
}
