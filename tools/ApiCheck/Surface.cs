// Surface mode: every public type and member of the shipped Unity assemblies (by name and
// signature, from metadata only) that our replacement lacks; and, with --mods, which of the
// missing ones Workshop mods reference. The default mode only checks what the game's own
// assemblies reference, but a mod may call any public Unity member.
//
// usage: ApiCheck --surface <gameManagedDir> <ourAssemblyDir> <providerPrefix,...>
//                 [--mods DIR] [--catalog mod-catalog.tsv] [--json out.json]
//
// "Public" is what another assembly can bind to: public members, and protected ones of
// unsealed types, on public (or public nested) types. Members of a type that is missing
// altogether are counted under the type, not listed one by one.
//
// --mods DIR scans the DLLs the game could load from each DIR/ID/: those at the top of the mod's
// folder and of each archived_versions/* folder (KMod.DLLLoader lists one folder, not its
// subfolders), but not copies of the shipped assemblies (a mod's bin/ folder or a stray
// UnityEngine.UIModule.dll), whose references the game never binds. Each member reference into a provider that does not resolve in
// ours is resolved in the shipped assemblies, which gives its declaring type (references bind
// through base types) and whether it is public there. --catalog adds each mod's rank and
// onimaxxing flags and name; the JSON also has, per reference, the DLLs that hold it, and
// (modReflection) the missing members whose name a mod's string literals hold while the mod
// references the member's type: a hint of reflection by name, not proof.
// Exit code: 0.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

static class Surface
{
    sealed class Missing
    {
        public SortedDictionary<string, int> Types { get; } = new(StringComparer.Ordinal); // type -> its public member count
        public SortedSet<string> Members { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Obsolete { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (int types, int members, int missingTypes, int membersOfMissing, int missingMembers)> PerAssembly { get; } = new(StringComparer.Ordinal);
    }

    public static int Run(string[] args)
    {
        var positional = new List<string>();
        string? jsonOut = null, modsDir = null, catalog = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json": jsonOut = args[++i]; break;
                case "--mods": modsDir = args[++i]; break;
                case "--catalog": catalog = args[++i]; break;
                default: positional.Add(args[i]); break;
            }
        }
        var gameDir = positional[0];
        var prefixes = positional[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool IsProvider(string asm) => prefixes.Any(p => p.EndsWith('.') ? asm.StartsWith(p, StringComparison.Ordinal) : asm == p);

        var ours = new OurAssemblies(positional[1]);
        var shipped = new OurAssemblies(gameDir);
        var missing = CompareSurface(gameDir, IsProvider, ours);

        int total = missing.PerAssembly.Values.Sum(x => x.members), types = missing.PerAssembly.Values.Sum(x => x.types);
        Console.WriteLine($"public surface: {types} types, {total} members in {missing.PerAssembly.Count} assemblies");
        Console.WriteLine($"missing: {missing.Types.Count} types (holding {missing.Types.Values.Sum()} members), {missing.Members.Count} members of present types ({missing.Obsolete.Count} of them [Obsolete])");
        Console.WriteLine($"  {"assembly",-48} {"types",6} {"members",8} {"miss.types",10} {"their mbrs",10} {"miss.mbrs",9}");
        foreach (var (asm, x) in missing.PerAssembly.OrderBy(k => k.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {asm,-48} {x.types,6} {x.members,8} {x.missingTypes,10} {x.membersOfMissing,10} {x.missingMembers,9}");

        Dictionary<string, SortedSet<string>>? modPublic = null, modOther = null, modDlls = null, modReflection = null;
        Func<SortedSet<string>, object>? modNames = null;
        if (modsDir != null)
        {
            var cat = catalog != null ? ReadCatalog(catalog) : new();
            (modPublic, modOther, modDlls, modReflection) = ScanMods(modsDir, gameDir, IsProvider, ours, shipped, missing);
            string Who(SortedSet<string> ids) => string.Join(" ", ids.Select(id => cat.TryGetValue(id, out var c) ? $"{id}(#{c.rank}{(c.flags.Length > 0 ? "," + c.flags : "")})" : id));
            modNames = ids => ids.Select(id => cat.TryGetValue(id, out var c) ? new { id, rank = c.rank, name = c.name } : new { id, rank = "", name = "" }).ToArray();
            Console.WriteLine();
            Console.WriteLine($"MOD-REFERENCED, public in the shipped assemblies and missing in ours: {modPublic.Count} ({modPublic.Count(k => k.Key.StartsWith("TYPE ", StringComparison.Ordinal))} types)");
            foreach (var (k, ids) in modPublic.OrderBy(k => k.Key, StringComparer.Ordinal)) Console.WriteLine($"  {k}  [{ids.Count}] {Who(ids)}");
            Console.WriteLine();
            Console.WriteLine($"MOD-REFERENCED, missing in ours, not public in the shipped assemblies or not in them: {modOther.Count}");
            foreach (var (k, ids) in modOther.OrderBy(k => k.Key, StringComparer.Ordinal)) Console.WriteLine($"  {k}  [{ids.Count}] {Who(ids)}");
            Console.WriteLine();
            Console.WriteLine($"MOD STRINGS naming a missing member of a type the mod references (reflection candidates): {modReflection.Count}");
            foreach (var (k, ids) in modReflection.OrderBy(k => k.Key, StringComparer.Ordinal)) Console.WriteLine($"  {k}  [{ids.Count}] {Who(ids)}");
        }

        Console.WriteLine();
        foreach (var (t, n) in missing.Types) Console.WriteLine($"  MISSING TYPE   {t}  ({n} members)");
        foreach (var m in missing.Members) Console.WriteLine($"  MISSING MEMBER {m}{(missing.Obsolete.Contains(m) ? "  [Obsolete]" : "")}");

        if (jsonOut != null)
        {
            File.WriteAllText(jsonOut, JsonSerializer.Serialize(new
            {
                missingTypes = missing.Types,
                missingMembers = missing.Members.ToArray(),
                obsolete = missing.Obsolete.ToArray(),
                perAssembly = missing.PerAssembly.ToDictionary(k => k.Key, k => new { k.Value.types, k.Value.members, k.Value.missingTypes, k.Value.membersOfMissing, k.Value.missingMembers }),
                modReferenced = modPublic?.ToDictionary(k => k.Key, k => k.Value.ToArray()),
                modReferencedNotPublic = modOther?.ToDictionary(k => k.Key, k => k.Value.ToArray()),
                modReferencedDlls = modDlls?.ToDictionary(k => k.Key, k => k.Value.ToArray()),
                modReflection = modReflection?.ToDictionary(k => k.Key, k => k.Value.ToArray()),
                mods = modNames == null || modPublic == null ? null : modNames(new SortedSet<string>(modPublic.Values.Concat(modReflection!.Values).SelectMany(v => v), StringComparer.Ordinal)),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }

    static Missing CompareSurface(string gameDir, Func<string, bool> isProvider, OurAssemblies ours)
    {
        var result = new Missing();
        foreach (var file in Directory.EnumerateFiles(gameDir, "*.dll").Order(StringComparer.Ordinal))
        {
            using var pe = new PEReader(File.OpenRead(file));
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) continue;
            var asm = md.GetString(md.GetAssemblyDefinition().Name);
            if (!isProvider(asm)) continue;
            int nTypes = 0, nMembers = 0, nMissingTypes = 0, nOfMissing = 0, nMissingMembers = 0;
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                if (!IsVisible(md, td)) continue;
                var typeName = RefNames.FullName(md, th);
                var unsealed = (td.Attributes & TypeAttributes.Sealed) == 0;
                var members = PublicMembers(md, td, unsealed).ToList();
                nTypes++;
                nMembers += members.Count;
                var ourType = ours.FindType(asm, typeName);
                if (ourType == null)
                {
                    nMissingTypes++;
                    nOfMissing += members.Count;
                    result.Types[$"[{asm}]{typeName}"] = members.Count;
                    continue;
                }
                foreach (var (name, sig, obsolete) in members)
                {
                    if (ours.FindMember(ourType.Value, name, sig) != null) continue;
                    var key = $"[{asm}]{typeName}::{sig.Display(name)}";
                    result.Members.Add(key);
                    if (obsolete) result.Obsolete.Add(key);
                    nMissingMembers++;
                }
            }
            result.PerAssembly[asm] = (nTypes, nMembers, nMissingTypes, nOfMissing, nMissingMembers);
        }
        return result;
    }

    /// Public, or nested public (or protected, in an unsealed type) all the way out.
    static bool IsVisible(MetadataReader md, TypeDefinition td)
    {
        var vis = td.Attributes & TypeAttributes.VisibilityMask;
        var decl = td.GetDeclaringType();
        if (decl.IsNil) return vis == TypeAttributes.Public;
        var outer = md.GetTypeDefinition(decl);
        var outerUnsealed = (outer.Attributes & TypeAttributes.Sealed) == 0;
        var ok = vis == TypeAttributes.NestedPublic || (outerUnsealed && vis is TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem);
        return ok && IsVisible(md, outer);
    }

    static bool Visible(int access, bool unsealed) =>
        access == (int)MethodAttributes.Public || (unsealed && access is (int)MethodAttributes.Family or (int)MethodAttributes.FamORAssem);

    static IEnumerable<(string name, Sig sig, bool obsolete)> PublicMembers(MetadataReader md, TypeDefinition td, bool unsealed)
    {
        foreach (var fh in td.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            if (!Visible((int)(f.Attributes & FieldAttributes.FieldAccessMask), unsealed)) continue;
            yield return (md.GetString(f.Name), Signatures.Decode(md, f.Signature), IsObsolete(md, f.GetCustomAttributes()));
        }
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            if (!Visible((int)(m.Attributes & MethodAttributes.MemberAccessMask), unsealed)) continue;
            yield return (md.GetString(m.Name), Signatures.Decode(md, m.Signature), IsObsolete(md, m.GetCustomAttributes()) || IsObsoleteAccessor(md, td, mh));
        }
    }

    /// An accessor is obsolete when its property or event is.
    static bool IsObsoleteAccessor(MetadataReader md, TypeDefinition td, MethodDefinitionHandle mh)
    {
        foreach (var ph in td.GetProperties())
        {
            var p = md.GetPropertyDefinition(ph);
            var a = p.GetAccessors();
            if ((a.Getter == mh || a.Setter == mh) && IsObsolete(md, p.GetCustomAttributes())) return true;
        }
        foreach (var eh in td.GetEvents())
        {
            var e = md.GetEventDefinition(eh);
            var a = e.GetAccessors();
            if ((a.Adder == mh || a.Remover == mh) && IsObsolete(md, e.GetCustomAttributes())) return true;
        }
        return false;
    }

    static bool IsObsolete(MetadataReader md, CustomAttributeHandleCollection attrs)
    {
        foreach (var ah in attrs)
        {
            var ctor = md.GetCustomAttribute(ah).Constructor;
            EntityHandle owner = ctor.Kind switch
            {
                HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)ctor).Parent,
                HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType(),
                _ => default,
            };
            var name = owner.Kind switch
            {
                HandleKind.TypeReference => new RefNames(md).Resolve((TypeReferenceHandle)owner).name,
                HandleKind.TypeDefinition => RefNames.FullName(md, (TypeDefinitionHandle)owner),
                _ => "",
            };
            if (name == "System.ObsoleteAttribute") return true;
        }
        return false;
    }

    /// Mod references into the providers that ours does not resolve, keyed by where the shipped
    /// assemblies declare them; split into public ones and the rest.
    static (Dictionary<string, SortedSet<string>> pub, Dictionary<string, SortedSet<string>> other, Dictionary<string, SortedSet<string>> dlls, Dictionary<string, SortedSet<string>> reflection) ScanMods(
        string modsDir, string gameDir, Func<string, bool> isProvider, OurAssemblies ours, OurAssemblies shipped, Missing missing)
    {
        var pub = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var other = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var dlls = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var reflection = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Note(Dictionary<string, SortedSet<string>> map, string key, string id)
        {
            if (!map.TryGetValue(key, out var set)) map[key] = set = new(StringComparer.Ordinal);
            set.Add(id);
        }
        bool TypeVisible(FoundType t) => IsVisible(t.Assembly.MD, t.Assembly.MD.GetTypeDefinition(t.Handle));
        string TypeKey(FoundType t) => $"[{t.Assembly.Name}]{RefNames.FullName(t.Assembly.MD, t.Handle)}";
        // Missing members of present types by declaring type ("[asm]Type") and by the name a
        // reflection call would use (accessors by their property's or event's name).
        var byType = new Dictionary<string, List<(string name, string key)>>(StringComparer.Ordinal);
        foreach (var key in missing.Members)
        {
            var sep = key.IndexOf("::", StringComparison.Ordinal);
            var type = key[..sep];
            var sig = key[(sep + 2)..];
            var paren = sig.IndexOf('(');
            var head = paren < 0 ? sig : sig[..paren];
            var name = head[(head.LastIndexOf(' ') + 1)..];
            var lt = name.IndexOf('<');
            if (lt > 0) name = name[..lt];
            foreach (var prefix in new[] { "get_", "set_", "add_", "remove_" })
                if (name.StartsWith(prefix, StringComparison.Ordinal)) { name = name[prefix.Length..]; break; }
            if (name.StartsWith('.')) continue;
            if (!byType.TryGetValue(type, out var list)) byType[type] = list = new();
            list.Add((name, key));
        }

        foreach (var dir in Directory.GetDirectories(modsDir).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(dir);
            var folders = new List<string> { dir };
            var archived = Path.Combine(dir, "archived_versions");
            if (Directory.Exists(archived)) folders.AddRange(Directory.GetDirectories(archived).Order(StringComparer.Ordinal));
            foreach (var file in folders.SelectMany(f => Directory.EnumerateFiles(f, "*.dll", SearchOption.TopDirectoryOnly))
                         .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal)))
            {
                PEReader pe;
                MetadataReader md;
                try
                {
                    pe = new PEReader(File.OpenRead(file));
                    if (!pe.HasMetadata) { pe.Dispose(); continue; }
                    md = pe.GetMetadataReader();
                }
                catch (BadImageFormatException) { continue; }
                using var _ = pe;
                if (!md.IsAssembly) continue;
                var self = md.GetString(md.GetAssemblyDefinition().Name);
                if (isProvider(self) || File.Exists(Path.Combine(gameDir, self + ".dll"))) continue;
                var dllName = id + "/" + Path.GetRelativePath(dir, file).Replace('\\', '/');
                var names = new RefNames(md);
                var referencedTypes = new HashSet<string>(StringComparer.Ordinal);
                // An obfuscated or damaged assembly can hold metadata the reader cannot follow:
                // what it read before that counts, the rest of it is skipped.
                try
                {
                    foreach (var h in md.TypeReferences)
                    {
                        var (asm, name) = names.Resolve(h);
                        if (asm == null || !isProvider(asm)) continue;
                        if (ours.FindType(asm, name) is { } ot)
                        {
                            // Members are keyed by the shipped declaring type (references bind
                            // through base types), so note the type and its shipped bases.
                            for (var t = shipped.FindType(asm, name); t is { } tt; t = shipped.BaseOf(tt)) referencedTypes.Add(TypeKey(tt));
                            continue;
                        }
                        if (shipped.FindType(asm, name) is { } st)
                        {
                            Note(TypeVisible(st) ? pub : other, "TYPE " + TypeKey(st), id);
                            Note(dlls, "TYPE " + TypeKey(st), dllName);
                        }
                        else Note(other, $"TYPE [{asm}]{name} (not in the shipped assemblies)", id);
                    }
                    foreach (var h in md.MemberReferences)
                    {
                        var mr = md.GetMemberReference(h);
                        string? asm; string typeName;
                        switch (mr.Parent.Kind)
                        {
                            case HandleKind.TypeReference: (asm, typeName) = names.Resolve((TypeReferenceHandle)mr.Parent); break;
                            case HandleKind.TypeSpecification: (asm, typeName) = names.ResolveSpecOwner((TypeSpecificationHandle)mr.Parent); break;
                            default: continue;
                        }
                        if (asm == null || !isProvider(asm)) continue;
                        var ourType = ours.FindType(asm, typeName);
                        if (ourType == null) continue; // reported as its type
                        var name = md.GetString(mr.Name);
                        Sig sig;
                        try { sig = Signatures.OfReference(md, mr); }
                        catch (BadImageFormatException) { continue; }
                        if (ours.FindMember(ourType.Value, name, sig) != null) continue;
                        var st = shipped.FindType(asm, typeName);
                        var found = st is { } t ? shipped.FindMember(t, name, sig) : null;
                        if (found is not { } f) { Note(other, $"[{asm}]{typeName}::{sig.Display(name)} (not in the shipped assemblies)", id); continue; }
                        var decl = new FoundType(f.Assembly, f.Declaring);
                        var declTd = f.Assembly.MD.GetTypeDefinition(f.Declaring);
                        var isPublic = TypeVisible(decl) && Visible(f.Access, (declTd.Attributes & TypeAttributes.Sealed) == 0);
                        Note(isPublic ? pub : other, $"{TypeKey(decl)}::{sig.Display(name)}", id);
                        Note(dlls, $"{TypeKey(decl)}::{sig.Display(name)}", dllName);
                    }
                    var strings = new HashSet<string>(StringComparer.Ordinal);
                    for (var uh = MetadataTokens.UserStringHandle(1); !uh.IsNil; uh = md.GetNextHandle(uh)) strings.Add(md.GetUserString(uh));
                    foreach (var type in referencedTypes)
                        if (byType.TryGetValue(type, out var list))
                            foreach (var (name, key) in list)
                                if (strings.Contains(name)) { Note(reflection, key, id); Note(dlls, "STRING " + key, dllName); }
                }
                catch (BadImageFormatException e)
                {
                    Console.Error.WriteLine($"{file}: {e.Message}; the rest of it skipped");
                }
            }
        }
        return (pub, other, dlls, reflection);
    }

    static Dictionary<string, (string rank, string flags, string name)> ReadCatalog(string path)
    {
        var map = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var f = line.Split('\t');
            if (f.Length >= 5) map[f[1]] = (f[0], f[3], f[4]);
            else if (f.Length >= 4) map[f[1]] = (f[0], f[3], "");
        }
        return map;
    }
}
