using System;
using System.Collections.Generic;
using OniMoono.Assets.TypeTrees;

namespace OniMoono.Assets.Shaders
{
    /// A pass stage's player subprogram: its GPU program type, its program entry and parameter blob
    /// (indices into its platform's table; -1 for no parameter blob) and the keywords it was
    /// compiled for (names, in the order the subprogram lists them). A subprogram of a shader
    /// serialized by Unity 2020.3 has no parameter blob; LegacyParameters is its parsed form's
    /// `m_Parameters` (null for U60's), and its program entry carries its parameters
    /// (D3d11Program.LegacyTail).
    public readonly record struct PlayerSubprogram(string Stage, int GpuProgramType, int BlobIndex, int ParameterBlobIndex, string[] Keywords, Fields? LegacyParameters = null)
    {
        public uint Platform => GpuProgramTypes.PlatformOf(GpuProgramType);
    }

    /// Reads the player subprograms of a pass (Unity 2021.2 and later: shader-wide keyword names).
    public static class PlayerSubprograms
    {
        /// SerializedPass stage fields, in pipeline order.
        public static readonly string[] Stages = ["progVertex", "progFragment", "progGeometry", "progHull", "progDomain", "progRayTracing"];

        /// Every player subprogram of a pass stage. `m_PlayerSubPrograms` holds one list per
        /// hardware tier (`m_ParameterBlobIndices` alike); all are returned, tier by tier.
        public static IEnumerable<PlayerSubprogram> Of(Fields pass, string stage, string[] keywordNames)
        {
            if (!pass.Contains(stage)) yield break;
            var program = pass.Struct(stage);
            if (!program.Contains("m_PlayerSubPrograms")) yield break;
            var lists = program.Array("m_PlayerSubPrograms");
            var parameterLists = program.Contains("m_ParameterBlobIndices") ? program.Array("m_ParameterBlobIndices") : [];
            for (int l = 0; l < lists.Length; l++)
            {
                var list = (object?[])lists[l]!;
                var parameterBlobs = l < parameterLists.Length ? (uint[])parameterLists[l]! : [];
                for (int k = 0; k < list.Length; k++)
                {
                    var sub = (Fields)list[k]!;
                    var indices = sub.Get<ushort[]>("m_KeywordIndices") ?? [];
                    yield return new PlayerSubprogram(stage, sub.Get<sbyte>("m_GpuProgramType"), (int)sub.Get<uint>("m_BlobIndex"),
                        k < parameterBlobs.Length ? (int)parameterBlobs[k] : -1, Array.ConvertAll(indices, i => keywordNames[i]));
                }
            }
        }

        /// The Direct3D 11 subprograms of a pass stage of a shader serialized by Unity 2020.3
        /// (`m_SubPrograms`; keywords as global then local indices into the pass's name table,
        /// the subprogram's own `m_Parameters`; no parameter blobs). Subprograms of other
        /// platforms, and Direct3D 11 ones without `m_Parameters` (Unity 2018's layout, whose
        /// program table is not read), are left out. Only hardware tier 3's are kept: Unity's
        /// manual (2020.3, "Graphics tiers") puts "Desktop: OpenGL, Metal, Vulkan, DirectX 11+" in
        /// Tier 3 and says Unity "only loads the variants for the active tier"; every 2020.3
        /// Direct3D 11 subprogram of the Workshop mods read is tier 3 (d3d11-plan.md M7,
        /// docs/research/d3d11/m7-mod-survey.txt). Damaged or unexpected fields throw
        /// InvalidDataException.
        public static IEnumerable<PlayerSubprogram> Legacy(Fields pass, string stage) => Guarded(() => LegacyOf(pass, stage));

        /// The parsed form's casts and lookups throw other exceptions on damaged fields
        /// (InvalidCastException, KeyNotFoundException, FormatException, OverflowException):
        /// InvalidDataException for all, as the reader promises.
        static T Guarded<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception e) when (e is InvalidCastException or KeyNotFoundException or FormatException or OverflowException or NullReferenceException)
            {
                throw new System.IO.InvalidDataException($"A 2020.3 pass with damaged fields: {e.Message}", e);
            }
        }

        /// Desktop Direct3D 11's hardware tier ("Graphics tiers": Tier 3).
        const int DesktopTier = 3;

        static List<PlayerSubprogram> LegacyOf(Fields pass, string stage)
        {
            if (!pass.Contains(stage)) return [];
            var program = pass.Struct(stage);
            if (!program.Contains("m_SubPrograms")) return [];
            var names = Names(pass);
            var found = new List<(PlayerSubprogram Sub, int Tier)>();
            foreach (var item in program.Array("m_SubPrograms"))
            {
                if (item is not Fields sub) throw new System.IO.InvalidDataException("A 2020.3 subprogram that is not a structure.");
                int Number(string field) => sub.Contains(field) && sub[field] is IConvertible c
                    ? Convert.ToInt32(c) : throw new System.IO.InvalidDataException($"A 2020.3 subprogram without {field}.");
                int type = Number("m_GpuProgramType");
                if (GpuProgramTypes.PlatformOf(type) != ShaderCompilerPlatform.D3D11) continue;
                if (!sub.Contains("m_Parameters") || !sub.Contains("m_GlobalKeywordIndices") || !sub.Contains("m_LocalKeywordIndices")) continue;
                long blob = sub.Contains("m_BlobIndex") && sub["m_BlobIndex"] is IConvertible b ? Convert.ToInt64(b) : throw new System.IO.InvalidDataException("A 2020.3 subprogram without m_BlobIndex.");
                if (blob is < 0 or > int.MaxValue) throw new System.IO.InvalidDataException($"A 2020.3 subprogram's blob index {blob}.");
                var keywords = new List<string>();
                foreach (var list in new[] { "m_GlobalKeywordIndices", "m_LocalKeywordIndices" })
                    foreach (var k in sub[list] switch { ushort[] a => a, null => [], _ => throw new System.IO.InvalidDataException($"A 2020.3 subprogram's {list} is not a list of indices.") })
                        keywords.Add(names.TryGetValue(k, out var name) ? name : throw new System.IO.InvalidDataException($"Shader pass keyword index {k} has no name."));
                if (sub["m_Parameters"] is not Fields parameters) throw new System.IO.InvalidDataException("A 2020.3 subprogram's m_Parameters is not a structure.");
                int tier = sub.Contains("m_ShaderHardwareTier") ? Number("m_ShaderHardwareTier") : 0;
                if (tier != DesktopTier) continue;
                found.Add((new PlayerSubprogram(stage, type, (int)blob, -1, keywords.ToArray(), parameters), tier));
            }
            return found.ConvertAll(f => f.Sub);
        }

        /// Whether a 2020.3 subprogram's own `m_Parameters` holds nothing: every field null or an
        /// empty list (SerializedProgramParameters' fields are all lists). The player refuses one
        /// that holds anything, and ShaderSurvey counts it as a failure, by this one rule.
        public static bool EmptyParameters(Fields own)
        {
            foreach (var item in own.Items)
                if (item.Value is not (object[] { Length: 0 } or null)) return false;
            return true;
        }

        /// Whether a pass stage has Direct3D 11 subprograms of Unity 2018's layout (one keyword
        /// index list, no m_Parameters), which are not read.
        public static bool Has2018D3d11(Fields pass, string stage) => Guarded(() => Has2018Of(pass, stage));

        static bool Has2018Of(Fields pass, string stage)
        {
            if (!pass.Contains(stage) || !pass.Struct(stage).Contains("m_SubPrograms")) return false;
            foreach (var item in pass.Struct(stage).Array("m_SubPrograms"))
                if (item is Fields sub && sub.Contains("m_GpuProgramType") && sub["m_GpuProgramType"] is IConvertible t
                    && GpuProgramTypes.PlatformOf(Convert.ToInt32(t)) == ShaderCompilerPlatform.D3D11 && !sub.Contains("m_Parameters"))
                    return true;
            return false;
        }

        /// The pass's name table (`m_NameIndices`: name → index), as index → name.
        public static Dictionary<int, string> Names(Fields pass) => Guarded(() => NamesOf(pass));

        static Dictionary<int, string> NamesOf(Fields pass)
        {
            var names = new Dictionary<int, string>();
            foreach (var item in pass.Array("m_NameIndices"))
            {
                var pair = (Fields)item!;
                names[Convert.ToInt32(pair["second"])] = pair.String("first");
            }
            return names;
        }

        /// The pass stage's common parameters, or empty ones.
        public static ShaderParameters Common(Fields pass, string stage, IReadOnlyDictionary<int, string> names)
        {
            if (!pass.Contains(stage) || !pass.Struct(stage).Contains("m_CommonParameters")) return new ShaderParameters();
            return ShaderParameters.ReadCommon(pass.Struct(stage).Struct("m_CommonParameters"), names);
        }
    }
}
