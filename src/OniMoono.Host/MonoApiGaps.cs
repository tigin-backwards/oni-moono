using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using OniMoono.Tweaks;

namespace OniMoono.Host
{
    /// ARCHITECTURE D57. A mod names a type or member of an assembly ours answers for in place of
    /// the original's copy in Managed/ (Unity's engine modules and packages, which ours replace, and
    /// Mono's class libraries, for which the framework answers), and ours cannot resolve it where
    /// the original can: ONI Boost Kit's GCSentinel reads `JobsUtility.JobWorkerCount`, which our
    /// engine lacked, and Artificial Natural Tiles' TryLoadSprite builds a `System.Drawing.Bitmap`,
    /// whose assembly .NET forwards to System.Drawing.Common, which the framework does not carry.
    /// CoreCLR fails such a method as a whole when it compiles it, before any of it runs, so the
    /// mod's own try/catch around the reference never sees the exception. On Mono the reference
    /// resolves and the method runs. This tells such references from what both lack by the
    /// original's metadata: MonoMissingTypes then makes each method naming a gap compile, each
    /// instruction naming one throwing where it runs, so the rest of the method runs as on Mono and
    /// its catch sees the exception: the one Mono gives for a reference that does not resolve
    /// (UnityProbe's atcall: FileNotFoundException for an assembly it cannot load). What both lack
    /// fails as a whole, as on Mono (D40, D42).
    internal static class MonoApiGaps
    {
        /// Whether ours answers, for the assembly of that simple name, in place of the original's
        /// copy in Managed/.
        internal static bool OursAnswer(string assemblyName)
        {
            if (AssemblyPolicy.ReferenceDirectories[0] is not { Length: > 0 } managed || !File.Exists(Path.Combine(managed, assemblyName + ".dll"))) return false;
            return assemblyName.StartsWith("Unity", StringComparison.Ordinal) || AssemblyPolicy.IsFrameworkAssembly(assemblyName)
                || AssemblyPolicy.IsMonoClassLibrary(assemblyName);
        }

        /// How ours resolves a top-level type of such an assembly: the type, or null with the reason
        /// (the assembly, or one a forwarder names, cannot be loaded; or it lacks the type).
        internal enum OursFailure { None, Assembly, Type }

        static readonly ConcurrentDictionary<(string, string), (Type?, OursFailure, string?)> ours = new();

        /// `File` is the display name of the assembly ours could not load.
        [NameLookupReviewed("A type a mod names in an assembly ours answers for, by that name")]
        internal static (Type? Type, OursFailure Failure, string? File) Ours(string assemblyName, string fullName) =>
            ours.GetOrAdd((assemblyName, fullName), key =>
            {
                Assembly assembly;
                try
                {
                    assembly = MonoMissingTypes.Loaded(key.Item1) ?? AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(key.Item1));
                }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    return (null, OursFailure.Assembly, (e as FileNotFoundException)?.FileName ?? key.Item1);
                }
                try
                {
                    return assembly.GetType(key.Item2, throwOnError: true) is { } type ? (type, OursFailure.None, null) : (null, OursFailure.Type, null);
                }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // A forwarder to an assembly the framework does not carry (System.Drawing's
                    // Bitmap, forwarded to System.Drawing.Common).
                    return (null, OursFailure.Assembly, (e as FileNotFoundException)?.FileName);
                }
                catch (TypeLoadException)
                {
                    return (null, OursFailure.Type, null);
                }
            });

        /// The original's copy of an assembly (Managed/NAME.dll), read as metadata.
        internal sealed class Original
        {
            public readonly MetadataReader Md;
            readonly PEReader pe;
            /// Its types by full name (nested ones as Outer/Inner).
            readonly Dictionary<string, TypeDefinitionHandle> types = new(StringComparer.Ordinal);
            /// Its forwarders: full name → the assembly named.
            readonly Dictionary<string, string> forwarded = new(StringComparer.Ordinal);
            readonly ConcurrentDictionary<TypeDefinitionHandle, HashSet<string>> members = new();

            Original(byte[] image)
            {
                pe = new PEReader(ImmutableArray.Create(image));
                Md = pe.GetMetadataReader();
                foreach (var handle in Md.TypeDefinitions) types[FullName(handle)] = handle;
                foreach (var handle in Md.ExportedTypes)
                {
                    var exported = Md.GetExportedType(handle);
                    if (exported.Implementation.Kind != HandleKind.AssemblyReference) continue;
                    var ns = Md.GetString(exported.Namespace);
                    var name = Md.GetString(exported.Name);
                    forwarded[ns.Length == 0 ? name : ns + "." + name] = Md.GetString(Md.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name);
                }
            }

            string FullName(TypeDefinitionHandle handle)
            {
                var type = Md.GetTypeDefinition(handle);
                var outer = type.GetDeclaringType();
                if (!outer.IsNil) return FullName(outer) + "/" + Md.GetString(type.Name);
                var ns = Md.GetString(type.Namespace);
                return ns.Length == 0 ? Md.GetString(type.Name) : ns + "." + Md.GetString(type.Name);
            }

            static readonly ConcurrentDictionary<string, Original?> copies = new(StringComparer.OrdinalIgnoreCase);

            internal static Original? Of(string assemblyName) => copies.GetOrAdd(assemblyName, name =>
            {
                if (AssemblyPolicy.ReferenceDirectories[0] is not { Length: > 0 } managed) return null;
                var path = Path.Combine(managed, name + ".dll");
                if (!File.Exists(path)) return null;
                try
                {
                    return new Original(File.ReadAllBytes(path));
                }
                catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return null;
                }
            });

            /// The type of that full name in the copy of that assembly, its forwarders followed.
            internal static (Original Copy, TypeDefinitionHandle Type)? Find(string assemblyName, string fullName, int depth = 0)
            {
                if (depth > 8 || Of(assemblyName) is not { } copy) return null;
                if (copy.types.TryGetValue(fullName, out var handle)) return (copy, handle);
                var top = fullName.IndexOf('/') is var slash and >= 0 ? fullName[..slash] : fullName;
                return copy.forwarded.TryGetValue(top, out var target) ? Find(target, fullName, depth + 1) : null;
            }

            /// An enum's underlying type (its instance field's), or null for another type.
            internal PrimitiveTypeCode? EnumUnderlying(TypeDefinitionHandle handle)
            {
                var type = Md.GetTypeDefinition(handle);
                if (type.BaseType.Kind != HandleKind.TypeReference || Name(Md, (TypeReferenceHandle)type.BaseType).FullName != "System.Enum") return null;
                foreach (var f in type.GetFields())
                {
                    var field = Md.GetFieldDefinition(f);
                    if ((field.Attributes & FieldAttributes.Static) != 0) continue;
                    var blob = Md.GetBlobReader(field.Signature);
                    blob.ReadSignatureHeader();
                    return (PrimitiveTypeCode)blob.ReadCompressedInteger();
                }
                return null;
            }

            /// The keys of the members a type declares: "F name type" for a field, "M name key" for
            /// a method (Canon.MethodKey).
            HashSet<string> Declared(TypeDefinitionHandle handle) => members.GetOrAdd(handle, h =>
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var type = Md.GetTypeDefinition(h);
                var canon = new Canon();
                foreach (var f in type.GetFields())
                {
                    var field = Md.GetFieldDefinition(f);
                    try { keys.Add("F " + Md.GetString(field.Name) + " " + field.DecodeSignature(canon, null)); }
                    catch (BadImageFormatException) { }
                }
                foreach (var m in type.GetMethods())
                {
                    var method = Md.GetMethodDefinition(m);
                    try { keys.Add("M " + Md.GetString(method.Name) + " " + Canon.MethodKey(method.DecodeSignature(canon, null))); }
                    catch (BadImageFormatException) { }
                }
                return keys;
            });

            /// Whether the type, or where Mono looks after it (its bases; for an interface, the
            /// interfaces it extends and then System.Object; a constructor in the type alone), has
            /// a member of that key. Null where the walk cannot tell (a generic base).
            internal bool? Has(TypeDefinitionHandle handle, string key, bool constructor, int depth = 0)
            {
                if (depth > 32) return null;
                if (Declared(handle).Contains(key)) return true;
                if (constructor) return false;
                var type = Md.GetTypeDefinition(handle);
                if ((type.Attributes & TypeAttributes.Interface) != 0)
                {
                    foreach (var i in type.GetInterfaceImplementations())
                    {
                        if (Resolve(Md.GetInterfaceImplementation(i).Interface) is not var (copy, t)) return null;
                        if (copy.Has(t, key, false, depth + 1) is not false and var found) return found;
                    }
                    return Find("mscorlib", "System.Object") is var (core, obj) ? core.Has(obj, key, false, depth + 1) : null;
                }
                if (type.BaseType.IsNil) return false;
                return Resolve(type.BaseType) is var (owner, baseType) ? owner.Has(baseType, key, false, depth + 1) : null;
            }

            /// A TypeDef or TypeRef of this copy, in its copy; null for a generic instantiation or a
            /// type the copies lack.
            (Original, TypeDefinitionHandle)? Resolve(EntityHandle handle)
            {
                if (handle.Kind == HandleKind.TypeDefinition) return (this, (TypeDefinitionHandle)handle);
                if (handle.Kind != HandleKind.TypeReference) return null;
                var (assembly, fullName) = Name(Md, (TypeReferenceHandle)handle);
                return assembly == null ? null : Find(assembly, fullName);
            }
        }

        /// A TypeRef's assembly (its outermost type's scope; null for another scope) and full name
        /// (nested types as Outer/Inner).
        internal static (string? Assembly, string FullName) Name(MetadataReader md, TypeReferenceHandle handle)
        {
            var reference = md.GetTypeReference(handle);
            var name = md.GetString(reference.Name);
            switch (reference.ResolutionScope.Kind)
            {
                case HandleKind.TypeReference:
                    var (assembly, outer) = Name(md, (TypeReferenceHandle)reference.ResolutionScope);
                    return (assembly, outer + "/" + name);
                case HandleKind.AssemblyReference:
                    var ns = md.GetString(reference.Namespace);
                    return (md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name), ns.Length == 0 ? name : ns + "." + name);
                default:
                    return (null, name);
            }
        }

        /// Whether the original's copy has the member a member reference names: true, false, or
        /// null where it cannot tell (taken as the original's: it fails where it runs).
        internal static bool? OriginalHas(MetadataReader md, TypeReferenceHandle parent, MemberReference reference)
        {
            var (assembly, fullName) = Name(md, parent);
            if (assembly == null || Original.Find(assembly, fullName) is not var (copy, type)) return null;
            var name = md.GetString(reference.Name);
            var canon = new Canon();
            try
            {
                var key = reference.GetKind() == MemberReferenceKind.Field
                    ? "F " + name + " " + reference.DecodeFieldSignature(canon, null)
                    : "M " + name + " " + Canon.MethodKey(reference.DecodeMethodSignature(canon, null));
                return copy.Has(type, key, name is ".ctor" or ".cctor");
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        /// Types written the same way from a mod's references and the original's definitions:
        /// full names without assemblies (forwarders move types between them), primitives by
        /// their codes, type parameters by position; custom modifiers left out.
        internal sealed class Canon : ISignatureTypeProvider<string, object?>
        {
            internal static string MethodKey(MethodSignature<string> s) =>
                (s.Header.IsInstance ? "i" : "s") + s.GenericParameterCount + " " + s.ReturnType + "(" + string.Join(",", s.ParameterTypes) + ")";

            static readonly HashSet<string> primitives = new(StringComparer.Ordinal)
            {
                "Boolean", "Char", "SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double",
                "IntPtr", "UIntPtr", "Object", "String", "TypedReference", "Void",
            };

            static string Named(string ns, string name) => ns == "System" && primitives.Contains(name) ? name : ns.Length == 0 ? name : ns + "." + name;

            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(shape.Rank - 1, 0)) + "]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetFunctionPointerType(MethodSignature<string> signature) => "*(" + MethodKey(signature) + ")";
            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
            public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
            public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetPinnedType(string elementType) => elementType;
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetSZArrayType(string elementType) => elementType + "[]";

            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                var type = reader.GetTypeDefinition(handle);
                var outer = type.GetDeclaringType();
                return outer.IsNil ? Named(reader.GetString(type.Namespace), reader.GetString(type.Name)) : GetTypeFromDefinition(reader, outer, 0) + "/" + reader.GetString(type.Name);
            }

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var reference = reader.GetTypeReference(handle);
                return reference.ResolutionScope.Kind == HandleKind.TypeReference
                    ? GetTypeFromReference(reader, (TypeReferenceHandle)reference.ResolutionScope, 0) + "/" + reader.GetString(reference.Name)
                    : Named(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
            }

            public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
                reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }
    }
}
