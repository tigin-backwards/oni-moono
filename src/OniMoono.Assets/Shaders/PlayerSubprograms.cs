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

        /// Every subprogram of a pass stage of a shader serialized before Unity 2021.2 (2020.3:
        /// `m_SubPrograms`, keywords as global then local indices into the pass's name table; no
        /// parameter blobs). Every 2020.3
        /// Direct3D 11 subprogram of the Workshop mods read is of hardware tier 3 alone (1,722;
        /// d3d11-plan.md M7), so tiers are not told apart, as U60's are not.
        public static IEnumerable<PlayerSubprogram> Legacy(Fields pass, string stage)
        {
            if (!pass.Contains(stage)) yield break;
            var program = pass.Struct(stage);
            if (!program.Contains("m_SubPrograms")) yield break;
            var names = Names(pass);
            foreach (var item in program.Array("m_SubPrograms"))
            {
                var sub = (Fields)item!;
                var keywords = new List<string>();
                var lists = sub.Contains("m_GlobalKeywordIndices") ? new[] { "m_GlobalKeywordIndices", "m_LocalKeywordIndices" } : new[] { "m_KeywordIndices" };
                foreach (var list in lists)
                    foreach (var k in sub.Get<ushort[]>(list) ?? [])
                        keywords.Add(names.TryGetValue(k, out var name) ? name : throw new System.IO.InvalidDataException($"Shader pass keyword index {k} has no name."));
                yield return new PlayerSubprogram(stage, Convert.ToInt32(sub["m_GpuProgramType"]), (int)Convert.ToUInt32(sub["m_BlobIndex"]), -1, keywords.ToArray(),
                    sub.Contains("m_Parameters") ? sub.Struct("m_Parameters") : null);
            }
        }

        /// The pass's name table (`m_NameIndices`: name → index), as index → name.
        public static Dictionary<int, string> Names(Fields pass)
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
