// Conformance check: every type and member that the game's assemblies reference in the Unity
// layer must resolve in our replacement assemblies, the way the runtime binder would resolve it
// (by assembly name, following type forwarders, then by member name and signature).
// Members whose body starts with `throw new NotImplementedException()` are reported as stubs.
//
// usage: ApiCheck <gameManagedDir> <ourAssemblyDir> <providerPrefix,...> <consumer.dll> [consumer.dll...]
//        [--json out.json]
//        ApiCheck --surface ...   (see Surface.cs: Unity's whole public surface, and what mods use of it)
// Exit code: 0 when everything resolves (stubs allowed), 1 when anything is missing.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

if (args.Length > 0 && args[0] == "--surface") return Surface.Run(args[1..]);
var positional = new List<string>();
string? jsonOut = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--json") jsonOut = args[++i];
    else positional.Add(args[i]);
}
var gameDir = positional[0];
var ourDir = positional[1];
var prefixes = positional[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
var consumers = positional.Skip(3).ToArray();

// A provider ending in '.' is a prefix; otherwise it names one assembly exactly.
bool IsProvider(string asm) => prefixes.Any(p => p.EndsWith('.') ? asm.StartsWith(p, StringComparison.Ordinal) : asm == p);

var ours = new OurAssemblies(ourDir);
var missingTypes = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
var missingMembers = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
var stubs = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
int typeRefs = 0, memberRefs = 0;

void Note(SortedDictionary<string, SortedSet<string>> map, string key, string consumer)
{
    if (!map.TryGetValue(key, out var set)) map[key] = set = new(StringComparer.Ordinal);
    set.Add(consumer);
}

foreach (var path in consumers)
{
    using var pe = new PEReader(File.OpenRead(Path.Combine(gameDir, path)));
    var md = pe.GetMetadataReader();
    var consumer = md.GetString(md.GetAssemblyDefinition().Name);
    var names = new RefNames(md);

    foreach (var h in md.TypeReferences)
    {
        var (asm, name) = names.Resolve(h);
        if (asm == null || !IsProvider(asm)) continue;
        typeRefs++;
        if (ours.FindType(asm, name) == null) Note(missingTypes, $"[{asm}]{name}", consumer);
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
        if (asm == null || !IsProvider(asm)) continue;
        memberRefs++;
        var name = md.GetString(mr.Name);
        var sig = Signatures.OfReference(md, mr);
        var key = $"[{asm}]{typeName}::{sig.Display(name)}";
        var type = ours.FindType(asm, typeName);
        if (type == null) { Note(missingTypes, $"[{asm}]{typeName}", consumer); continue; }
        var found = ours.FindMember(type.Value, name, sig);
        if (found == null) Note(missingMembers, key, consumer);
        else if (found.Value.IsStub) Note(stubs, key, consumer);
    }
}

Console.WriteLine($"type refs: {typeRefs}, member refs: {memberRefs}");
Console.WriteLine($"missing types: {missingTypes.Count}, missing members: {missingMembers.Count}, stub members: {stubs.Count}");
foreach (var (k, v) in missingTypes) Console.WriteLine($"  MISSING TYPE   {k}  ({string.Join(",", v)})");
foreach (var (k, v) in missingMembers) Console.WriteLine($"  MISSING MEMBER {k}  ({string.Join(",", v)})");
if (jsonOut != null)
{
    File.WriteAllText(jsonOut, JsonSerializer.Serialize(new
    {
        missingTypes = missingTypes.ToDictionary(x => x.Key, x => x.Value.ToArray()),
        missingMembers = missingMembers.ToDictionary(x => x.Key, x => x.Value.ToArray()),
        stubs = stubs.ToDictionary(x => x.Key, x => x.Value.ToArray()),
    }, new JsonSerializerOptions { WriteIndented = true }));
}
return missingTypes.Count + missingMembers.Count == 0 ? 0 : 1;

/// A signature reduced to type names (assembly-agnostic), which is what binding compares once
/// forwarders have been followed: our implementation types live in different assemblies than
/// the Unity module names the game was compiled against.
sealed record Sig(bool IsField, bool IsInstance, int GenericArity, string Return, ImmutableArray<string> Params)
{
    public string Display(string name) => IsField
        ? $"field {Return} {name}"
        : $"{(IsInstance ? "" : "static ")}{Return} {name}{(GenericArity > 0 ? $"<{GenericArity}>" : "")}({string.Join(", ", Params)})";
}

static class Signatures
{
    public static Sig OfReference(MetadataReader md, MemberReference mr) => Decode(md, mr.Signature);

    public static Sig Decode(MetadataReader md, BlobHandle blobHandle)
    {
        var blob = md.GetBlobReader(blobHandle);
        var header = blob.ReadSignatureHeader();
        blob.Reset();
        var dec = new SignatureDecoder<string, object?>(new SigNames(md), md, null);
        if (header.Kind == SignatureKind.Field)
            return new Sig(true, false, 0, dec.DecodeFieldSignature(ref blob), ImmutableArray<string>.Empty);
        var ms = dec.DecodeMethodSignature(ref blob);
        return new Sig(false, ms.Header.IsInstance, ms.GenericParameterCount, ms.ReturnType, ms.ParameterTypes);
    }
}

/// Signature type names without assembly qualification.
sealed class SigNames(MetadataReader md) : ISignatureTypeProvider<string, object?>
{
    readonly RefNames refs = new(md);
    public string GetArrayType(string e, ArrayShape s) => $"{e}[{new string(',', s.Rank - 1)}]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => $"{g}<{string.Join(",", a)}>";
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool r) => r ? $"{u} modreq({m})" : u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode t) => t switch
    {
        #pragma warning disable format // a hand-laid table
        PrimitiveTypeCode.Boolean => "System.Boolean", PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.SByte => "System.SByte", PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Int16 => "System.Int16", PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32", PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.Int64 => "System.Int64", PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.Single => "System.Single", PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.IntPtr => "System.IntPtr", PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Object => "System.Object", PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.TypedReference => "System.TypedReference", PrimitiveTypeCode.Void => "System.Void",
        #pragma warning restore format
        _ => t.ToString(),
    };
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => RefNames.FullName(r, h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => refs.Resolve(h).name;
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => "spec";
}

sealed class RefNames(MetadataReader md)
{
    public (string? asm, string name) Resolve(TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        var name = md.GetString(tr.Name);
        var ns = md.GetString(tr.Namespace);
        switch (tr.ResolutionScope.Kind)
        {
            case HandleKind.TypeReference:
                var (asm, outer) = Resolve((TypeReferenceHandle)tr.ResolutionScope);
                return (asm, outer + "+" + name);
            case HandleKind.AssemblyReference:
                var ar = md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope);
                return (md.GetString(ar.Name), ns.Length > 0 ? ns + "." + name : name);
            default:
                return (null, ns.Length > 0 ? ns + "." + name : name);
        }
    }

    public (string? asm, string name) ResolveSpecOwner(TypeSpecificationHandle h)
    {
        var blob = md.GetBlobReader(md.GetTypeSpecification(h).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return (null, "");
        blob.ReadSignatureTypeCode();
        var handle = blob.ReadTypeHandle();
        return handle.Kind == HandleKind.TypeReference ? Resolve((TypeReferenceHandle)handle) : (null, "");
    }

    public static string FullName(MetadataReader md, TypeDefinitionHandle h)
    {
        var td = md.GetTypeDefinition(h);
        var name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return FullName(md, decl) + "+" + name;
        var ns = md.GetString(td.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }
}

readonly record struct FoundType(LoadedAssembly Assembly, TypeDefinitionHandle Handle);
/// Where a member resolved: its declaring type and its access (the MemberAccessMask bits, the same
/// values for fields and methods).
readonly record struct FoundMember(bool IsStub, LoadedAssembly Assembly, TypeDefinitionHandle Declaring, int Access);

sealed class LoadedAssembly : IDisposable
{
    public required string Name;
    public required PEReader PE;
    public required MetadataReader MD;
    public readonly Dictionary<string, TypeDefinitionHandle> Types = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> Forwards = new(StringComparer.Ordinal); // type -> target assembly
    public void Dispose() => PE.Dispose();
}

sealed class OurAssemblies
{
    readonly Dictionary<string, LoadedAssembly> byName = new(StringComparer.Ordinal);

    public OurAssemblies(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.dll"))
        {
            var pe = new PEReader(File.OpenRead(file));
            if (!pe.HasMetadata) { pe.Dispose(); continue; }
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) { pe.Dispose(); continue; }
            var la = new LoadedAssembly { Name = md.GetString(md.GetAssemblyDefinition().Name), PE = pe, MD = md };
            foreach (var h in md.TypeDefinitions) la.Types[RefNames.FullName(md, h)] = h;
            foreach (var h in md.ExportedTypes)
            {
                var et = md.GetExportedType(h);
                if (!et.IsForwarder) continue;
                var name = md.GetString(et.Name);
                var ns = md.GetString(et.Namespace);
                var full = ns.Length > 0 ? ns + "." + name : name;
                la.Forwards[full] = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)et.Implementation).Name);
            }
            byName[la.Name] = la;
        }
    }

    public FoundType? FindType(string asm, string fullName)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            if (!byName.TryGetValue(asm, out var la)) return null;
            if (la.Types.TryGetValue(fullName, out var h)) return new FoundType(la, h);
            // Nested types are forwarded with their outermost declaring type.
            var outer = fullName.Split('+')[0];
            if (!la.Forwards.TryGetValue(outer, out var target)) return null;
            asm = target;
        }
        return null;
    }

    public FoundMember? FindMember(FoundType type, string name, Sig sig)
    {
        var md = type.Assembly.MD;
        // Walk the base type chain: references to inherited members bind through base classes.
        TypeDefinitionHandle? current = type.Handle;
        var currentAsm = type.Assembly;
        while (current is { } th)
        {
            md = currentAsm.MD;
            var td = md.GetTypeDefinition(th);
            if (sig.IsField)
            {
                foreach (var fh in td.GetFields())
                {
                    var f = md.GetFieldDefinition(fh);
                    if (md.GetString(f.Name) != name) continue;
                    if (Signatures.Decode(md, f.Signature).Return == sig.Return)
                        return new FoundMember(false, currentAsm, th, (int)(f.Attributes & FieldAttributes.FieldAccessMask));
                }
            }
            else
            {
                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (md.GetString(m.Name) != name) continue;
                    var ms = Signatures.Decode(md, m.Signature);
                    if (ms.IsInstance != sig.IsInstance || ms.GenericArity != sig.GenericArity || ms.Return != sig.Return
                        || !ms.Params.SequenceEqual(sig.Params)) continue;
                    return new FoundMember(IsStub(currentAsm, m), currentAsm, th, (int)(m.Attributes & MethodAttributes.MemberAccessMask));
                }
            }
            // Constructors and static members never bind through base types.
            if (name == ".ctor" || name == ".cctor") return null;
            (current, currentAsm) = BaseOf(currentAsm, td);
        }
        return null;
    }

    /// The type's base type, when it is in these assemblies.
    public FoundType? BaseOf(FoundType type)
    {
        var (h, la) = BaseOf(type.Assembly, type.Assembly.MD.GetTypeDefinition(type.Handle));
        return h is { } th ? new FoundType(la, th) : null;
    }

    (TypeDefinitionHandle?, LoadedAssembly) BaseOf(LoadedAssembly la, TypeDefinition td)
    {
        var md = la.MD;
        // System.Object's (and an interface's) base is nil, which reads as TypeDefinition row 0.
        if (td.BaseType.IsNil) return (null, la);
        switch (td.BaseType.Kind)
        {
            case HandleKind.TypeDefinition:
                return ((TypeDefinitionHandle)td.BaseType, la);
            case HandleKind.TypeReference:
                var (asm, name) = new RefNames(md).Resolve((TypeReferenceHandle)td.BaseType);
                if (asm != null && FindType(asm, name) is { } ft) return (ft.Handle, ft.Assembly);
                return (null, la);
            case HandleKind.TypeSpecification:
                var (sasm, sname) = new RefNames(md).ResolveSpecOwner((TypeSpecificationHandle)td.BaseType);
                if (sasm != null && FindType(sasm, sname) is { } st) return (st.Handle, st.Assembly);
                return (null, la);
            default:
                return (null, la);
        }
    }

    /// True when the IL body is exactly `newobj NotImplementedException::.ctor(...); throw`.
    static bool IsStub(LoadedAssembly la, MethodDefinition m)
    {
        if (m.RelativeVirtualAddress == 0) return false;
        var body = la.PE.GetMethodBody(m.RelativeVirtualAddress);
        var il = body.GetILReader();
        if (il.RemainingBytes < 6 || il.ReadByte() != 0x73) return false; // newobj
        var token = il.ReadInt32();
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.MemberReference) return false;
        var ctor = la.MD.GetMemberReference((MemberReferenceHandle)handle);
        if (ctor.Parent.Kind != HandleKind.TypeReference) return false;
        var (_, owner) = new RefNames(la.MD).Resolve((TypeReferenceHandle)ctor.Parent);
        return owner == "System.NotImplementedException" && il.ReadByte() == 0x7A; // throw
    }
}
