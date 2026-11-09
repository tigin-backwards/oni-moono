// Small helpers over System.Reflection.Metadata: type names, signatures, custom attribute values
// and an IL operand walker. Nothing here loads an assembly.
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ModTriage;

/// A type as seen from a mod: its full name ("Ns.Outer+Inner") and, for a reference, the
/// assembly it names (null for a definition in the module itself).
public readonly record struct TypeName(string FullName, string? Assembly, TypeDefinitionHandle Definition)
{
    public string Namespace => Names.Namespace(FullName);
    /// The outermost type's full name, without nested parts.
    public string Outer => FullName.IndexOf('+') is var i and >= 0 ? FullName[..i] : FullName;
    public string Simple => Names.Simple(FullName);
    public bool IsDefinition => !Definition.IsNil;
}

public static class Names
{
    /// The outermost type's simple name: "Ns.Outer+Inner" → "Outer".
    public static string OuterSimple(string fullName)
    {
        var outer = fullName.IndexOf('+') is var p and >= 0 ? fullName[..p] : fullName;
        return outer[(outer.LastIndexOf('.') + 1)..];
    }

    /// The innermost simple name: "Ns.Outer+Inner" → "Inner".
    public static string Simple(string fullName)
    {
        var i = Math.Max(fullName.LastIndexOf('.'), fullName.LastIndexOf('+'));
        return fullName[(i + 1)..];
    }

    public static string Namespace(string fullName)
    {
        var outer = fullName.IndexOf('+') is var p and >= 0 ? fullName[..p] : fullName;
        var i = outer.LastIndexOf('.');
        return i < 0 ? "" : outer[..i];
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

    public static TypeName Of(MetadataReader md, TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        var name = md.GetString(tr.Name);
        var ns = md.GetString(tr.Namespace);
        switch (tr.ResolutionScope.Kind)
        {
            case HandleKind.TypeReference:
                var outer = Of(md, (TypeReferenceHandle)tr.ResolutionScope);
                return outer with { FullName = outer.FullName + "+" + name };
            case HandleKind.AssemblyReference:
                var ar = md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope);
                return new TypeName(ns.Length > 0 ? ns + "." + name : name, md.GetString(ar.Name), default);
            default:
                return new TypeName(ns.Length > 0 ? ns + "." + name : name, null, default);
        }
    }

    /// A type handle of any kind; a generic instantiation, array or pointer is named by the type
    /// it is built on. Null when there is no such type (a generic parameter, say).
    public static TypeName? Of(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition:
                var td = (TypeDefinitionHandle)h;
                return new TypeName(FullName(md, td), null, td);
            case HandleKind.TypeReference:
                return Of(md, (TypeReferenceHandle)h);
            case HandleKind.TypeSpecification:
                var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
                return OfSignature(md, ref blob);
            default:
                return null;
        }
    }

    static TypeName? OfSignature(MetadataReader md, ref BlobReader blob)
    {
        var code = blob.ReadSignatureTypeCode();
        switch (code)
        {
            case SignatureTypeCode.GenericTypeInstance:
                blob.ReadSignatureTypeCode();
                return Of(md, blob.ReadTypeHandle());
            case SignatureTypeCode.TypeHandle:
                return Of(md, blob.ReadTypeHandle());
            case SignatureTypeCode.SZArray:
            case SignatureTypeCode.Pointer:
            case SignatureTypeCode.ByReference:
            case SignatureTypeCode.Pinned:
                return OfSignature(md, ref blob);
            case SignatureTypeCode.Array:
                return OfSignature(md, ref blob);
            default:
                return null;
        }
    }

    /// The type that declares a member token (MemberRef, MethodDef, FieldDef, MethodSpec).
    public static (TypeName? owner, string member) MemberOf(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.MemberReference:
                var mr = md.GetMemberReference((MemberReferenceHandle)h);
                var name = md.GetString(mr.Name);
                return mr.Parent.Kind switch
                {
                    HandleKind.MethodDefinition => (Of(md, md.GetMethodDefinition((MethodDefinitionHandle)mr.Parent).GetDeclaringType()), name),
                    _ => (Of(md, mr.Parent), name),
                };
            case HandleKind.MethodDefinition:
                var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                return (Of(md, m.GetDeclaringType()), md.GetString(m.Name));
            case HandleKind.FieldDefinition:
                var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                return (Of(md, f.GetDeclaringType()), md.GetString(f.Name));
            case HandleKind.MethodSpecification:
                return MemberOf(md, md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
            default:
                return (null, "");
        }
    }

    /// The constructor's declaring type of a custom attribute.
    public static TypeName? AttributeType(MetadataReader md, CustomAttribute ca) => MemberOf(md, ca.Constructor).owner;

    /// "Ns.Type+Nested, Assembly, Version=…" → "Ns.Type+Nested".
    public static string FromSerialized(string name)
    {
        int depth = 0;
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '[') depth++;
            else if (name[i] == ']') depth--;
            else if (name[i] == ',' && depth == 0) return name[..i].Trim();
        }
        return name.Trim();
    }
}

/// Type names in signatures, assembly-agnostic; used for field types and attribute arguments.
public sealed class SignatureNames(MetadataReader md) : ISignatureTypeProvider<string, object?>, ICustomAttributeTypeProvider<string>
{
    public const string SystemType = "System.Type";
    public string GetArrayType(string e, ArrayShape s) => e + "[]";
    public string GetByReferenceType(string e) => e;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => $"{g}<{string.Join(",", a)}>";
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode t) => "System." + t;
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Names.FullName(r, h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Names.Of(r, h).FullName;
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => Names.Of(r, h)?.FullName ?? "?";
    public string GetSystemType() => SystemType;
    public bool IsSystemType(string type) => type == SystemType;
    public string GetTypeFromSerializedName(string name) => Names.FromSerialized(name);
    // Enums in attribute blobs: Harmony's MethodType and ArgumentType are ints, as are nearly all
    // others; a wrong guess only fails that attribute's decoding, which the caller tolerates.
    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

    /// All type names a signature mentions, generic arguments included.
    public static IEnumerable<string> Split(string sig) =>
        sig.Split(['<', '>', ',', '[', ']', '*'], StringSplitOptions.RemoveEmptyEntries).Where(s => !s.StartsWith('!'));

    public MetadataReader Reader => md;
}

/// One IL instruction with a metadata token or string operand.
public readonly record struct TokenOperand(int Offset, ushort OpCode, int Token);

public static class IL
{
    public const ushort Call = 0x28, Callvirt = 0x6F, Newobj = 0x73, Ldstr = 0x72, Ldtoken = 0xD0, Ldftn = 0xFE06, Ldvirtftn = 0xFE07;

    /// The instructions of a method body that carry a token (calls, field and type operands,
    /// ldstr, ldtoken), in order.
    public static List<TokenOperand> Tokens(BlobReader il)
    {
        var list = new List<TokenOperand>();
        while (il.RemainingBytes > 0)
        {
            int offset = il.Offset;
            ushort op = il.ReadByte();
            if (op == 0xFE) op = (ushort)(0xFE00 | il.ReadByte());
            int size = OperandSize(op);
            if (size == -1) // switch
            {
                int n = il.ReadInt32();
                il.Offset += 4 * n;
                continue;
            }
            if (IsToken(op)) { list.Add(new TokenOperand(offset, op, il.ReadInt32())); continue; }
            if (size > il.RemainingBytes) break;
            il.Offset += size;
        }
        return list;
    }

    static bool IsToken(ushort op) => op switch
    {
        0x27 or 0x28 or 0x29 or 0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x79
            or >= 0x7B and <= 0x81 or 0x8C or 0x8D or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0
            or 0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C => true,
        _ => false,
    };

    /// Operand size in bytes; -1 for switch's variable table.
    static int OperandSize(ushort op) => op switch
    {
        >= 0x0E and <= 0x13 => 1,            // ldarg.s … stloc.s
        0x1F => 1,                            // ldc.i4.s
        0x20 or 0x22 => 4,                    // ldc.i4, ldc.r4
        0x21 or 0x23 => 8,                    // ldc.i8, ldc.r8
        >= 0x2B and <= 0x37 => 1,             // short branches
        >= 0x38 and <= 0x44 => 4,             // long branches
        0x45 => -1,                           // switch
        0xDD => 4,
        0xDE => 1,                 // leave, leave.s
        0xFE09 or 0xFE0A or 0xFE0B or 0xFE0C or 0xFE0D or 0xFE0E => 2,
        0xFE12 or 0xFE19 => 1,                // unaligned., no.
        _ => IsToken(op) ? 4 : 0,
    };

    public static bool IsCall(ushort op) => op is Call or Callvirt or Newobj or Ldftn or Ldvirtftn;

    public static EntityHandle Handle(int token) => MetadataTokens.EntityHandle(token);
}
