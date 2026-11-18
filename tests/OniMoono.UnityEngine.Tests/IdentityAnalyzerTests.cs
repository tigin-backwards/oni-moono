using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using OniMoono.Analyzers;
using Xunit;

namespace OniMoono.Tests
{
    /// The engine's identity analyzer (src/OniMoono.Analyzers, docs/research/facade-merge.md): each
    /// line of a sample that ends in `// MOONOnnn` must get that diagnostic, and no other line any.
    public class IdentityAnalyzerTests
    {
        // Stand-ins for the engine's types: the analyzer finds them by name.
        const string Types = """
            namespace UnityEngine
            {
                public class Object
                {
                    public static bool operator ==(Object x, Object y) => ReferenceEquals(x, y);
                    public static bool operator !=(Object x, Object y) => !ReferenceEquals(x, y);
                    public static implicit operator bool(Object o) => o is not null;
                    public override bool Equals(object o) => ReferenceEquals(this, o);
                    public override int GetHashCode() => 0;
                }
                public class Component : Object { }
                public class Texture : Object { }
            }
            namespace OniMoono.Engine
            {
                class NativeObject { }
                class GameObjectNative : NativeObject { }
                class ComponentNative : NativeObject { }
                class BehaviourNative : ComponentNative { }
                class TextureNative : NativeObject { }
            }
            """;

        const string Engine = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using UnityEngine;
            using Object = UnityEngine.Object;
            namespace OniMoono.Engine
            {
                readonly record struct Keyed(Component C, int N);
                readonly record struct Plain(int A, string B);
                static class Sample
                {
                    static void M(ComponentNative a, ComponentNative b, NativeObject n, TextureNative t, BehaviourNative be, Component f, Component g,
                                  List<ComponentNative> records, Component[] facades, List<int> ints, Keyed k, Plain p)
                    {
                        bool x;
                        x = a == null; // MOONO001
                        x = a != b; // MOONO001
                        x = n == null; // MOONO001
                        x = be != null; // MOONO001
                        x = a is null;
                        x = ReferenceEquals(a, b);
                        x = t == null;
                        x = f == null; // MOONO002
                        x = f != g; // MOONO002
                        x = !f;
                        x = f is not null;
                        x = (a, 1) == (b, 1); // MOONO001
                        x = k == default; // MOONO002
                        x = p == default;
                        x = a.Equals(b); // MOONO003
                        x = f.Equals(g); // MOONO003
                        x = Equals(f, g); // MOONO003
                        x = k.Equals(default); // MOONO003
                        _ = new HashSet<ComponentNative>(); // MOONO004
                        _ = new HashSet<ComponentNative>(ReferenceEqualityComparer.Instance);
                        _ = new Dictionary<Component, int>(); // MOONO004
                        _ = new Dictionary<(Component, int), int>(); // MOONO004
                        _ = new Dictionary<Keyed, int>(); // MOONO004
                        _ = new Dictionary<Plain, int>();
                        _ = new HashSet<TextureNative>();
                        HashSet<GameObjectNative> set = []; // MOONO004
                        _ = EqualityComparer<GameObjectNative>.Default; // MOONO004
                        _ = records.Distinct(); // MOONO004
                        x = records.Contains(a); // MOONO005
                        records.Remove(a); // MOONO005
                        _ = records.IndexOf(b); // MOONO005
                        _ = Array.IndexOf(facades, f); // MOONO005
                        x = facades.Contains(f); // MOONO005
                        x = ints.Contains(1);
                        x = ByReference(records, a);
                    }

                    static bool ByReference(List<ComponentNative> l, ComponentNative c) => l.Exists(o => ReferenceEquals(o, c));

                    static bool Generic<T>(T o) where T : Object => o == null; // MOONO002
                }
            }
            namespace UnityEngine
            {
                public class Facade : Object
                {
                    // The game-facing API keeps Unity's operators.
                    public bool Public(Object o) => o == null;
                    public bool PublicEquals(Object o) => Equals(o);
                    // The engine's own members of a facade are engine code.
                    internal bool Internal(Object o) => o == null; // MOONO002
                    bool Private(Object o) => o != null; // MOONO002
                    internal bool Record(OniMoono.Engine.ComponentNative c) => c == null; // MOONO001
                    public bool PublicRecord() => Record() != null; // MOONO001
                    static OniMoono.Engine.ComponentNative Record() => null;
                }
                class Hidden : Object
                {
                    public bool InAnInternalType(Object o) => o == null; // MOONO002
                }
            }
            """;

        // Not the engine or its platform (the host's UI and tweaks are game code): only records.
        const string Host = """
            using UnityEngine;
            namespace OniMoono.Host
            {
                static class Sample
                {
                    static bool M(Component f, OniMoono.Engine.ComponentNative c) => f == null || f.Equals(f) || c == null; // MOONO001
                }
            }
            """;

        [Fact]
        public void TheEngineAssemblyGetsEveryRule() => Check("OniMoono.UnityEngine", Engine);

        [Fact]
        public void TheDesktopPlatformIsEngineCode() => Check("OniMoono.Desktop", Engine.Replace("namespace UnityEngine\n{", "namespace UnityEngine.Unchecked\n{"), facadeMembers: false);

        [Fact]
        public void OtherAssembliesGetOnlyTheRecordRules() => Check("OniMoono", Host);

        static void Check(string assembly, string source, bool facadeMembers = true)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create(assembly,
                [CSharpSyntaxTree.ParseText(Types, path: "Types.cs"), CSharpSyntaxTree.ParseText(source, path: "Sample.cs")],
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors));

            var found = compilation.WithAnalyzers([new IdentityAnalyzer()]).GetAnalyzerDiagnosticsAsync().Result
                .Where(d => d.Location.SourceTree?.FilePath == "Sample.cs")
                .Select(d => (Line: d.Location.GetLineSpan().StartLinePosition.Line + 1, d.Id))
                .OrderBy(d => d.Line).ToList();
            var lines = source.Split('\n');
            var expected = new List<(int Line, string Id)>();
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], @"// (MOONO\d{3})\s*$");
                // In the desktop's sample the facade members are no longer the engine's.
                bool facadeMember = !facadeMembers && lines.Take(i).Any(l => l.Contains("namespace UnityEngine"));
                if (m.Success && !(facadeMember && m.Groups[1].Value == "MOONO002")) expected.Add((i + 1, m.Groups[1].Value));
            }
            Assert.Equal(string.Join("\n", expected), string.Join("\n", found));
        }

        /// Every project that can see the records, and the engine and its platform for the
        /// facades, runs the analyzer: a project that dropped it would build without the checks.
        [Theory]
        [InlineData("OniMoono.UnityEngine")]
        [InlineData("OniMoono.Desktop")]
        [InlineData("OniMoono.Host")]
        [InlineData("UnityEngine.UI")]
        [InlineData("Unity.TextMeshPro")]
        [InlineData("Unity.Timeline")]
        [InlineData("Unity.InternalAPIEngineBridge.004")]
        public void TheProjectRunsTheAnalyzer(string project)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "OniMoono.slnx"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            var csproj = File.ReadAllText(Path.Combine(dir, "src", project, project + ".csproj"));
            Assert.Contains("<ProjectReference Include=\"../OniMoono.Analyzers/OniMoono.Analyzers.csproj\" OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" />", csproj);
        }
    }
}
