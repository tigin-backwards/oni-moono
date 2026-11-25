using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Assets.Files;
using OniMoono.Assets.Shaders;
using OniMoono.Assets.TypeTrees;

namespace ShaderSurvey;

/// A subprogram with where it sits: `s{subshader}p{pass}`, its pass and the shader's keywords.
sealed record Subprogram(string Id, Fields Pass, PlayerSubprogram Sub);

/// The data folder's shaders and their programs.
static class Data
{
    static readonly string[] files = ["level0", "level1", "level2", "globalgamemanagers.assets", "resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets",
        "Resources/unity default resources", "Resources/unity_builtin_extra"];

    public static IEnumerable<(string File, Fields Shader)> Shaders(string dataDir)
    {
        foreach (var name in files)
        {
            var path = Path.Combine(dataDir, name);
            if (!File.Exists(path)) continue;
            var file = SerializedFile.Open(path);
            foreach (var info in file.Objects)
            {
                var type = file.TypeOf(info);
                if (type.ClassId != 48) continue;
                var reader = file.OpenObject(info);
                yield return (name, type.Tree != null ? TypeTreeReader.ReadObject(type.Tree, reader, TypeTreeDatabase.TrailingBytes(48)) : TypeTreeReader.ReadNative(48, reader));
            }
        }
    }

    public static string NameOf(Fields shader) => shader.Struct("m_ParsedForm").String("m_Name");

    /// A platform's table, empty when the shader has none.
    public readonly record struct Platform(ShaderProgramTable? Table)
    {
        public int Count => Table?.Count ?? 0;
        public long Bytes => Table?.Bytes ?? 0;
        public bool Contains(int index) => Table != null && Table.Contains(index);
    }

    public static Platform Table(Fields shader, uint platform) => new(ShaderProgramTable.Load(shader, platform));

    /// Every player subprogram of every pass, in subshader, pass and stage order.
    public static IEnumerable<Subprogram> Subprograms(Fields shader)
    {
        var parsed = shader.Struct("m_ParsedForm");
        var keywords = parsed.Contains("m_KeywordNames") ? parsed.Array("m_KeywordNames").Cast<string>().ToArray() : [];
        int s = 0;
        foreach (var sub in parsed.Array("m_SubShaders").Cast<Fields>())
        {
            int p = 0;
            foreach (var pass in sub.Array("m_Passes").Cast<Fields>())
            {
                foreach (var stage in PlayerSubprograms.Stages)
                    foreach (var x in parsed.Contains("m_KeywordNames") ? PlayerSubprograms.Of(pass, stage, keywords) : PlayerSubprograms.Legacy(pass, stage))
                        yield return new Subprogram($"s{s}p{p}", pass, x);
                p++;
            }
            s++;
        }
    }

    /// The keywords as the survey prints them: sorted, joined by '+'.
    public static string KeywordKey(IEnumerable<string> keywords) => string.Join("+", keywords.Order(StringComparer.Ordinal));

    /// A subprogram's parameters: its stage's common ones and its blob's (or, 2020.3, its entry's).
    public static ShaderParameters Parameters(Subprogram x, Platform blobs)
    {
        var names = PlayerSubprograms.Names(x.Pass);
        var common = PlayerSubprograms.Common(x.Pass, x.Sub.Stage, names);
        // A 2020.3 subprogram's own parameters are in its program entry.
        if (x.Sub.LegacyParameters != null) return ShaderParameters.Merge(common, ShaderParameters.ReadInline(D3d11Program.Read(blobs.Table!.Get(x.Sub.BlobIndex)).LegacyTail));
        if (x.Sub.ParameterBlobIndex < 0) return common;
        return ShaderParameters.Merge(common, ShaderParameters.Read(blobs.Table!.Get(x.Sub.ParameterBlobIndex)));
    }

    /// ShaderChannel of an HLSL input semantic (POSITION, NORMAL, TANGENT, COLOR, TEXCOORDn); -1
    /// for others.
    public static int ChannelOf(string semantic, string index) => semantic switch
    {
        "POSITION" => 0,
        "NORMAL" => 1,
        "TANGENT" => 2,
        "COLOR" => 3,
        "TEXCOORD" => 4 + (index.Length == 0 ? 0 : int.Parse(index)),
        _ => -1,
    };
}
