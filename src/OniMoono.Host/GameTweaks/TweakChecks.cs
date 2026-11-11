#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// A tweak's GameMemberAttribute, as read from its assembly's metadata.
    internal sealed record GameMemberDeclaration(string Tweak, GameUse Use, string Member, string[] Fingerprints, int Rewrites, bool Optional = false)
    {
        public bool Fingerprinted => (Use & (GameUse.Transpiler | GameUse.Replaces | GameUse.Mirrors)) != 0;
        public bool IsMethod => Member.Contains('(');
        /// A type taken by name (`PathFinder/Cell`): no `::` and no space.
        public bool IsType => !Member.Contains("::", StringComparison.Ordinal) && !Member.Contains(' ');

        /// `Type.Member`, as the log and the screen name it.
        public string Display
        {
            get
            {
                if (IsType) return GameLookup.Display(Member);
                var (type, name) = Split(Member);
                int tick = name.IndexOf("``", StringComparison.Ordinal);
                return GameLookup.Display(type) + "." + (tick >= 0 ? name.Substring(0, tick) : name);
            }
        }

        /// The declaring type and the member's name (with a generic method's `` `N``).
        public static (string Type, string Name) Split(string member)
        {
            int colons = member.IndexOf("::", StringComparison.Ordinal);
            if (colons < 0) return ("", member);
            int space = member.LastIndexOf(' ', colons);
            string type = member.Substring(space + 1, colons - space - 1);
            int paren = member.IndexOf('(', colons);
            string name = paren < 0 ? member.Substring(colons + 2) : member.Substring(colons + 2, paren - colons - 2);
            return (type, name);
        }
    }

    /// What a tweak's check found: why it is off (none: it can install), and each of its
    /// fingerprinted members' body as this game has it (for the review).
    internal sealed class TweakCheck
    {
        public readonly string Tweak;
        public readonly List<GameMemberDeclaration> Members = new();
        public readonly List<string> Problems = new();
        public readonly List<(GameMemberDeclaration Member, string Hash, string Listing)> Bodies = new();

        public TweakCheck(string tweak) => Tweak = tweak;

        /// The first problem, as the log line's reason.
        public string? Reason => Problems.Count == 0 ? null : Problems[0] + (Problems.Count > 1 ? $" (and {Problems.Count - 1} more)" : "");
    }

    /// The checks a tweak passes before it patches anything (GameTweaks.Install; ARCHITECTURE
    /// "How game tweaks install"), read from metadata alone: the game's files as they are on disk
    /// and the tweaks' assemblies. A tweak that fails one is off for the run: none of its code
    /// runs, the game's own does, and the Moono Tweaks screen shows its row greyed out.
    ///
    /// 1. Every member it declares (GameMemberAttribute) is in the game with that signature.
    /// 2. Every game type and member the code of its classes refers to (OniMoono.GameTweaks is
    ///    built against the game's types) resolves in the game's files, so none can fail as its
    ///    code first runs in play.
    /// 3. Every member it transpiles, replaces or mirrors has a body whose fingerprint
    ///    (Fingerprints.Of) is one it declares as reviewed.
    /// (4, the sites each transpiler rewrote, is checked as it installs: GameTweaks; 5 and 6, the
    /// declarations against the code, offline: TweakDrift.)
    internal static class TweakChecks
    {
        /// Every tweak's declarations in an assembly, by tweak name; `classes` the classes whose
        /// `Name` constant is each tweak's.
        public static Dictionary<string, List<GameMemberDeclaration>> Declarations(MetadataReader reader, out Dictionary<string, List<TypeDefinitionHandle>> classes)
        {
            classes = GameLookup.TweakClasses(reader);
            var byType = new Dictionary<TypeDefinitionHandle, string>();
            foreach (var (name, types) in classes)
                foreach (var type in types) byType[type] = name;
            var result = new Dictionary<string, List<GameMemberDeclaration>>(StringComparer.Ordinal);
            var names = new Names(reader);
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                foreach (var a in type.GetCustomAttributes())
                {
                    var attribute = reader.GetCustomAttribute(a);
                    if (attribute.Constructor.Kind != HandleKind.MethodDefinition) continue;
                    var ctor = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                    if (names.Definition(ctor.GetDeclaringType()) != "OniMoono.Tweaks.GameMemberAttribute") continue;
                    if (!byType.TryGetValue(handle, out var tweak)) throw new InvalidOperationException($"{names.Definition(handle)} declares game members but has no Name constant");
                    var value = attribute.DecodeValue(names);
                    var use = (GameUse)(int)value.FixedArguments[0].Value!;
                    var member = (string)value.FixedArguments[1].Value!;
                    var prints = ImmutableArrayOf.TryFrom(value.FixedArguments[2].Value, out var list) ? list.Strings : Array.Empty<string>();
                    int rewrites = -1;
                    bool optional = false;
                    foreach (var named in value.NamedArguments)
                    {
                        if (named.Name == nameof(GameMemberAttribute.Rewrites)) rewrites = (int)named.Value!;
                        if (named.Name == nameof(GameMemberAttribute.Optional)) optional = (bool)named.Value!;
                    }
                    if (!result.TryGetValue(tweak, out var members)) result[tweak] = members = new List<GameMemberDeclaration>();
                    members.Add(new GameMemberDeclaration(tweak, use, member, prints, rewrites, optional));
                }
            }
            return result;
        }

        /// DecodeValue's string arrays (an ImmutableArray of typed arguments), as strings.
        readonly struct ImmutableArrayOf
        {
            public readonly string[] Strings;
            ImmutableArrayOf(string[] strings) => Strings = strings;
            public static bool TryFrom(object? value, out ImmutableArrayOf result)
            {
                result = default;
                if (value is not System.Collections.Immutable.ImmutableArray<CustomAttributeTypedArgument<string>> items) return false;
                result = new ImmutableArrayOf(items.Select(i => (string)i.Value!).ToArray());
                return true;
            }
        }

        /// A tweak assembly to check: its metadata, its declarations and whether its code is
        /// built against the game's types (the reference scan, 2).
        public sealed class Assembly
        {
            public readonly MetadataFile File;
            public readonly bool Typed;
            public readonly Dictionary<string, List<GameMemberDeclaration>> Declared;
            public readonly Dictionary<string, List<TypeDefinitionHandle>> Classes;
            GameLookup.ReferenceScan? scan;
            TweakDrift.Scan? drift;
            public GameLookup.ReferenceScan Scan(GameFiles game) => scan ??= new GameLookup.ReferenceScan(game, File);
            public TweakDrift.Scan Drift(GameFiles game) => drift ??= new TweakDrift.Scan(game, File);

            public Assembly(MetadataFile file, bool typed)
            {
                File = file;
                Typed = typed;
                Declared = Declarations(file.Reader, out Classes);
            }
        }

        /// The checks of each tweak in `tweaks`. `bodies`: keep each fingerprinted member's
        /// listing (the review); else only the problems.
        public static Dictionary<string, TweakCheck> Run(GameFiles game, IReadOnlyList<Assembly> assemblies, IEnumerable<string> tweaks, bool bodies = false)
        {
            var result = new Dictionary<string, TweakCheck>(StringComparer.Ordinal);
            // Test runs (ONI_TWEAK_TEST_UNREVIEWED=NAME,…): those tweaks' reviewed fingerprints are
            // ignored, as on a game build whose bodies nobody reviewed yet (the screen's spec).
            var unreviewed = (Environment.GetEnvironmentVariable("ONI_TWEAK_TEST_UNREVIEWED") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            // ONI_TWEAK_TEST_MISSING=NAME,…: those tweaks' declared members are taken as gone from
            // the game, as a game update that removed them (the menu's spec).
            var missing = (Environment.GetEnvironmentVariable("ONI_TWEAK_TEST_MISSING") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var tweak in tweaks)
            {
                var check = new TweakCheck(tweak);
                result[tweak] = check;
                foreach (var assembly in assemblies)
                    if (assembly.Declared.TryGetValue(tweak, out var members))
                        check.Members.AddRange(Array.IndexOf(unreviewed, tweak) < 0 ? members : members.Select(m => m with { Fingerprints = [] }));
                foreach (var member in check.Members) Declared(game, member, check, bodies, gone: Array.IndexOf(missing, tweak) >= 0);
                foreach (var assembly in assemblies)
                    if (assembly.Typed && assembly.Classes.TryGetValue(tweak, out var classes))
                        foreach (var problem in assembly.Scan(game).Problems(classes, OtherTweaks(assembly, tweak)))
                            if (!check.Problems.Contains(problem)) check.Problems.Add(problem);
            }
            return result;
        }

        /// Where the menu's reference scan stops: the tweaks' own classes. The menu reaches
        /// them only to read which methods a tweak guards (ModOverlaps.Guards, each in its own
        /// try), so a tweak that does not match the game turns off that tweak, not the menu. A
        /// tweak's scan follows everything it uses.
        static HashSet<TypeDefinitionHandle>? OtherTweaks(Assembly assembly, string tweak)
        {
            if (tweak != GameTweakNames.Menu) return null;
            var stop = new HashSet<TypeDefinitionHandle>();
            foreach (var (name, classes) in assembly.Classes)
                if (name != tweak) stop.UnionWith(classes);
            return stop;
        }

        /// 1 and 3 for one declared member. `gone`: taken as missing from the game (a test run's).
        static void Declared(GameFiles game, GameMemberDeclaration member, TweakCheck check, bool bodies, bool gone = false)
        {
            if (member.IsType && member.Member.Length > 0)
            {
                if (member.Optional) return;
                if (gone || game.Find(member.Member) == null) check.Problems.Add($"`{member.Display}` is missing in this game version");
                return;
            }
            var (typeName, name) = GameMemberDeclaration.Split(member.Member);
            if (typeName.Length == 0)
            {
                check.Problems.Add($"`{member.Member}` is not a member's signature");
                return;
            }
            if (gone && !member.Optional)
            {
                check.Problems.Add($"`{member.Display}` is missing in this game version");
                return;
            }
            if (game.Find(typeName) is not { } owner)
            {
                if (member.Optional) return;
                check.Problems.Add($"`{GameLookup.Display(typeName)}` is missing in this game version");
                return;
            }
            var reader = owner.File.Reader;
            var type = reader.GetTypeDefinition(owner.Type);
            int tick = name.IndexOf("``", StringComparison.Ordinal);
            string bare = tick >= 0 ? name.Substring(0, tick) : name;
            bool named = false;
            MethodDefinitionHandle found = default;
            if (member.IsMethod)
            {
                foreach (var handle in type.GetMethods())
                {
                    if (!reader.StringComparer.Equals(reader.GetMethodDefinition(handle).Name, bare)) continue;
                    named = true;
                    if (owner.File.Names.Method(handle) == member.Member) { found = handle; break; }
                }
            }
            else
            {
                foreach (var handle in type.GetFields())
                {
                    if (!reader.StringComparer.Equals(reader.GetFieldDefinition(handle).Name, bare)) continue;
                    named = true;
                    if (owner.File.Names.Field(handle) == member.Member) return;
                }
                if (member.Optional && !named) return;
            }
            if (found.IsNil)
            {
                if (member.Optional && !named) return;
                check.Problems.Add($"`{member.Display}` {(named ? "changed" : "is missing")} in this game version");
                return;
            }
            if ((member.Use & GameUse.Transpiler) != 0 && member.Rewrites < 0)
                check.Problems.Add($"`{member.Display}`: its transpiler declares no rewrite count");
            if (!member.Fingerprinted) return;
            var (hash, listing) = Fingerprints.Of(owner.File, found);
            if (bodies) check.Bodies.Add((member, hash, listing));
            bool accepted = false;
            foreach (var print in member.Fingerprints)
                if (print.Length >= hash.Length && string.CompareOrdinal(print, 0, hash, 0, hash.Length) == 0 && (print.Length == hash.Length || print[hash.Length] == ' ')) accepted = true;
            if (!accepted) check.Problems.Add($"`{member.Display}` changed in this game version");
        }

        /// A loaded method's signature as GameMemberAttribute spells it, for matching the
        /// transpilers' counts (TranspilerCounts) to their declarations.
        public static string Signature(MethodBase method)
        {
            var declaring = method.DeclaringType!;
            // The definition's signature: a method of a generic type in terms of its parameters.
            if (declaring.IsConstructedGenericType)
            {
                declaring = declaring.GetGenericTypeDefinition();
                method = MethodBase.GetMethodFromHandle(method.MethodHandle, declaring.TypeHandle) ?? method;
            }
            if (method is MethodInfo { IsGenericMethod: true } generic && !generic.IsGenericMethodDefinition) method = generic.GetGenericMethodDefinition();
            string ret = method is MethodInfo info ? TypeName(info.ReturnType, info.ReturnParameter) : "System.Void";
            var parameters = method.GetParameters().Select(p => TypeName(p.ParameterType, p));
            int arity = method.IsGenericMethod ? method.GetGenericArguments().Length : 0;
            return $"{ret} {TypeName(declaring, null)}::{method.Name}{(arity > 0 ? "``" + arity : "")}({string.Join(",", parameters)})";
        }

        static string TypeName(Type type, ParameterInfo? parameter)
        {
            string name = TypeName(type);
            if (parameter != null)
                foreach (var modifier in parameter.GetRequiredCustomModifiers()) name += " modreq(" + TypeName(modifier) + ")";
            return name;
        }

        static string TypeName(Type type)
        {
            if (type.IsByRef) return TypeName(type.GetElementType()!) + "&";
            if (type.IsPointer) return TypeName(type.GetElementType()!) + "*";
            if (type.IsArray) return TypeName(type.GetElementType()!) + (type.IsSZArray ? "[]" : "[" + new string(',', type.GetArrayRank() - 1) + "]");
            if (type.IsGenericParameter) return (type.DeclaringMethod != null ? "!!" : "!") + type.GenericParameterPosition;
            if (type.IsConstructedGenericType)
                return TypeName(type.GetGenericTypeDefinition()) + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
            if (type.IsNested) return TypeName(type.DeclaringType!) + "/" + type.Name;
            return string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;
        }
    }
}
