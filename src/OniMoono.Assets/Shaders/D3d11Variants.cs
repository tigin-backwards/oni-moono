using System;
using System.Collections.Generic;
using OniMoono.Assets.TypeTrees;

namespace OniMoono.Assets.Shaders
{
    /// A pass's D3D11 programs, one list per stage. D3D11 keeps one program per stage, keyed only
    /// by the keywords that stage uses, as Metal does; Vulkan and GLCore key one all-stage program
    /// by the union (measured on U60's data, docs/research/d3d11/programs.md §4). Each stage is
    /// selected from its own list by VariantSelection's rule. Measured on U60's data (ShaderSurvey
    /// `d3d11`): for every Vulkan variant's keyword set, every stage's selection is the program
    /// whose keywords are exactly that set's keywords the stage knows.
    public sealed class D3d11Variants
    {
        /// The pass's D3D11 player subprograms per stage, in PlayerSubprograms.Stages order
        /// (empty lists for stages without D3D11 programs).
        public PlayerSubprogram[][] Stages { get; private init; } = [];

        public PlayerSubprogram[] Vertex => Stages[0];
        public PlayerSubprogram[] Fragment => Stages[1];
        public PlayerSubprogram[] Geometry => Stages[2];

        public bool IsEmpty => Vertex.Length == 0 && Fragment.Length == 0;

        /// No programs for any stage (a legacy pass, or one without keyword names).
        public static readonly D3d11Variants Empty = new()
        {
            Stages = Array.ConvertAll(PlayerSubprograms.Stages, _ => Array.Empty<PlayerSubprogram>()),
        };

        public static D3d11Variants Of(Fields pass, string[] keywordNames)
        {
            var stages = new PlayerSubprogram[PlayerSubprograms.Stages.Length][];
            for (int s = 0; s < stages.Length; s++)
            {
                var list = new List<PlayerSubprogram>();
                foreach (var sub in PlayerSubprograms.Of(pass, PlayerSubprograms.Stages[s], keywordNames))
                    if (sub.Platform == ShaderCompilerPlatform.D3D11) list.Add(sub);
                stages[s] = list.ToArray();
            }
            return new D3d11Variants { Stages = stages };
        }

        /// The selected program of each stage (null for a stage without D3D11 programs), in
        /// PlayerSubprograms.Stages order.
        public PlayerSubprogram?[] Select(Func<string, bool> isEnabled)
        {
            var result = new PlayerSubprogram?[Stages.Length];
            for (int s = 0; s < Stages.Length; s++)
            {
                int i = VariantSelection.SelectIndex<PlayerSubprogram>(Stages[s], static x => x.Keywords, isEnabled);
                result[s] = i < 0 ? null : Stages[s][i];
            }
            return result;
        }
    }
}
