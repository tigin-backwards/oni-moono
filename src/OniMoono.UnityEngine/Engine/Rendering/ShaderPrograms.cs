using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OniMoono.Assets.Shaders;
using OniMoono.Assets.TypeTrees;

namespace OniMoono.Engine.Rendering
{
    /// One compiled program variant of a pass: GLSL stages as Unity's GL device compiles them.
    internal sealed class ShaderVariant
    {
        public int BlobIndex;
        public string[] Keywords;
        public string VertexSource;
        public string FragmentSource;
        /// Null for programs without a geometry stage.
        public string GeometrySource;
        /// Vertex channels the program reads (bit per channel: position 0x1, normal 0x2,
        /// tangent 0x4, color 0x8, texcoord n 0x10 << n).
        public uint ChannelMask;
    }

    /// The programs selected for a draw, per stage: program and parameter blob indices (-1: none).
    /// On OpenGLCore both stages name the one program.
    internal readonly record struct StagePrograms(int Vertex, int VertexParameters, int Fragment, int FragmentParameters);

    /// A shader's programs for one graphics API (ShaderCompilerPlatform: 15 OpenGLCore, 14 Metal),
    /// from its blob table (ShaderProgramTable).
    ///
    /// OpenGLCore entries are: version, GPU program type, four stats, the variant's keywords, the
    /// GLSL source of all stages (sections `#ifdef VERTEX` … `#ifdef FRAGMENT` …), then the vertex
    /// channel mask. Shaders built before Unity 2021.2 (mods' bundles; Unity 6 loads them) list
    /// global then local keywords, and keep the channel mask in the parsed form's subprograms
    /// instead. One GLCore program holds every stage, keyed by the union of their keywords.
    ///
    /// Metal entries hold one stage each, as MSL source (MetalProgram), keyed by the keywords that
    /// stage uses (docs/research/macos-feasibility.md §1.3); their parameter blobs
    /// (ShaderParameters) give the constant-buffer layouts and the texture, sampler and buffer
    /// slots. Legacy shaders' Metal programs are not read.
    internal sealed class ShaderPrograms
    {
        readonly ShaderProgramTable table;
        readonly Dictionary<int, ShaderVariant> variants = new();
        readonly Dictionary<int, MetalProgram> metalPrograms = new();
        readonly Dictionary<int, VulkanProgram> vulkanPrograms = new();
        readonly Dictionary<int, D3d11Program> d3d11Programs = new();
        readonly Dictionary<(Fields Pass, string Stage, int Blob), ShaderParameters> parameters = new();
        /// Legacy shaders: each blob's channel mask, from the parsed form (null otherwise).
        readonly Dictionary<int, uint> legacyChannels;

        /// The ShaderCompilerPlatform the programs are for.
        public readonly uint Platform;

        ShaderPrograms(ShaderProgramTable table, uint platform, Dictionary<int, uint> legacyChannels)
        {
            this.table = table;
            Platform = platform;
            this.legacyChannels = legacyChannels;
        }

        /// The OpenGLCore programs (null when the shader has none).
        public static ShaderPrograms Load(Fields shader) => Load(shader, ShaderNative.OpenGLCorePlatform);

        /// The programs of one platform (null when the shader has none for it).
        public static ShaderPrograms Load(Fields shader, uint platform)
        {
            var table = ShaderProgramTable.Load(shader, platform);
            if (table == null) return null;
            Dictionary<int, uint> legacyChannels = null;
            if (platform == ShaderNative.OpenGLCorePlatform && IsLegacy(shader))
            {
                legacyChannels = new Dictionary<int, uint>();
                foreach (Fields sub in shader.Struct("m_ParsedForm").Array("m_SubShaders"))
                    foreach (Fields pass in sub.Array("m_Passes"))
                        foreach (Fields program in pass.Struct("progVertex").Array("m_SubPrograms"))
                            if (IsGlCore(Convert.ToInt32(program["m_GpuProgramType"])))
                                legacyChannels[(int)Convert.ToUInt32(program["m_BlobIndex"])] = Convert.ToUInt32(program.Struct("m_Channels")["m_SourceMap"]);
            }
            return new ShaderPrograms(table, platform, legacyChannels);
        }

        /// A shader serialized before Unity 2021.2: no shader-wide keyword list.
        public static bool IsLegacy(Fields shader) => !shader.Struct("m_ParsedForm").Contains("m_KeywordNames");

        /// An OpenGLCore program variant, decoded on first use.
        public ShaderVariant Variant(int blobIndex)
        {
            if (Platform != ShaderNative.OpenGLCorePlatform) throw new InvalidOperationException($"Shader platform {Platform} has no GLSL variants.");
            lock (variants)
            {
                if (variants.TryGetValue(blobIndex, out var v)) return v;
                v = Decode(blobIndex);
                variants[blobIndex] = v;
                return v;
            }
        }

        ShaderVariant Decode(int blobIndex)
        {
            var data = table.Get(blobIndex);
            int pos = 24;
            var keywords = new List<string>();
            // Legacy entries list global keywords, then local ones.
            for (int list = 0; list < (legacyChannels != null ? 2 : 1); list++)
            {
                int keywordCount = BitConverter.ToInt32(data[pos..]);
                pos += 4;
                for (int i = 0; i < keywordCount; i++)
                {
                    int len = BitConverter.ToInt32(data[pos..]);
                    keywords.Add(Encoding.UTF8.GetString(data.Slice(pos + 4, len)));
                    pos += 4 + ((len + 3) & ~3);
                }
            }
            int textLength = BitConverter.ToInt32(data[pos..]);
            var source = Encoding.UTF8.GetString(data.Slice(pos + 4, textLength));
            pos += 4 + ((textLength + 3) & ~3);
            uint channels = legacyChannels != null ? legacyChannels.GetValueOrDefault(blobIndex) : BitConverter.ToUInt32(data[pos..]);
            var stages = Split(source);
            return new ShaderVariant
            {
                BlobIndex = blobIndex,
                Keywords = keywords.ToArray(),
                VertexSource = stages["VERTEX"],
                FragmentSource = stages["FRAGMENT"],
                GeometrySource = stages.GetValueOrDefault("GEOMETRY"),
                ChannelMask = channels,
            };
        }

        /// A Vulkan program (every stage), decoded on first use.
        public VulkanProgram Vulkan(int blobIndex)
        {
            if (Platform != ShaderNative.VulkanPlatform) throw new InvalidOperationException($"Shader platform {Platform} has no Vulkan programs.");
            lock (vulkanPrograms)
            {
                if (vulkanPrograms.TryGetValue(blobIndex, out var v)) return v;
                v = VulkanProgram.Read(table.Get(blobIndex));
                vulkanPrograms[blobIndex] = v;
                return v;
            }
        }

        /// A Direct3D 11 stage program (its DXBC), decoded on first use.
        public D3d11Program D3d11(int blobIndex)
        {
            if (Platform != ShaderNative.D3D11Platform) throw new InvalidOperationException($"Shader platform {Platform} has no D3D11 programs.");
            lock (d3d11Programs)
            {
                if (d3d11Programs.TryGetValue(blobIndex, out var p)) return p;
                p = D3d11Program.Read(table.Get(blobIndex));
                d3d11Programs[blobIndex] = p;
                return p;
            }
        }

        /// A Metal stage program, decoded on first use.
        public MetalProgram Metal(int blobIndex)
        {
            if (Platform != ShaderNative.MetalPlatform) throw new InvalidOperationException($"Shader platform {Platform} has no Metal programs.");
            lock (metalPrograms)
            {
                if (metalPrograms.TryGetValue(blobIndex, out var m)) return m;
                m = MetalProgram.Read(table.Get(blobIndex));
                metalPrograms[blobIndex] = m;
                return m;
            }
        }

        /// What a stage variant of a pass reads: the stage's common parameters merged with the
        /// variant's parameter blob (the common ones alone without a blob). Read on first use.
        public ShaderParameters Parameters(Fields pass, string stage, int parameterBlobIndex)
        {
            lock (parameters)
            {
                if (parameters.TryGetValue((pass, stage, parameterBlobIndex), out var p)) return p;
                var common = PlayerSubprograms.Common(pass, stage, PlayerSubprograms.Names(pass));
                p = parameterBlobIndex < 0 ? common : ShaderParameters.Merge(common, ShaderParameters.Read(table.Get(parameterBlobIndex)));
                parameters[(pass, stage, parameterBlobIndex)] = p;
                return p;
            }
        }

        static readonly string[] stageNames = ["VERTEX", "FRAGMENT", "GEOMETRY", "HULL", "DOMAIN"];

        /// The stage sections of Unity's combined GLSL: the text inside each `#ifdef STAGE`
        /// section, which ends at the last `#endif` before the next section (or the end).
        public static Dictionary<string, string> Split(string source)
        {
            var starts = new List<(int Index, string Stage)>();
            foreach (var stage in stageNames)
            {
                int i = source.IndexOf("#ifdef " + stage + "\n", StringComparison.Ordinal);
                if (i >= 0) starts.Add((i, stage));
            }
            starts.Sort((a, b) => a.Index.CompareTo(b.Index));
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int k = 0; k < starts.Count; k++)
            {
                int start = starts[k].Index + ("#ifdef " + starts[k].Stage + "\n").Length;
                int end = k + 1 < starts.Count ? starts[k + 1].Index : source.Length;
                int close = source.LastIndexOf("#endif", end - 1, end - start, StringComparison.Ordinal);
                if (close < start) throw new InvalidDataException("Unterminated GLSL stage section.");
                result[starts[k].Stage] = source[start..close];
            }
            if (!result.ContainsKey("VERTEX") || !result.ContainsKey("FRAGMENT")) throw new InvalidDataException("GLSL program without VERTEX and FRAGMENT sections.");
            return result;
        }

        /// GPU program types of the OpenGLCore platform (GLCore32, GLCore41, GLCore43). Player
        /// program lists hold every platform's programs, told apart by type.
        public static bool IsGlCore(int gpuProgramType) => gpuProgramType is 6 or 7 or 8;

        /// A pass's OpenGLCore player programs, with the shader's keyword names.
        public static ProgramVariant[] Variants(Fields pass, string[] shaderKeywords)
        {
            var result = new List<ProgramVariant>();
            foreach (var list in pass.Struct("progVertex").Array("m_PlayerSubPrograms"))
                foreach (Fields sub in (object[])list)
                {
                    if (!IsGlCore(sub.Get<sbyte>("m_GpuProgramType"))) continue;
                    var indices = sub.Get<ushort[]>("m_KeywordIndices") ?? Array.Empty<ushort>();
                    result.Add(new ProgramVariant((int)sub.Get<uint>("m_BlobIndex"), Array.ConvertAll(indices, k => shaderKeywords[k])));
                }
            return result.ToArray();
        }

        /// A pass stage's player programs for a platform whose programs hold one stage each
        /// (Metal: `progVertex`, `progFragment`), with their parameter blobs.
        public static ProgramVariant[] StageVariants(Fields pass, string stage, string[] shaderKeywords, uint platform)
        {
            var result = new List<ProgramVariant>();
            foreach (var sub in PlayerSubprograms.Of(pass, stage, shaderKeywords))
                if (sub.Platform == platform) result.Add(new ProgramVariant(sub.BlobIndex, sub.Keywords, sub.ParameterBlobIndex));
            return result.ToArray();
        }

        /// A legacy pass's OpenGLCore programs: their global and local keyword indices name
        /// keywords through the pass's name table (before Unity 2019.1, which brought local
        /// keywords, one list of indices does).
        public static ProgramVariant[] LegacyVariants(Fields pass)
        {
            var names = new Dictionary<int, string>();
            foreach (Fields pair in pass.Array("m_NameIndices")) names[Convert.ToInt32(pair["second"])] = pair.String("first");
            var result = new List<ProgramVariant>();
            foreach (Fields sub in pass.Struct("progVertex").Array("m_SubPrograms"))
            {
                if (!IsGlCore(Convert.ToInt32(sub["m_GpuProgramType"]))) continue;
                var keywords = new List<string>();
                var lists = sub.Contains("m_GlobalKeywordIndices") ? new[] { "m_GlobalKeywordIndices", "m_LocalKeywordIndices" } : new[] { "m_KeywordIndices" };
                foreach (var list in lists)
                    foreach (var k in sub.Get<ushort[]>(list) ?? Array.Empty<ushort>())
                        keywords.Add(names.TryGetValue(k, out var name) ? name : throw new InvalidDataException($"Shader pass keyword index {k} has no name."));
                result.Add(new ProgramVariant((int)Convert.ToUInt32(sub["m_BlobIndex"]), keywords.ToArray()));
            }
            return result.ToArray();
        }

        /// The blob index of the variant Unity selects for a pass given the enabled keywords:
        /// among the pass's OpenGLCore programs, an exact match of the enabled keywords the pass
        /// knows; otherwise the one with most matches, then fewest extra keywords, then the
        /// earliest.
        public static int SelectVariant(ShaderPass pass, Func<string, bool> isEnabled) => SelectVariant(pass.Variants, isEnabled);

        /// The blob index of the variant among `variants` that rule selects (-1: none).
        public static int SelectVariant(ProgramVariant[] variants, Func<string, bool> isEnabled)
        {
            int best = SelectIndex(variants, isEnabled);
            return best < 0 ? -1 : variants[best].BlobIndex;
        }

        /// The position in `variants` of the variant that rule selects (-1 when there is none).
        public static int SelectIndex(ProgramVariant[] variants, Func<string, bool> isEnabled)
            => VariantSelection.SelectIndex<ProgramVariant>(variants, static v => v.Keywords, isEnabled);

        /// The programs a draw uses on a platform: on OpenGLCore one program for both stages; on
        /// Metal each stage selected from its own list by the same rule, since Metal keys each
        /// stage by the keywords it uses (a vertex-only keyword does not split the fragment
        /// programs).
        public static StagePrograms SelectStages(ShaderPass pass, uint platform, Func<string, bool> isEnabled)
        {
            if (platform != ShaderNative.MetalPlatform)
            {
                int blob = SelectVariant(pass, isEnabled);
                return new StagePrograms(blob, -1, blob, -1);
            }
            var vertex = pass.MetalVertexVariants;
            var fragment = pass.MetalFragmentVariants;
            int v = SelectIndex(vertex, isEnabled), f = SelectIndex(fragment, isEnabled);
            return new StagePrograms(v < 0 ? -1 : vertex[v].BlobIndex, v < 0 ? -1 : vertex[v].ParameterBlobIndex,
                f < 0 ? -1 : fragment[f].BlobIndex, f < 0 ? -1 : fragment[f].ParameterBlobIndex);
        }
    }
}
