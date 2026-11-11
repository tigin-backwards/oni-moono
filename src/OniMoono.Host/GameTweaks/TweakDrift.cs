#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// What a name lookup asks the game for.
    internal enum LookupKind { Type, Nested, Field, Method, Property, Getter, Setter, Member, Enumerate }

    /// Where a type value in a tweak's IL came from: a reference into a game assembly (typeof), a
    /// name to resolve in the game's files (AccessTools.TypeByName), a type of ours or the
    /// runtime's, or the static type of an object (`x.GetType()`, `Traverse.Create(x)`: its
    /// type or one derived from it).
    internal enum TypeOrigin { Unknown, Game, Named, Other, Static }

    /// A call that reaches a game member by name, found in a tweak's IL: `Type` and `Name` null
    /// when the IL does not give them as constants.
    /// `NameMethod` and `NameOffset`: where the name was written, when not at the call (a
    /// constant list of names).
    internal sealed record LookupSite(TypeDefinitionHandle Owner, MethodDefinitionHandle Method, int Offset, string Api, LookupKind Kind,
        string? Type, TypeOrigin Origin, string? Name, MethodDefinitionHandle NameMethod = default, int NameOffset = -1);

    /// What `--check-tweaks` finds of a tweak's drift from its declarations (TweakDrift):
    /// failures (an undeclared name lookup, a MirrorsGame marker and Mirrors declaration without
    /// the other), warnings (lookups whose type or name the IL does not give) and notes (those
    /// reviewed, NameLookupReviewed).
    internal sealed class DriftCheck
    {
        public readonly string Tweak;
        public readonly List<string> Failures = new(), Warnings = new(), Notes = new();
        /// Lookups checked, and those of them spelt with nameof (checked by the compiler).
        public int Checked, Nameof;

        public DriftCheck(string tweak) => Tweak = tweak;
    }

    /// The declarations a tweak's code needs but the game's metadata alone cannot ask for
    /// (ARCHITECTURE "How game tweaks install", 5 and 6), checked offline (`--check-tweaks`,
    /// check.sh), not at start-up: they hold for the tweak's code whatever the build.
    ///
    /// 5. Each game member its code reaches by name (AccessTools.Field/Method/Property/
    ///    PropertyGetter/PropertySetter/Inner/TypeByName/FieldRefAccess, Traverse, Type.GetField/
    ///    GetMethod/GetProperty/GetNestedType/GetMember, Type.GetType and Assembly.GetType,
    ///    HarmonyPatch attributes) is declared: a name is no member reference, so the reference
    ///    scan (2) misses it and a rename would fail only as the code runs. The IL is followed
    ///    from each `ldstr` and `typeof` through the stack, locals and fields (a field holding one
    ///    value wherever it is stored, closures' captures included) to the lookup's arguments.
    ///    One with a game type and a constant name must resolve in the game and be declared (any
    ///    declaration of that member, on the type that has it; a type by any declaration that
    ///    names it); one whose type or name the IL does not give is a warning, unless its method
    ///    or class is marked NameLookupReviewed.
    /// 6. Each MirrorsGame marker on its code has a Mirrors declaration of that member, and each
    ///    Mirrors declaration a marker.
    /// The code is each tweak's classes and every class they use (the reference scan's walk),
    /// stopping at other tweaks' classes: their lookups are theirs to declare.
    internal static class TweakDrift
    {
        public static Dictionary<string, DriftCheck> Run(GameFiles game, IReadOnlyList<TweakChecks.Assembly> assemblies, IReadOnlyDictionary<string, TweakCheck> checks, string label)
        {
            var result = new Dictionary<string, DriftCheck>(StringComparer.Ordinal);
            foreach (var (tweak, check) in checks)
            {
                var drift = result[tweak] = new DriftCheck(tweak);
                var mirrored = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var assembly in assemblies)
                {
                    // A loaded assembly's metadata has no bodies to follow (TweakCheckTool opens the file).
                    if (!assembly.Classes.TryGetValue(tweak, out var classes) || assembly.File.Path == null) continue;
                    var scan = assembly.Drift(game);
                    var stop = new HashSet<TypeDefinitionHandle>();
                    foreach (var (name, others) in assembly.Classes)
                        if (name != tweak) stop.UnionWith(others);
                    stop.ExceptWith(classes);
                    foreach (var type in assembly.Scan(game).Reach(classes, stop))
                    {
                        foreach (var site in scan.Sites(type)) Lookup(game, scan, site, check.Members, drift, label);
                        foreach (var (member, where) in scan.Markers(type)) mirrored.TryAdd(member, where);
                    }
                }
                foreach (var (member, where) in mirrored)
                {
                    if (check.Members.Any(m => (m.Use & GameUse.Mirrors) != 0 && m.Member == member)) continue;
                    drift.Failures.Add($"{where} is marked [MirrorsGame(\"{member}\")], which the tweak does not declare; add {MirrorsLine(game, member, label)}");
                }
                foreach (var member in check.Members)
                    if ((member.Use & GameUse.Mirrors) != 0 && !mirrored.ContainsKey(member.Member))
                        drift.Failures.Add($"declares `{member.Display}` GameUse.Mirrors, but no code of the tweak is marked [MirrorsGame(\"{member.Member}\")]");
            }
            return result;
        }

        static string MirrorsLine(GameFiles game, string member, string label)
        {
            var (typeName, _) = GameMemberDeclaration.Split(member);
            string print = "";
            if (game.Find(typeName) is { } owner)
                foreach (var handle in owner.File.Reader.GetTypeDefinition(owner.Type).GetMethods())
                    if (owner.File.Names.Method(handle) == member) print = $", \"{Fingerprints.Of(owner.File, handle).Hash} {label}\"";
            return $"[GameMember(GameUse.Mirrors, \"{member}\"{print})]";
        }

        /// One lookup against the game and the tweak's declarations.
        static void Lookup(GameFiles game, Scan scan, LookupSite site, IReadOnlyList<GameMemberDeclaration> declared, DriftCheck drift, string label)
        {
            if (site.Origin == TypeOrigin.Other) return;
            string where = scan.Where(site);
            string? reviewed = scan.Reviewed(site);
            // A type of ours or the runtime's, named: not the game's.
            (MetadataFile File, TypeDefinitionHandle Type)? owner = site.Type == null ? null : game.Find(site.Type);
            // An object of a type not the game's (`object`, ours) can still be the game's.
            if (site.Origin == TypeOrigin.Static && owner == null) site = site with { Type = null, Origin = TypeOrigin.Unknown };
            if (site.Type != null && owner == null) return;
            if (site.Kind == LookupKind.Enumerate || site.Type == null || site.Name == null && site.Kind != LookupKind.Type)
            {
                string what = site.Kind == LookupKind.Enumerate ? $"enumerates the members of `{GameLookup.Display(site.Type!)}`"
                    : site.Kind == LookupKind.Type ? "takes a type by a name the IL does not give"
                    : site.Type == null ? $"looks up {(site.Name == null ? "a member" : $"`{site.Name}`")} on a type the IL does not give"
                    : $"looks up a member of `{GameLookup.Display(site.Type)}` by a name the IL does not give";
                if (reviewed != null) drift.Notes.Add($"{where}: {what} ({site.Api}); reviewed: {reviewed}");
                else drift.Warnings.Add($"{where}: {what} ({site.Api}); not checked (mark it [NameLookupReviewed(\"why\")] once reviewed)");
                return;
            }
            if (site.Kind == LookupKind.Type)
            {
                if (site.Name == null) return;
                drift.Checked++;
                if (declared.Any(d => Names(d.Member, site.Type))) return;
                drift.Failures.Add($"{where}: takes `{GameLookup.Display(site.Type)}` by name ({site.Api}) without declaring it; add [GameMember(GameUse.Uses, \"{site.Type}\")]");
                return;
            }
            drift.Checked++;
            string name = site.Name!;
            bool nameof = scan.Nameof(site);
            if (site.Kind == LookupKind.Nested)
            {
                string nested = site.Type + "/" + name;
                if (declared.Any(d => Names(d.Member, nested))) return;
                bool there = game.Find(nested) != null;
                drift.Failures.Add($"{where}: takes `{GameLookup.Display(nested)}` by name ({site.Api}) without declaring it; " +
                    (there ? $"add [GameMember(GameUse.Uses, \"{nested}\")]" : "nor is it in this game version"));
                return;
            }
            var found = Find(game, owner!.Value, site.Kind, name);
            string declaring = found.Count > 0 ? found[0].Declaring : site.Type;
            // nameof: the compiler checked it against its build; it must be in this one too.
            if (nameof && found.Count > 0) { drift.Nameof++; return; }
            if (declared.Any(d => Matches(d, declaring, site.Kind, name))) return;
            if (found.Count == 0 && site.Origin == TypeOrigin.Static)
            {
                // The object's type may be one derived from it that has the member.
                if (reviewed != null) drift.Notes.Add($"{where}: looks up `{name}` on an object of `{GameLookup.Display(site.Type)}`, which has none ({site.Api}); reviewed: {reviewed}");
                else drift.Warnings.Add($"{where}: looks up `{name}` on an object of `{GameLookup.Display(site.Type)}`, which has none: a type derived from it? ({site.Api}); not checked");
                return;
            }
            if (found.Count == 0)
            {
                // Declared by name on the type it was looked up on (an Optional member), or missing.
                if (declared.Any(d => Matches(d, site.Type, site.Kind, name))) return;
                drift.Failures.Add($"{where}: looks up `{GameLookup.Display(site.Type)}.{name}` by name ({site.Api}), which is not in this game version and not declared");
                return;
            }
            var lines = found.Select(f => $"[GameMember(GameUse.Uses, \"{f.Signature}\")]").Distinct().ToList();
            drift.Failures.Add($"{where}: looks up `{GameLookup.Display(declaring)}.{name}` by name ({site.Api}) without declaring it; add " +
                (lines.Count == 1 ? lines[0] : $"the one it means of {string.Join(" ", lines)}"));
        }

        /// Whether a declaration names `type`: as the type form, its declaring type, or a type in
        /// its signature (a member of `type` or of a type nested in it).
        static bool Names(string member, string type)
        {
            for (int at = member.IndexOf(type, StringComparison.Ordinal); at >= 0; at = member.IndexOf(type, at + 1, StringComparison.Ordinal))
            {
                char before = at == 0 ? ' ' : member[at - 1];
                char after = at + type.Length >= member.Length ? ' ' : member[at + type.Length];
                if ((before is ' ' or '(' or ',' or '<') && (after is ' ' or ':' or '/' or ',' or ')' or '&' or '[' or '<' or '>' or '*')) return true;
            }
            return false;
        }

        static bool Matches(GameMemberDeclaration d, string type, LookupKind kind, string name)
        {
            if (d.IsType) return false;
            var (t, n) = GameMemberDeclaration.Split(d.Member);
            if (t != type) return false;
            int tick = n.IndexOf("``", StringComparison.Ordinal);
            if (tick >= 0) n = n.Substring(0, tick);
            return kind switch
            {
                LookupKind.Field => !d.IsMethod && n == name,
                LookupKind.Method => d.IsMethod && n == name,
                LookupKind.Getter => d.IsMethod && n == "get_" + name,
                LookupKind.Setter => d.IsMethod && n == "set_" + name,
                LookupKind.Property => d.IsMethod && (n == "get_" + name || n == "set_" + name),
                _ => n == name || n == "get_" + name || n == "set_" + name || n == "add_" + name || n == "remove_" + name,
            };
        }

        /// The game's members a lookup finds, in the type or its game base types (the first that
        /// has any): each with its declaring type and GameMember signature.
        static List<(string Declaring, string Signature)> Find(GameFiles game, (MetadataFile File, TypeDefinitionHandle Type) owner, LookupKind kind, string name)
        {
            var result = new List<(string, string)>();
            var (file, type) = owner;
            for (int depth = 0; depth < 32 && !type.IsNil; depth++)
            {
                var reader = file.Reader;
                var definition = reader.GetTypeDefinition(type);
                string declaring = file.Names.Definition(type);
                bool fields = kind is LookupKind.Field or LookupKind.Member, methods = kind is LookupKind.Method or LookupKind.Member;
                if (fields)
                    foreach (var f in definition.GetFields())
                        if (reader.StringComparer.Equals(reader.GetFieldDefinition(f).Name, name)) result.Add((declaring, file.Names.Field(f)));
                foreach (var m in definition.GetMethods())
                {
                    var method = reader.GetMethodDefinition(m);
                    bool match = methods && reader.StringComparer.Equals(method.Name, name)
                        || kind is LookupKind.Getter or LookupKind.Property or LookupKind.Member && reader.StringComparer.Equals(method.Name, "get_" + name)
                        || kind is LookupKind.Setter or LookupKind.Property or LookupKind.Member && reader.StringComparer.Equals(method.Name, "set_" + name)
                        || kind is LookupKind.Member && (reader.StringComparer.Equals(method.Name, "add_" + name) || reader.StringComparer.Equals(method.Name, "remove_" + name));
                    if (match) result.Add((declaring, file.Names.Method(m)));
                }
                // A property with only a setter, asked for as a property: its setter.
                if (kind == LookupKind.Property && result.Count > 1) result.RemoveAll(r => r.Item2.Contains("::set_", StringComparison.Ordinal));
                if (result.Count > 0) return result;
                var baseType = definition.BaseType;
                if (baseType.IsNil) break;
                if (baseType.Kind == HandleKind.TypeDefinition) { type = (TypeDefinitionHandle)baseType; continue; }
                if (baseType.Kind == HandleKind.TypeSpecification) baseType = GameLookup.GenericHead(reader, (TypeSpecificationHandle)baseType);
                if (baseType.Kind != HandleKind.TypeReference || GameLookup.ResolveReference(game, file, (TypeReferenceHandle)baseType) is not { } next) break;
                (file, type) = next;
            }
            return result;
        }

        /// A value the scan follows through a method's IL: a string, a type (its name and where it
        /// came from; no name: a type the IL does not give), a Traverse of one, or anything else
        /// with its static type (`Text`, when known).
        readonly record struct Value(int Kind, string? Text = null, TypeOrigin Origin = TypeOrigin.Unknown)
        {
            /// Array: a new array (`Text` its id in Scan.arrays); Strings: one of its strings (an
            /// element of it, as a `foreach` over a constant list of names reads them).
            public const int None = 0, String = 1, Type = 2, Traverse = 3, Array = 4, Strings = 5;
            public static readonly Value Unknown = default;
            public static Value Of(string? type) => new(None, type);

            /// An object's type as a lookup's: its static type when that is the game's.
            public Value TypeOfObject(int kind) => Kind == None && Text is { } t && t != "System.Object" ? new Value(kind, t, TypeOrigin.Static) : new Value(kind);
        }

        /// One tweak assembly's lookup sites and MirrorsGame markers, by type, with the values its
        /// fields hold.
        internal sealed class Scan
        {
            readonly GameFiles game;
            readonly MetadataFile file;
            readonly Dictionary<TypeDefinitionHandle, List<LookupSite>> sites = new();
            readonly Dictionary<EntityHandle, Value> fields = new();
            readonly HashSet<EntityHandle> conflicted = new();
            /// Each new array's strings, by its id; null once anything else is stored in it.
            readonly Dictionary<string, List<string>?> arrays = new(StringComparer.Ordinal);
            /// Where each array was made: its method and offset.
            readonly Dictionary<string, (MethodDefinitionHandle Method, int Offset)> made = new(StringComparer.Ordinal);
            bool fieldsKnown;
            MetadataReader? pdb;
            bool pdbOpened;

            public Scan(GameFiles game, MetadataFile file)
            {
                this.game = game;
                this.file = file;
            }

            /// The lookups in a type's own methods (not its nested types').
            public List<LookupSite> Sites(TypeDefinitionHandle type)
            {
                if (!fieldsKnown)
                {
                    fieldsKnown = true;
                    // The values each field is stored, twice: a field stored from another is known the second time.
                    for (int pass = 0; pass < 2; pass++)
                        foreach (var t in file.Reader.TypeDefinitions)
                            foreach (var m in file.Reader.GetTypeDefinition(t).GetMethods())
                                Interpret(t, m, null);
                }
                if (sites.TryGetValue(type, out var list)) return list;
                sites[type] = list = new List<LookupSite>();
                var definition = file.Reader.GetTypeDefinition(type);
                foreach (var m in definition.GetMethods()) Interpret(type, m, list);
                foreach (var a in definition.GetCustomAttributes()) Patch(type, default, a, null, list);
                foreach (var m in definition.GetMethods())
                    foreach (var a in file.Reader.GetMethodDefinition(m).GetCustomAttributes())
                        Patch(type, m, a, ClassPatch(definition), list);
                return list;
            }

            /// MirrorsGame markers on a type, its methods and its properties, with where each is.
            public IEnumerable<(string Member, string Where)> Markers(TypeDefinitionHandle type)
            {
                var reader = file.Reader;
                var definition = reader.GetTypeDefinition(type);
                string typeName = GameLookup.Display(file.Names.Definition(type));
                foreach (var member in Strings(definition.GetCustomAttributes(), "OniMoono.Tweaks.MirrorsGameAttribute")) yield return (member, typeName);
                foreach (var m in definition.GetMethods())
                    foreach (var member in Strings(reader.GetMethodDefinition(m).GetCustomAttributes(), "OniMoono.Tweaks.MirrorsGameAttribute"))
                        yield return (member, Where(type, m, -1));
                foreach (var p in definition.GetProperties())
                {
                    var property = reader.GetPropertyDefinition(p);
                    foreach (var member in Strings(property.GetCustomAttributes(), "OniMoono.Tweaks.MirrorsGameAttribute"))
                        yield return (member, typeName + "." + reader.GetString(property.Name));
                }
            }

            /// The first string argument of each attribute of that type.
            IEnumerable<string> Strings(CustomAttributeHandleCollection attributes, string attributeType)
            {
                foreach (var a in attributes)
                {
                    var attribute = file.Reader.GetCustomAttribute(a);
                    if (AttributeType(attribute) != attributeType) continue;
                    var value = attribute.DecodeValue(file.Names);
                    if (value.FixedArguments.Length > 0 && value.FixedArguments[0].Value is string s) yield return s;
                }
            }

            string AttributeType(CustomAttribute attribute) => attribute.Constructor.Kind switch
            {
                HandleKind.MethodDefinition => file.Names.Definition(file.Reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
                HandleKind.MemberReference => file.Names.Parent(file.Reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
                _ => "",
            };

            /// NameLookupReviewed's reason for a site: on its method, the method a lambda, local
            /// function or iterator of it was compiled from, or a class around them.
            public string? Reviewed(LookupSite site)
            {
                var reader = file.Reader;
                const string attribute = "OniMoono.Tweaks.NameLookupReviewedAttribute";
                if (!site.Method.IsNil)
                {
                    if (Strings(reader.GetMethodDefinition(site.Method).GetCustomAttributes(), attribute).FirstOrDefault() is { } own) return own;
                    string? source = SourceName(reader.GetString(reader.GetMethodDefinition(site.Method).Name));
                    for (var t = site.Owner; !t.IsNil; t = reader.GetTypeDefinition(t).GetDeclaringType())
                    {
                        var type = reader.GetTypeDefinition(t);
                        source ??= SourceName(reader.GetString(type.Name));
                        if (source != null)
                            foreach (var m in type.GetMethods())
                                if (reader.StringComparer.Equals(reader.GetMethodDefinition(m).Name, source)
                                    && Strings(reader.GetMethodDefinition(m).GetCustomAttributes(), attribute).FirstOrDefault() is { } outer) return outer;
                    }
                }
                for (var t = site.Owner; !t.IsNil; t = reader.GetTypeDefinition(t).GetDeclaringType())
                    if (Strings(reader.GetTypeDefinition(t).GetCustomAttributes(), attribute).FirstOrDefault() is { } reason) return reason;
                return null;
            }

            /// `X` of a compiler-generated `<X>b__0_1`, `<X>g__Local|0_1`, `<X>d__4`; null otherwise.
            static string? SourceName(string name)
            {
                if (name.Length < 3 || name[0] != '<') return null;
                int end = name.IndexOf('>');
                return end > 1 ? name.Substring(1, end - 1) : null;
            }

            /// `Type.Method (File.cs:LINE)`, a lambda's as its method's.
            public string Where(LookupSite site) => Where(site.Owner, site.Method, site.Offset);

            string Where(TypeDefinitionHandle owner, MethodDefinitionHandle method, int offset)
            {
                var reader = file.Reader;
                // The type and method as written: compiler-generated types and methods named by theirs.
                var t = owner;
                while (!t.IsNil && reader.GetString(reader.GetTypeDefinition(t).Name).StartsWith('<') && !reader.GetTypeDefinition(t).GetDeclaringType().IsNil)
                    t = reader.GetTypeDefinition(t).GetDeclaringType();
                string type = GameLookup.Display(file.Names.Definition(t));
                string text = type;
                if (!method.IsNil)
                {
                    string name = reader.GetString(reader.GetMethodDefinition(method).Name);
                    string? generated = SourceName(name) ?? SourceName(reader.GetString(reader.GetTypeDefinition(owner).Name));
                    text = type + "." + (generated ?? name);
                    if (Line(method, offset) is { } line) text += $" ({line})";
                }
                return text;
            }

            /// `src/…/File.cs:LINE` of an IL offset (the last sequence point at or before it;
            /// -1, the method's first), from the assembly's portable PDB beside it.
            string? Line(MethodDefinitionHandle method, int offset)
            {
                if (Point(method, offset) is not { } p) return null;
                string document = p.Document.Replace('\\', '/');
                int src = document.LastIndexOf("/src/", StringComparison.Ordinal);
                if (src < 0) src = document.LastIndexOf("/tests/", StringComparison.Ordinal);
                return (src >= 0 ? document.Substring(src + 1) : System.IO.Path.GetFileName(document)) + ":" + p.Point.StartLine;
            }

            readonly Dictionary<string, string[]?> sources = new(StringComparer.Ordinal);

            /// Whether a site's statement spells its constant name `nameof(…Name)`, not as a
            /// literal: the compiler checked that member against the game it was built with. Read
            /// from the source the PDB names; false without it.
            public bool Nameof(LookupSite site)
            {
                var (method, offset) = site.NameMethod.IsNil ? (site.Method, site.Offset) : (site.NameMethod, site.NameOffset);
                if (site.Name == null || method.IsNil || offset < 0 || Point(method, offset) is not { } p) return false;
                if (!sources.TryGetValue(p.Document, out var lines))
                {
                    try { lines = File.Exists(p.Document) ? File.ReadAllLines(p.Document) : null; }
                    catch (IOException) { lines = null; }
                    catch (UnauthorizedAccessException) { lines = null; }
                    sources[p.Document] = lines;
                }
                var point = p.Point;
                if (lines == null || point.StartLine < 1 || point.EndLine > lines.Length || point.EndLine < point.StartLine) return false;
                var text = new System.Text.StringBuilder();
                for (int line = point.StartLine; line <= point.EndLine; line++)
                {
                    string l = lines[line - 1];
                    int from = line == point.StartLine ? Math.Min(l.Length, point.StartColumn - 1) : 0;
                    int to = line == point.EndLine ? Math.Min(l.Length, point.EndColumn - 1) : l.Length;
                    if (to > from) text.Append(l, from, to - from).Append('\n');
                }
                string statement = text.ToString();
                return !statement.Contains("\"" + site.Name + "\"", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.IsMatch(statement, @"\bnameof\s*\([^()]*?\b" + System.Text.RegularExpressions.Regex.Escape(site.Name) + @"\s*\)");
            }

            (SequencePoint Point, string Document)? Point(MethodDefinitionHandle method, int offset)
            {
                if (!pdbOpened)
                {
                    pdbOpened = true;
                    try
                    {
                        string? path = file.Path == null ? null : System.IO.Path.ChangeExtension(file.Path, ".pdb");
                        if (path != null && File.Exists(path))
                            pdb = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(path)).GetMetadataReader();
                    }
                    catch (BadImageFormatException) { }
                    catch (IOException) { }
                }
                if (pdb == null) return null;
                try
                {
                    var info = pdb.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(method)));
                    SequencePoint? best = null;
                    foreach (var point in info.GetSequencePoints())
                    {
                        if (point.IsHidden) continue;
                        if (best == null || offset >= 0 && point.Offset <= offset) best = point;
                        if (offset < 0) break;
                    }
                    if (best is not { } p) return null;
                    return (p, pdb.GetString(pdb.GetDocument(p.Document).Name));
                }
                catch (BadImageFormatException) { return null; }
            }

            // ---- HarmonyPatch attributes ----

            /// A class's [HarmonyPatch] type and method name, for its methods' attributes.
            (string? Type, string? Name, int MethodType) ClassPatch(TypeDefinition type)
            {
                (string?, string?, int) result = (null, null, 0);
                foreach (var a in type.GetCustomAttributes())
                    if (PatchArguments(file.Reader.GetCustomAttribute(a)) is { } p) result = (p.Type ?? result.Item1, p.Name ?? result.Item2, p.MethodType != 0 ? p.MethodType : result.Item3);
                return result;
            }

            (string? Type, string? Name, int MethodType)? PatchArguments(CustomAttribute attribute)
            {
                if (AttributeType(attribute) != "HarmonyLib.HarmonyPatch") return null;
                CustomAttributeValue<string> value;
                ImmutableArray<string> parameters;
                try
                {
                    value = attribute.DecodeValue(file.Names);
                    parameters = attribute.Constructor.Kind == HandleKind.MemberReference
                        ? file.Reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).DecodeMethodSignature(file.Names, null).ParameterTypes
                        : file.Reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).DecodeSignature(file.Names, null).ParameterTypes;
                }
                catch (BadImageFormatException) { return null; }
                string? type = null, name = null;
                var strings = new List<string>();
                int methodType = 0;
                for (int i = 0; i < value.FixedArguments.Length && i < parameters.Length; i++)
                {
                    var argument = value.FixedArguments[i].Value;
                    if (parameters[i] == "System.Type" && argument is string t) type ??= TypeName(t);
                    else if (parameters[i] == "System.String" && argument is string s) strings.Add(s);
                    else if (parameters[i] == "HarmonyLib.MethodType" && argument is int k) methodType = k;
                }
                // (string typeName, string methodName, …) without a Type; else the method's name.
                if (type == null && strings.Count >= 2) { type = TypeName(strings[0]); name = strings[1]; }
                else if (strings.Count > 0) name = strings[0];
                return (type, name, methodType);
            }

            /// A serialized or reflection type name as GameMemberAttribute spells it.
            static string TypeName(string name)
            {
                int comma = name.IndexOf(',');
                if (comma >= 0) name = name.Substring(0, comma);
                return name.Trim().Replace('+', '/');
            }

            void Patch(TypeDefinitionHandle owner, MethodDefinitionHandle method, CustomAttributeHandle handle, (string? Type, string? Name, int MethodType)? outer, List<LookupSite> list)
            {
                if (PatchArguments(file.Reader.GetCustomAttribute(handle)) is not { } p) return;
                // Only where the class's and the method's arguments together name a method.
                if (method.IsNil && p.Name == null) return;
                string? type = p.Type ?? outer?.Type, name = p.Name ?? outer?.Name;
                int kind = p.MethodType != 0 ? p.MethodType : outer?.MethodType ?? 0;
                if (name == null || kind > 2) return; // constructors are named by none
                list.Add(new LookupSite(owner, method, -1, "HarmonyPatch", kind == 1 ? LookupKind.Getter : kind == 2 ? LookupKind.Setter : LookupKind.Method,
                    type, type == null ? TypeOrigin.Unknown : TypeOrigin.Named, name));
            }

            // ---- The IL ----

            /// Follows a method's IL: with `list`, adds its lookups; without, records the values
            /// its stores give fields.
            void Interpret(TypeDefinitionHandle owner, MethodDefinitionHandle handle, List<LookupSite>? list)
            {
                var body = file.Body(handle);
                if (body == null) return;
                var reader = file.Reader;
                List<IlWalk.Instruction> code;
                try { code = IlWalk.Read(body.GetILReader()); }
                catch (BadImageFormatException) { return; }
                int localCount = 0;
                if (!body.LocalSignature.IsNil)
                    try { localCount = reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(file.Names, null).Length; }
                    catch (BadImageFormatException) { }
                var locals = new Value[localCount];
                ImmutableArray<string> localTypes = ImmutableArray<string>.Empty, argTypes;
                if (!body.LocalSignature.IsNil)
                    try { localTypes = reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(file.Names, null); }
                    catch (BadImageFormatException) { }
                var definition = reader.GetMethodDefinition(handle);
                try
                {
                    var signature = definition.DecodeSignature(file.Names, null);
                    argTypes = signature.Header.IsInstance ? signature.ParameterTypes.Insert(0, file.Names.Definition(owner)) : signature.ParameterTypes;
                }
                catch (BadImageFormatException) { argTypes = ImmutableArray<string>.Empty; }
                for (int k = 0; k < locals.Length && k < localTypes.Length; k++) locals[k] = Value.Of(localTypes[k]);
                var stack = new List<Value>();
                var snapshots = new Dictionary<int, Value[]>();
                var handlers = new HashSet<int>();
                foreach (var region in body.ExceptionRegions)
                {
                    if (region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter) handlers.Add(region.HandlerOffset);
                    if (region.Kind == ExceptionRegionKind.Filter) handlers.Add(region.FilterOffset);
                }
                var stored = new Dictionary<EntityHandle, Value>();
                bool reachable = true;
                Value Pop() { if (stack.Count == 0) return Value.Unknown; var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v; }
                void Snapshot(int target)
                {
                    var now = stack.ToArray();
                    if (!snapshots.TryGetValue(target, out var before)) snapshots[target] = now;
                    else if (before.Length == now.Length)
                        for (int k = 0; k < now.Length; k++)
                            if (before[k] != now[k]) before[k] = Value.Unknown;
                }
                foreach (var i in code)
                {
                    if (handlers.Contains(i.Offset)) { stack.Clear(); stack.Add(Value.Unknown); }
                    else if (!reachable) { stack.Clear(); if (snapshots.TryGetValue(i.Offset, out var s)) stack.AddRange(s); }
                    else if (snapshots.TryGetValue(i.Offset, out var s) && s.Length == stack.Count)
                        for (int k = 0; k < s.Length; k++)
                            if (s[k] != stack[k]) stack[k] = Value.Unknown;
                    reachable = true;
                    var op = i.Op;
                    if (op == OpCodes.Ldstr) stack.Add(new Value(Value.String, reader.GetUserString(MetadataTokens.UserStringHandle((int)i.Value & 0xFFFFFF))));
                    else if (op == OpCodes.Ldtoken) stack.Add(TypeToken(MetadataTokens.EntityHandle((int)i.Value)));
                    else if (op == OpCodes.Dup) { var v = Pop(); stack.Add(v); stack.Add(v); }
                    else if (Local(op, i, out int load, out bool store))
                    {
                        if (store) { var v = Pop(); if (load < locals.Length) locals[load] = v.Kind == Value.None && load < localTypes.Length ? Value.Of(localTypes[load]) : v; }
                        else stack.Add(load < locals.Length ? locals[load] : Value.Unknown);
                    }
                    else if (Argument(op, i) is int argument and >= 0) stack.Add(argument < argTypes.Length ? Value.Of(argTypes[argument]) : Value.Unknown);
                    else if (op == OpCodes.Ldloca || op == OpCodes.Ldloca_S)
                    {
                        if (i.Value < locals.Length) locals[i.Value] = Value.Of(i.Value < localTypes.Length ? localTypes[(int)i.Value] : null);
                        stack.Add(Value.Unknown);
                    }
                    else if (op == OpCodes.Stsfld || op == OpCodes.Stfld)
                    {
                        var v = Pop();
                        if (op == OpCodes.Stfld) Pop();
                        var field = MetadataTokens.EntityHandle((int)i.Value);
                        stored[field] = v;
                        if (list == null) Store(field, v);
                    }
                    else if (op == OpCodes.Ldsfld || op == OpCodes.Ldfld)
                    {
                        if (op == OpCodes.Ldfld) Pop();
                        var field = MetadataTokens.EntityHandle((int)i.Value);
                        stack.Add(stored.TryGetValue(field, out var v) && v.Kind != Value.None ? v : fields.TryGetValue(field, out v) ? v : Value.Of(FieldType(field)));
                    }
                    else if (op == OpCodes.Newarr)
                    {
                        Pop();
                        string id = MetadataTokens.GetToken(handle) + "@" + i.Offset;
                        arrays.TryAdd(id, new List<string>());
                        made[id] = (handle, i.Offset);
                        stack.Add(new Value(Value.Array, id));
                    }
                    else if (op == OpCodes.Stelem_Ref || op == OpCodes.Stelem)
                    {
                        var v = Pop();
                        Pop();
                        var array = Pop();
                        if (array.Kind == Value.Array && arrays.TryGetValue(array.Text!, out var strings) && strings != null)
                        {
                            if (v.Kind == Value.String) { if (!strings.Contains(v.Text!)) strings.Add(v.Text!); }
                            else arrays[array.Text!] = null;
                        }
                    }
                    else if (op == OpCodes.Ldelem_Ref || op == OpCodes.Ldelem)
                    {
                        Pop();
                        var array = Pop();
                        stack.Add(array.Kind == Value.Array ? new Value(Value.Strings, array.Text) : Value.Unknown);
                    }
                    else if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                        Call(owner, handle, i, MetadataTokens.EntityHandle((int)i.Value), stack, list);
                    else if (op == OpCodes.Calli)
                    {
                        try
                        {
                            var sig = reader.GetStandaloneSignature((StandaloneSignatureHandle)MetadataTokens.EntityHandle((int)i.Value)).DecodeMethodSignature(file.Names, null);
                            for (int k = 0; k < sig.ParameterTypes.Length + 1 + (sig.Header.IsInstance ? 1 : 0); k++) Pop();
                            if (sig.ReturnType != "System.Void") stack.Add(Value.Unknown);
                        }
                        catch (BadImageFormatException) { stack.Clear(); }
                    }
                    else if (op.FlowControl == FlowControl.Return || op.FlowControl == FlowControl.Throw) { stack.Clear(); reachable = false; }
                    else if (op == OpCodes.Leave || op == OpCodes.Leave_S) { stack.Clear(); Snapshot((int)i.Value); reachable = false; }
                    else
                    {
                        for (int k = Pops(op); k > 0; k--) Pop();
                        for (int k = Pushes(op); k > 0; k--) stack.Add(Value.Unknown);
                        if (op.FlowControl == FlowControl.Branch) { Snapshot((int)i.Value); reachable = false; }
                        else if (op.FlowControl == FlowControl.Cond_Branch)
                        {
                            if (i.Targets != null) foreach (var t in i.Targets) Snapshot(t);
                            else Snapshot((int)i.Value);
                        }
                    }
                }
            }

            /// A field's value wherever it is stored: one value, or unknown once it is stored two.
            void Store(EntityHandle field, Value v)
            {
                // An unknown store (null, a parameter) is taken as the known one's reset.
                if (v.Kind == Value.None || conflicted.Contains(field)) return;
                if (fields.TryGetValue(field, out var before) && before != v)
                {
                    conflicted.Add(field);
                    fields.Remove(field);
                    return;
                }
                fields[field] = v;
            }

            static bool Local(OpCode op, IlWalk.Instruction i, out int index, out bool store)
            {
                store = false;
                index = -1;
                if (op == OpCodes.Ldloc_0) index = 0;
                else if (op == OpCodes.Ldloc_1) index = 1;
                else if (op == OpCodes.Ldloc_2) index = 2;
                else if (op == OpCodes.Ldloc_3) index = 3;
                else if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S) index = (int)i.Value;
                else
                {
                    store = true;
                    if (op == OpCodes.Stloc_0) index = 0;
                    else if (op == OpCodes.Stloc_1) index = 1;
                    else if (op == OpCodes.Stloc_2) index = 2;
                    else if (op == OpCodes.Stloc_3) index = 3;
                    else if (op == OpCodes.Stloc || op == OpCodes.Stloc_S) index = (int)i.Value;
                }
                return index >= 0;
            }

            /// A load of an argument's index, or -1.
            static int Argument(OpCode op, IlWalk.Instruction i) =>
                op == OpCodes.Ldarg_0 ? 0 : op == OpCodes.Ldarg_1 ? 1 : op == OpCodes.Ldarg_2 ? 2 : op == OpCodes.Ldarg_3 ? 3
                : op == OpCodes.Ldarg || op == OpCodes.Ldarg_S ? (int)i.Value : -1;

            string? FieldType(EntityHandle field)
            {
                try
                {
                    return field.Kind switch
                    {
                        HandleKind.FieldDefinition => file.Reader.GetFieldDefinition((FieldDefinitionHandle)field).DecodeSignature(file.Names, null),
                        HandleKind.MemberReference => file.Reader.GetMemberReference((MemberReferenceHandle)field).DecodeFieldSignature(file.Names, null),
                        _ => null,
                    };
                }
                catch (BadImageFormatException) { return null; }
            }

            static int Pops(OpCode op) => op.StackBehaviourPop switch
            {
                StackBehaviour.Pop0 or StackBehaviour.Varpop => 0,
                StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
                StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4
                    or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
                _ => 3,
            };

            static int Pushes(OpCode op) => op.StackBehaviourPush switch
            {
                StackBehaviour.Push0 or StackBehaviour.Varpush => 0,
                StackBehaviour.Push1_push1 => 2,
                _ => 1,
            };

            /// A type token as a value: the game's (a reference into a game assembly), or ours.
            Value TypeToken(EntityHandle token)
            {
                var reader = file.Reader;
                switch (token.Kind)
                {
                    case HandleKind.TypeDefinition: return new Value(Value.Type, file.Names.Definition((TypeDefinitionHandle)token), TypeOrigin.Other);
                    case HandleKind.TypeReference:
                        var reference = (TypeReferenceHandle)token;
                        return new Value(Value.Type, file.Names.Reference(reference), GameLookup.IsGameReference(game, file, reference) ? TypeOrigin.Game : TypeOrigin.Other);
                    case HandleKind.TypeSpecification:
                        var head = GameLookup.GenericHead(reader, (TypeSpecificationHandle)token);
                        if (head.Kind == HandleKind.TypeReference && GameLookup.IsGameReference(game, file, (TypeReferenceHandle)head))
                            return new Value(Value.Type, file.Names.Reference((TypeReferenceHandle)head), TypeOrigin.Game);
                        return new Value(Value.Type, file.Names.Type(token), TypeOrigin.Other);
                    default: return Value.Unknown;
                }
            }

            /// A call's effect on the stack, and the lookup it makes.
            void Call(TypeDefinitionHandle owner, MethodDefinitionHandle method, IlWalk.Instruction i, EntityHandle token, List<Value> stack, List<LookupSite>? list)
            {
                var reader = file.Reader;
                string declaring, name;
                MethodSignature<string> sig;
                ImmutableArray<string> generic = ImmutableArray<string>.Empty;
                try
                {
                    var callee = token;
                    if (callee.Kind == HandleKind.MethodSpecification)
                    {
                        var spec = reader.GetMethodSpecification((MethodSpecificationHandle)callee);
                        generic = spec.DecodeSignature(file.Names, null);
                        callee = spec.Method;
                    }
                    if (callee.Kind == HandleKind.MethodDefinition)
                    {
                        var definition = reader.GetMethodDefinition((MethodDefinitionHandle)callee);
                        declaring = file.Names.Definition(definition.GetDeclaringType());
                        name = reader.GetString(definition.Name);
                        sig = definition.DecodeSignature(file.Names, null);
                    }
                    else if (callee.Kind == HandleKind.MemberReference)
                    {
                        var member = reader.GetMemberReference((MemberReferenceHandle)callee);
                        declaring = file.Names.Parent(member.Parent);
                        name = reader.GetString(member.Name);
                        sig = member.DecodeMethodSignature(file.Names, null);
                    }
                    else { stack.Clear(); return; }
                }
                catch (BadImageFormatException) { stack.Clear(); return; }
                bool construct = i.Op == OpCodes.Newobj;
                int count = sig.ParameterTypes.Length + (sig.Header.IsInstance && !construct ? 1 : 0);
                var args = new Value[count];
                for (int k = count - 1; k >= 0; k--) args[k] = stack.Count == 0 ? Value.Unknown : Pop(stack);
                var result = Evaluate(owner, method, i.Offset, declaring, name, sig, generic, args, construct, list);
                if (construct || sig.ReturnType != "System.Void") stack.Add(result == Value.Unknown ? Value.Of(construct ? declaring : sig.ReturnType) : result);
            }

            /// The strings of a constant list a value is one of (Value.Strings), or null.
            List<string>? OneOf(Value value) =>
                value.Kind == Value.Strings && arrays.TryGetValue(value.Text!, out var strings) && strings is { Count: > 0 } ? strings : null;

            static Value Pop(List<Value> stack)
            {
                var v = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                return v;
            }

            /// The lookup APIs: what each finds by name, and the type value it returns.
            Value Evaluate(TypeDefinitionHandle owner, MethodDefinitionHandle method, int offset, string declaring, string name, MethodSignature<string> sig,
                ImmutableArray<string> generic, Value[] args, bool construct, List<LookupSite>? list)
            {
                int self = sig.Header.IsInstance && !construct ? 1 : 0;
                var parameters = sig.ParameterTypes;
                int typeAt = parameters.IndexOf("System.Type"), stringAt = parameters.IndexOf("System.String");
                Value Arg(int index) => index < 0 ? Value.Unknown : args[index + self];
                void Site(LookupKind kind, Value type, Value text, string api)
                {
                    if (list == null) return;
                    string? typeName = type.Kind is Value.Type or Value.Traverse ? type.Text : null;
                    var origin = typeName == null ? TypeOrigin.Unknown : type.Origin;
                    // One of a constant list's names: each of them.
                    if (OneOf(text) is { } names)
                    {
                        var at = made.TryGetValue(text.Text!, out var where) ? where : (method, offset);
                        foreach (var n in names) list.Add(new LookupSite(owner, method, offset, api, kind, typeName, origin, n, at.Item1, at.Item2));
                    }
                    else list.Add(new LookupSite(owner, method, offset, api, kind, typeName, origin, text.Kind == Value.String ? text.Text : null));
                }
                Value Named(Value text) => text.Kind == Value.String && text.Text is { Length: > 0 } t ? new Value(Value.Type, TypeName(t), TypeOrigin.Named) : new Value(Value.Type);
                string api = GameLookup.Display(declaring) + "." + name;
                switch (declaring)
                {
                    case "System.String" when name == "Concat" && self == 0 && parameters.All(p => p == "System.String") && args.Length is 2 or 3:
                    {
                        // A constant prefix and one of a list ("LoadScreen:" + name): each.
                        var parts = args.Select(a => a.Kind == Value.String ? new List<string> { a.Text! } : OneOf(a)).ToList();
                        if (parts.Any(p => p == null)) break;
                        IEnumerable<string> joined = [""];
                        foreach (var part in parts) joined = joined.SelectMany(j => part!.Select(x => j + x)).ToList();
                        var values = joined.ToList();
                        if (values.Count == 1) return new Value(Value.String, values[0]);
                        string id = MetadataTokens.GetToken(method) + "@" + offset + "+";
                        arrays[id] = values;
                        return new Value(Value.Strings, id);
                    }
                    case "System.Type" when name == "GetTypeFromHandle":
                        return args.Length > 0 && args[0].Kind == Value.Type ? args[0] : new Value(Value.Type);
                    case "System.Type" when name == "GetType" && self == 0 && stringAt == 0:
                    case "System.Reflection.Assembly" when name == "GetType" && stringAt == 0:
                    {
                        var text = Arg(stringAt);
                        Site(LookupKind.Type, Named(text), text, api);
                        return Named(text);
                    }
                    case "System.Object" when name == "GetType":
                        return args[0].TypeOfObject(Value.Type);
                    case "System.Type" or "System.Reflection.TypeInfo" when self == 1:
                    {
                        var kind = name switch
                        {
                            "GetField" or "GetDeclaredField" => LookupKind.Field,
                            "GetMethod" or "GetDeclaredMethod" => LookupKind.Method,
                            "GetProperty" or "GetDeclaredProperty" => LookupKind.Property,
                            "GetNestedType" or "GetDeclaredNestedType" => LookupKind.Nested,
                            "GetMember" or "GetEvent" or "GetDeclaredEvent" => LookupKind.Member,
                            "GetFields" or "GetMethods" or "GetProperties" or "GetMembers" or "GetNestedTypes" or "GetEvents" => LookupKind.Enumerate,
                            _ => (LookupKind)(-1),
                        };
                        if ((int)kind < 0 || kind != LookupKind.Enumerate && stringAt != 0) break;
                        var receiver = args[0];
                        if (kind == LookupKind.Enumerate)
                        {
                            if (receiver.Kind == Value.Type && receiver.Text != null) Site(kind, receiver, Value.Unknown, api);
                            break;
                        }
                        Site(kind, receiver, Arg(stringAt), api);
                        if (kind == LookupKind.Nested && receiver.Kind == Value.Type && receiver.Text != null && Arg(stringAt).Kind == Value.String)
                            return new Value(Value.Type, receiver.Text + "/" + Arg(stringAt).Text, receiver.Origin);
                        return kind == LookupKind.Nested ? new Value(Value.Type) : Value.Unknown;
                    }
                    case "HarmonyLib.AccessTools":
                    {
                        if (name == "TypeByName" && stringAt == 0)
                        {
                            Site(LookupKind.Type, Named(Arg(0)), Arg(0), api);
                            return Named(Arg(0));
                        }
                        if (name == "Inner" && typeAt == 0 && stringAt == 1)
                        {
                            var type = Arg(0);
                            Site(LookupKind.Nested, type, Arg(1), api);
                            return type.Kind == Value.Type && type.Text != null && Arg(1).Kind == Value.String
                                ? new Value(Value.Type, type.Text + "/" + Arg(1).Text, type.Origin) : new Value(Value.Type);
                        }
                        var kind = name switch
                        {
                            "Method" or "DeclaredMethod" => LookupKind.Method,
                            "Field" or "DeclaredField" or "FieldRefAccess" or "StaticFieldRefAccess" => LookupKind.Field,
                            "Property" or "DeclaredProperty" => LookupKind.Property,
                            "PropertyGetter" or "DeclaredPropertyGetter" => LookupKind.Getter,
                            "PropertySetter" or "DeclaredPropertySetter" => LookupKind.Setter,
                            "GetDeclaredMethods" or "GetDeclaredFields" or "GetDeclaredProperties" or "GetMethodNames" or "GetFieldNames" or "GetPropertyNames" => LookupKind.Enumerate,
                            _ => (LookupKind)(-1),
                        };
                        if ((int)kind < 0) break;
                        if (kind == LookupKind.Enumerate)
                        {
                            if (typeAt == 0 && Arg(0).Kind == Value.Type && Arg(0).Text != null) Site(kind, Arg(0), Value.Unknown, api);
                            break;
                        }
                        if (stringAt < 0) break; // by a MethodInfo or FieldInfo: no name
                        if (typeAt >= 0) Site(kind, Arg(typeAt), Arg(stringAt), api);
                        else if (generic.Length >= 2 && kind == LookupKind.Field) // FieldRefAccess<T, F>("name")
                            Site(kind, new Value(Value.Type, generic[0], TypeOrigin.Named), Arg(stringAt), api);
                        else
                        {
                            // "Type:member": the type by its name.
                            var text = Arg(stringAt);
                            var all = text.Kind == Value.String ? new List<string> { text.Text! } : OneOf(text);
                            if (all == null) Site(kind, Value.Unknown, Value.Unknown, api);
                            else
                                foreach (var t in all)
                                {
                                    int colon = t.LastIndexOf(':');
                                    if (colon > 0) Site(kind, new Value(Value.Type, TypeName(t.Substring(0, colon)), TypeOrigin.Named), new Value(Value.String, t.Substring(colon + 1)), api);
                                    else Site(kind, Value.Unknown, Value.Unknown, api);
                                }
                        }
                        break;
                    }
                    case "HarmonyLib.HarmonyMethod" when construct && typeAt == 0 && stringAt == 1:
                        Site(LookupKind.Method, Arg(0), Arg(1), "HarmonyMethod");
                        break;
                    case "HarmonyLib.Traverse":
                    {
                        if (self == 0 && name == "Create")
                        {
                            if (generic.Length == 1) return new Value(Value.Traverse, generic[0], TypeOrigin.Named);
                            if (typeAt == 0 && Arg(0).Kind == Value.Type) return Arg(0) with { Kind = Value.Traverse };
                            return Arg(0).TypeOfObject(Value.Traverse);
                        }
                        if (self == 0 && name == "CreateWithType" && stringAt == 0)
                        {
                            Site(LookupKind.Type, Named(Arg(0)), Arg(0), api);
                            return Named(Arg(0)) with { Kind = Value.Traverse };
                        }
                        if (self == 1 && stringAt == 0)
                        {
                            var kind = name switch
                            {
                                "Field" => LookupKind.Field,
                                "Property" => LookupKind.Property,
                                "Method" => LookupKind.Method,
                                "Type" => LookupKind.Nested,
                                _ => (LookupKind)(-1),
                            };
                            if ((int)kind < 0) break;
                            var receiver = args[0].Kind == Value.Traverse ? args[0] with { Kind = Value.Type } : new Value(Value.Type);
                            Site(kind, receiver, Arg(stringAt), api);
                            return new Value(Value.Traverse);
                        }
                        break;
                    }
                }
                return Value.Unknown;
            }
        }
    }
}
