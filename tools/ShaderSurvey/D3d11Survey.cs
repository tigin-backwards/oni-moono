using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Assets.Files;
using OniMoono.Assets.Shaders;
using OniMoono.Assets.TypeTrees;

namespace ShaderSurvey;

/// `d3d11 DATADIR` and `d3d11-bundles WSDIR`: reads every D3D11 (platform 4) program entry of a
/// data folder's shaders, or of the mod bundles under WSDIR/&lt;id&gt;/… (read only), and checks
/// each against what Unity records for it:
/// - D3d11Program.Read (entry layout, DXBC checksum, chunks, signatures, declarations);
/// - the entry's GPU program type and keywords equal its subprogram's;
/// - per subprogram, D3d11Bindings.Check: Unity's parameters against the DXBC declarations and
///   the prefix's slot counts;
/// - vertex inputs: the channel mask is the channels of the input signature's non-system-value
///   elements, and the (channel, component) pairs list those an input always reads; programs of
///   other stages have neither;
/// - per pass with Vulkan programs: each stage's keyword sets are distinct, and for every Vulkan
///   variant's keywords K, D3d11Variants.Select picks in every stage the program whose keywords
///   are exactly K ∩ (the keywords that stage's programs use).
/// Mod shaders serialized by Unity 2020.3 (no shader-wide keyword names; their entries of
/// another magic, D3d11Program.Legacy; their subprograms' parameters in the parsed form) are
/// read the same way, but for the Vulkan comparison; 2018's (another program table) are counted,
/// not read. A bundle, serialized file or
/// shader that cannot be read is a failure. Its source is also compiled into
/// OniMoono.Assets.Tests, whose D3d11DataTests run it on the frozen data.
static class D3d11Survey
{
    public sealed class Totals
    {
        public int Shaders, Subprograms, Entries, VulkanKeys, Passes, LegacyShaders, Failures, Textures, TexturesWithoutSampler, InlineSamplers, ConstantBuffers, SharedSamplerSlots;
        public long DxbcBytes;
        public readonly SortedDictionary<string, int> ChunkSets = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Models = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Components = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> PrefixRest = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> CbSecondInt = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> SharedSamplers = new(StringComparer.Ordinal);
        /// 2020.3 Direct3D 11 subprograms by hardware tier.
        public readonly SortedDictionary<string, int> LegacyTiers = new(StringComparer.Ordinal);
    }

    public static int RunGame(string dataDir, TextWriter o) => Report(Game(dataDir, o), o);

    public static int RunBundles(string wsDir, TextWriter o)
    {
        var t = Bundles(wsDir, o);
        o.WriteLine($"{t.LegacyShaders} shaders with D3D11 programs serialized by Unity 2018 (another program table), not read");
        return Report(t, o);
    }

    public static Totals Game(string dataDir, TextWriter o)
    {
        var t = new Totals();
        foreach (var (file, shader) in Data.Shaders(dataDir)) Shader($"{file}", shader, t, o);
        return t;
    }

    public static Totals Bundles(string wsDir, TextWriter o)
    {
        var t = new Totals();
        foreach (var modDir in Directory.EnumerateDirectories(wsDir).Order(StringComparer.Ordinal))
        {
            string mod = Path.GetFileName(modDir);
            foreach (var path in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (!IsUnityFs(path)) continue;
                string bundle = Path.GetRelativePath(modDir, path);
                UnityFsArchive archive;
                try { archive = UnityFsArchive.Open(path); }
                catch (Exception e)
                {
                    Fail(t, o, $"{mod}/{bundle}: bundle not read: {e.GetType().Name}: {e.Message}");
                    continue;
                }
                foreach (var node in archive.Nodes)
                {
                    if (node.Path.EndsWith(".resS", StringComparison.Ordinal) || node.Path.EndsWith(".resource", StringComparison.Ordinal)) continue;
                    SerializedFile file;
                    try { file = SerializedFile.Parse(node.Path, archive.Read(node)); }
                    catch (Exception e)
                    {
                        Fail(t, o, $"{mod}/{bundle} {node.Path}: serialized file not read: {e.GetType().Name}: {e.Message}");
                        continue;
                    }
                    foreach (var info in file.Objects)
                    {
                        var type = file.TypeOf(info);
                        if (type.ClassId != 48) continue;
                        Fields shader;
                        try { shader = type.Tree != null ? TypeTreeReader.ReadObject(type.Tree, file.OpenObject(info), TypeTreeDatabase.TrailingBytes(48)) : TypeTreeReader.ReadNative(48, file.OpenObject(info)); }
                        catch (Exception e)
                        {
                            Fail(t, o, $"{mod}/{bundle}: shader not read: {e.GetType().Name}: {e.Message}");
                            continue;
                        }
                        if (!shader.Contains("platforms") || Array.IndexOf(shader.Get<uint[]>("platforms"), ShaderCompilerPlatform.D3D11) < 0) continue;
                        if (!shader.Contains("m_ParsedForm") || !shader.Struct("m_ParsedForm").Contains("m_KeywordNames") && !Is2020(shader))
                        {
                            t.LegacyShaders++;
                            continue;
                        }
                        if (Is2020(shader)) Legacy2020($"{mod}/{bundle}", shader, t, o);
                        Shader($"{mod}/{bundle}", shader, t, o);
                    }
                }
            }
        }
        return t;
    }

    static int Report(Totals t, TextWriter o)
    {
        o.WriteLine("chunk sets: " + string.Join(", ", t.ChunkSets.Select(kv => $"{kv.Key} {kv.Value}")));
        o.WriteLine("program types: " + string.Join(", ", t.Models.Select(kv => $"{kv.Key} {kv.Value}")));
        o.WriteLine("vertex input (channel->component) pairs: " + string.Join(", ", t.Components.Select(kv => $"{kv.Key} {kv.Value}")));
        o.WriteLine("prefix bytes 0 and 4-37: " + string.Join(", ", t.PrefixRest.Select(kv => $"{kv.Key} {kv.Value}")));
        o.WriteLine("constant buffer bindings' second int: " + string.Join(", ", t.CbSecondInt.Select(kv => $"{kv.Key} {kv.Value}")));
        o.WriteLine($"bindings checked: {t.Textures} textures ({t.TexturesWithoutSampler} without a sampler), {t.InlineSamplers} inline samplers, {t.ConstantBuffers} constant buffers");
        o.WriteLine($"sampler slots two or more textures of one subprogram name: {t.SharedSamplerSlots}" + string.Concat(t.SharedSamplers.Select(kv => $"\n  {kv.Key} ({kv.Value} subprograms)")));
        if (t.LegacyTiers.Count > 0) o.WriteLine("2020.3 Direct3D 11 subprograms by hardware tier: " + string.Join(", ", t.LegacyTiers.Select(kv => $"{kv.Key} {kv.Value}")));
        o.WriteLine($"{t.Shaders} shaders, {t.Passes} passes, {t.Subprograms} subprograms, {t.Entries} distinct entries ({t.DxbcBytes} DXBC bytes), " +
            $"{t.VulkanKeys} Vulkan variant keys mapped, {t.Failures} failures");
        return t.Failures == 0 ? 0 : 1;
    }

    static void Shader(string where, Fields shader, Totals t, TextWriter o)
    {
        string name = Data.NameOf(shader);
        ShaderProgramTable? table, vulkanTable;
        try
        {
            table = ShaderProgramTable.Load(shader, ShaderCompilerPlatform.D3D11);
            vulkanTable = ShaderProgramTable.Load(shader, ShaderCompilerPlatform.Vulkan);
        }
        catch (Exception e)
        {
            Fail(t, o, $"{where} {name}: platform tables: {e.Message}");
            return;
        }
        if (table == null) return;
        t.Shaders++;
        var programs = new Dictionary<int, D3d11Program?>();
        var subprograms = Data.Subprograms(shader).ToList();
        foreach (var x in subprograms.Where(x => x.Sub.Platform == ShaderCompilerPlatform.D3D11))
        {
            t.Subprograms++;
            string id = $"{where} {name} {x.Id} {x.Sub.Stage} [{Data.KeywordKey(x.Sub.Keywords)}]";
            if (!programs.TryGetValue(x.Sub.BlobIndex, out var program))
            {
                program = null;
                try
                {
                    if (!table.Contains(x.Sub.BlobIndex)) throw new InvalidDataException($"blob {x.Sub.BlobIndex} is not in the table of {table.Count}");
                    program = D3d11Program.Read(table.Get(x.Sub.BlobIndex));
                    t.Entries++;
                    t.DxbcBytes += program.Dxbc.Bytes.Length;
                    Tally(t.ChunkSets, string.Join("+", program.Dxbc.Chunks.Select(c => c.Name)));
                    Tally(t.Models, $"{program.GpuProgramType}:{"pvghdc"[program.Dxbc.Shader.ProgramType]}s_{program.Dxbc.Shader.Major}_{program.Dxbc.Shader.Minor}");
                    Tally(t.PrefixRest, $"[0]={program.Prefix[0]}" + string.Concat(program.Prefix.Skip(4).Select((b, i) => (b, i)).Where(p => p.b != 0).Select(p => $" [{p.i + 4}]={p.b}")));
                }
                catch (InvalidDataException e)
                {
                    Fail(t, o, $"{id}: {e.Message}");
                }
                programs[x.Sub.BlobIndex] = program;
            }
            if (program == null) continue;
            var errors = new List<string>();
            if (program.GpuProgramType != x.Sub.GpuProgramType) errors.Add($"entry type {program.GpuProgramType}, subprogram type {x.Sub.GpuProgramType}");
            if (Data.KeywordKey(program.Keywords) != Data.KeywordKey(x.Sub.Keywords)) errors.Add($"entry keywords [{Data.KeywordKey(program.Keywords)}]");
            if (x.Sub.ParameterBlobIndex < 0 && x.Sub.LegacyParameters == null) errors.Add("no parameter blob");
            else
            {
                try
                {
                    var parameters = Data.Parameters(x, new Data.Platform(table));
                    var bindings = D3d11Bindings.From(parameters);
                    errors.AddRange(bindings.Check(program));
                    foreach (var b in bindings.ConstantBuffers) Tally(t.CbSecondInt, $"{x.Sub.Stage}:{b.Unknown}");
                    t.Textures += bindings.Textures.Length;
                    t.TexturesWithoutSampler += bindings.Textures.Count(b => b.Sampler < 0);
                    t.InlineSamplers += bindings.Samplers.Length;
                    // Sampler slots that more than one texture of the subprogram names.
                    foreach (var g in bindings.Textures.Where(b => b.Sampler >= 0).GroupBy(b => b.Sampler).Where(g => g.Count() > 1))
                    {
                        t.SharedSamplerSlots++;
                        Tally(t.SharedSamplers, $"{name}: s{g.Key} for {string.Join(", ", g.Select(b => b.Name))}");
                    }
                    t.ConstantBuffers += bindings.ConstantBuffers.Length;
                }
                catch (InvalidDataException e)
                {
                    errors.Add(e.Message);
                }
            }
            errors.AddRange(InputErrors(program, x.Sub.Stage, t));
            foreach (var e in errors) Fail(t, o, $"{id}: {e}");
        }
        foreach (var pass in subprograms.Select(x => x.Pass).Distinct())
            Pass(where, name, shader, pass, vulkanTable != null, t, o);
    }

    static IEnumerable<string> InputErrors(D3d11Program program, string stage, Totals t)
    {
        var errors = new List<string>();
        if (stage != "progVertex")
        {
            if (program.ChannelMask != 0 || program.Inputs.Length != 0) errors.Add($"a {stage} program with vertex inputs (mask {program.ChannelMask:x})");
            return errors;
        }
        uint mask = 0;
        var read = new SortedSet<int>();
        foreach (var e in program.Dxbc.Inputs)
        {
            if (e.SystemValue != 0) continue;
            int channel = ChannelOf(e.SemanticName, e.SemanticIndex);
            if (channel < 0 || channel > 31) { errors.Add($"input {e.SemanticName}{e.SemanticIndex} has no vertex channel"); continue; }
            mask |= 1u << channel;
            if (e.ReadWriteMask != 0) read.Add(channel);
        }
        if (mask != program.ChannelMask) errors.Add($"channel mask {program.ChannelMask:x}, input signature gives {mask:x}");
        var pairs = program.Inputs.Select(i => i.Channel).Order().ToList();
        if (!pairs.SequenceEqual(read)) errors.Add($"pairs for channels {string.Join(",", pairs)}, input signature reads {string.Join(",", read)}");
        foreach (var (c, k) in program.Inputs) Tally(t.Components, $"{c}->{k}");
        return errors;
    }

    /// A 2020.3 shader's Direct3D 11 subprograms: their hardware tiers tallied (D3d11Variants
    /// keeps the highest per keyword set), and their own m_Parameters, which the device refuses
    /// when not empty, checked empty.
    static void Legacy2020(string where, Fields shader, Totals t, TextWriter o)
    {
        foreach (var sub in shader.Struct("m_ParsedForm").Array("m_SubShaders").Cast<Fields>())
            foreach (var pass in sub.Array("m_Passes").Cast<Fields>())
                foreach (var stage in PlayerSubprograms.Stages)
                    if (pass.Contains(stage) && pass.Struct(stage).Contains("m_SubPrograms"))
                        foreach (var sp in pass.Struct(stage).Array("m_SubPrograms").Cast<Fields>())
                        {
                            if (!GpuProgramTypes.IsD3D11(Convert.ToInt32(sp["m_GpuProgramType"]))) continue;
                            Tally(t.LegacyTiers, $"tier {sp["m_ShaderHardwareTier"]}");
                            foreach (var item in sp.Struct("m_Parameters").Items)
                                if (item.Value is object[] { Length: > 0 })
                                    Fail(t, o, $"{where} {Data.NameOf(shader)} {stage}: a 2020.3 subprogram with its own {item.Key}");
                        }
    }

    /// A shader serialized by Unity 2020.3: no shader-wide keyword names, and its subprograms list
    /// global and local keyword indices and their own parameters (2018's list one kind of index).
    static bool Is2020(Fields shader)
    {
        foreach (var sub in shader.Struct("m_ParsedForm").Array("m_SubShaders").Cast<Fields>())
            foreach (var pass in sub.Array("m_Passes").Cast<Fields>())
                foreach (var stage in PlayerSubprograms.Stages)
                    if (pass.Contains(stage) && pass.Struct(stage).Contains("m_SubPrograms"))
                        foreach (var sp in pass.Struct(stage).Array("m_SubPrograms").Cast<Fields>())
                            return sp.Contains("m_GlobalKeywordIndices") && sp.Contains("m_Parameters");
        return false;
    }

    static void Pass(string where, string name, Fields shader, Fields pass, bool hasVulkan, Totals t, TextWriter o)
    {
        bool legacy = !shader.Struct("m_ParsedForm").Contains("m_KeywordNames");
        var keywordNames = legacy ? [] : shader.Struct("m_ParsedForm").Array("m_KeywordNames").Cast<string>().ToArray();
        var variants = legacy ? D3d11Variants.OfLegacy(pass) : D3d11Variants.Of(pass, keywordNames);
        if (variants.IsEmpty) return;
        t.Passes++;
        for (int s = 0; s < variants.Stages.Length; s++)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in variants.Stages[s])
                if (!keys.Add(Data.KeywordKey(x.Keywords))) Fail(t, o, $"{where} {name} {PlayerSubprograms.Stages[s]}: two programs keyed [{Data.KeywordKey(x.Keywords)}]");
        }
        if (!hasVulkan || legacy) return;
        var vulkan = PlayerSubprograms.Of(pass, "progVertex", keywordNames).Where(x => x.Platform == ShaderCompilerPlatform.Vulkan).ToList();
        if (vulkan.Count > 0 && (variants.Vertex.Length == 0 || variants.Fragment.Length == 0))
            Fail(t, o, $"{where} {name}: a pass with Vulkan programs lacks D3D11 vertex or fragment programs");
        var known = variants.Stages.Select(list => list.SelectMany(x => x.Keywords).ToHashSet(StringComparer.Ordinal)).ToArray();
        foreach (var v in vulkan)
        {
            t.VulkanKeys++;
            var k = v.Keywords.ToHashSet(StringComparer.Ordinal);
            var selected = variants.Select(k.Contains);
            for (int s = 0; s < selected.Length; s++)
            {
                if (variants.Stages[s].Length == 0) continue;
                var expected = Data.KeywordKey(k.Where(known[s].Contains));
                var got = selected[s] is { } p ? Data.KeywordKey(p.Keywords) : "(none)";
                if (got != expected) Fail(t, o, $"{where} {name} Vulkan [{Data.KeywordKey(v.Keywords)}]: {PlayerSubprograms.Stages[s]} selects [{got}], expected [{expected}]");
            }
        }
    }

    /// The mesh channel (Unity's VertexAttribute order: position, normal, tangent, colour,
    /// texcoord 0–7) an input semantic carries; -1 for any other semantic or index.
    static int ChannelOf(string semantic, uint index) => (semantic.ToUpperInvariant(), index) switch
    {
        ("POSITION", 0) => 0,
        ("NORMAL", 0) => 1,
        ("TANGENT", 0) => 2,
        ("COLOR", 0) => 3,
        ("TEXCOORD", <= 7) => 4 + (int)index,
        _ => -1,
    };

    static void Tally(SortedDictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;

    static void Fail(Totals t, TextWriter o, string message)
    {
        t.Failures++;
        o.WriteLine("FAILED " + message);
    }

    static bool IsUnityFs(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            return f.Read(head) == 8 && head.SequenceEqual("UnityFS\0"u8);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
