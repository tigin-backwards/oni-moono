using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace OniMoono.Host
{
    /// ARCHITECTURE D42. A mod calls a method or reads a field its type no longer has (Trapdoors &
    /// Floor Hatches' OnSpawn calls `SimMessages.SetCellProperties(int, byte)`). Both runtimes fail
    /// the method naming it as a whole when it is compiled; they word it differently. Mono's JIT
    /// keeps the member lookup's error (mini/method-to-ir.c, CHECK_CFG_ERROR; Unity's fork, branch
    /// unity-6000.3-mbe) and throws it from the caller's frame: "Method not found: void
    /// .SimMessages.SetCellProperties(int,byte)" (metadata/exception.c,
    /// mono_error_set_method_missing; types as metadata/debug-helpers.c's mono_type_get_desc
    /// writes them), "Field not found: int .GlobalKeeper.Field Due to: Could not find field in
    /// class" (mono_error_set_field_missing, from metadata/loader.c's field_from_memberref).
    /// CoreCLR's frame is the failing method's own, and it says "Method not found: 'Void
    /// SimMessages.SetCellProperties(Int32, Byte)'.". Measured in the original player (UnityProbe's
    /// missingmembers).
    ///
    /// This finds a mod's member references to types that exist, in an assembly whose types are
    /// the same in both players (MonoMissingTypes), that name no member of that type or its bases
    /// with that name and signature, and Mono's message for each; MonoMissingTypes then gives the
    /// methods naming them the body Mono's compilation gives them, as for a missing type. The
    /// lookup is the runtime's, over the types loaded here (bases from Unity's engine are ours), so
    /// a member it finds the runtime binds; a reference whose signature names a type that cannot be
    /// resolved here (the mod's own, a missing one: MonoMissingTypes) is taken as found.
    internal static class MonoMissingMembers
    {
        internal readonly record struct Missing(bool Field, string Message);

        /// The member references (by token) the runtime finds no member for, with Mono's messages.
        internal static Dictionary<int, Missing> Find(MetadataReader md)
        {
            var found = new Dictionary<int, Missing>();
            var provider = new Provider(md);
            foreach (var handle in md.MemberReferences)
            {
                var reference = md.GetMemberReference(handle);
                string? message;
                try
                {
                    if (Parent(md, provider, reference.Parent) is not var (type, klass, typeArguments)) continue;
                    var name = md.GetString(reference.Name);
                    var context = new Context(typeArguments);
                    message = reference.GetKind() == MemberReferenceKind.Field
                        ? FieldMissing(type, name, reference.DecodeFieldSignature(provider, context), klass)
                        : MethodMissing(type, name, reference, provider, context, klass);
                }
                catch (Exception e) when (e is BadImageFormatException or TypeLoadException or System.IO.IOException or NotSupportedException or ArgumentException)
                {
                    // Taken as found: the method fails, if it does, as CoreCLR fails it.
                    continue;
                }
                if (message != null) found[MetadataTokens.GetToken(handle)] = new Missing(reference.GetKind() == MemberReferenceKind.Field, message);
            }
            return found;
        }

        /// The type a member reference's parent names (a generic one's definition), as Mono names it
        /// in the message (namespace, '.', name, the namespace empty for a nested type), with the
        /// definition's type parameters; null where it is not a type of an assembly the same in both
        /// players.
        static (Type Type, string Klass, Type[] TypeArguments)? Parent(MetadataReader md, Provider provider, EntityHandle parent)
        {
            if (parent.Kind == HandleKind.TypeSpecification)
            {
                var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                if (blob.ReadSignatureHeader().RawValue != (byte)SignatureTypeCode.GenericTypeInstance) return null;
                blob.ReadCompressedInteger(); // class or valuetype
                var definition = blob.ReadTypeHandle();
                if (definition.Kind != HandleKind.TypeReference) return null;
                parent = definition;
            }
            if (parent.Kind != HandleKind.TypeReference) return null;
            var handle = (TypeReferenceHandle)parent;
            if (provider.Resolve(handle) is not { } type || !provider.SameInBothPlayers(type)) return null;
            var reference = md.GetTypeReference(handle);
            var klass = md.GetString(reference.Namespace) + "." + md.GetString(reference.Name);
            return (type, klass, type.IsGenericTypeDefinition ? type.GetGenericArguments() : Type.EmptyTypes);
        }

        const BindingFlags DeclaredFlags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ILookup<string, MemberInfo>> declared = new();

        /// The methods, constructors and fields a type declares under a name.
        static IEnumerable<MemberInfo> Declared(Type type, string name) =>
            declared.GetOrAdd(type, t => t.GetMembers(DeclaredFlags).Where(m => m is MethodBase or FieldInfo).ToLookup(m => m.Name, StringComparer.Ordinal))[name];

        static string? MethodMissing(Type type, string name, MemberReference reference, Provider provider, Context context, string klass)
        {
            var signature = reference.DecodeMethodSignature(provider, context);
            if (signature.Header.CallingConvention == SignatureCallingConvention.VarArgs) return null;
            if (signature.ReturnType.Type == null || signature.ParameterTypes.Any(p => p.Type == null)) return null;
            // A generic instantiation over a class tagged `valuetype` is another type on Mono than
            // any the member's signature names (D56).
            bool distinct = signature.ReturnType.Distinct || signature.ParameterTypes.Any(p => p.Distinct);
            bool Matches(MethodBase method)
            {
                if (distinct || method.Name != name || method.IsStatic == signature.Header.IsInstance) return false;
                var arity = method.IsGenericMethodDefinition ? method.GetGenericArguments().Length : 0;
                if (arity != signature.GenericParameterCount) return false;
                var parameters = method.GetParameters();
                if (parameters.Length != signature.ParameterTypes.Length) return false;
                var returns = method is MethodInfo info ? info.ReturnType : typeof(void);
                if (!Same(signature.ReturnType.Type!, returns)) return false;
                for (int i = 0; i < parameters.Length; i++)
                    if (!Same(signature.ParameterTypes[i].Type!, parameters[i].ParameterType)) return false;
                return true;
            }
            bool constructor = name is ".ctor" or ".cctor";
            // Mono looks in the class and its bases (a constructor in the class alone), and for an
            // interface in the interfaces it extends, then in System.Object (loader.c, find_method).
            var levels = constructor ? [type] : type.IsInterface ? [type, .. type.GetInterfaces(), typeof(object)] : Bases(type);
            foreach (var level in levels)
            {
                if (Declared(level, name).Any(m => m is MethodBase method && Matches(method))) return null;
            }
            var text = new System.Text.StringBuilder("Method not found: ");
            text.Append(signature.ReturnType.Desc).Append(' ').Append(klass).Append('.').Append(name);
            if (signature.GenericParameterCount > 0)
                text.Append('<').Append(string.Join(",", Enumerable.Range(0, signature.GenericParameterCount).Select(i => "!" + i))).Append('>');
            text.Append('(').Append(string.Join(",", signature.ParameterTypes.Select(p => p.Desc))).Append(')');
            return text.ToString();
        }

        static string? FieldMissing(Type type, string name, Sig fieldType, string klass)
        {
            if (fieldType.Type == null) return null;
            // Mono looks by name and type in the class and its bases (class.c,
            // mono_class_get_field_from_name_full).
            foreach (var level in Bases(type))
                if (!fieldType.Distinct && Declared(level, name).Any(m => m is FieldInfo f && Same(fieldType.Type, f.FieldType))) return null;
            return $"Field not found: {fieldType.Desc} {klass}.{name} Due to: Could not find field in class";
        }

        static IEnumerable<Type> Bases(Type type)
        {
            for (var level = type; level != null; level = level.BaseType) yield return level;
        }

        /// Whether a type decoded from the reference's signature is the member's: type parameters
        /// by position, the rest by identity.
        static bool Same(Type wanted, Type actual)
        {
            if (wanted.IsGenericMethodParameter) return actual.IsGenericMethodParameter && actual.GenericParameterPosition == wanted.GenericParameterPosition;
            if (wanted.IsGenericTypeParameter) return actual.IsGenericTypeParameter && actual.GenericParameterPosition == wanted.GenericParameterPosition;
            if (actual.IsGenericParameter) return false;
            if (wanted.HasElementType)
            {
                if (!actual.HasElementType || wanted.IsByRef != actual.IsByRef || wanted.IsPointer != actual.IsPointer || wanted.IsArray != actual.IsArray) return false;
                if (wanted.IsArray && (wanted.IsSZArray != actual.IsSZArray || wanted.GetArrayRank() != actual.GetArrayRank())) return false;
                return Same(wanted.GetElementType()!, actual.GetElementType()!);
            }
            if (wanted.IsConstructedGenericType)
            {
                // A generic type over its own parameters is its definition in reflection
                // (`Boxed<T> Boxed<T>.Get(T)`).
                if (!actual.IsGenericType || wanted.GetGenericTypeDefinition() != (actual.IsGenericTypeDefinition ? actual : actual.GetGenericTypeDefinition())) return false;
                var a = wanted.GenericTypeArguments;
                var b = actual.GetGenericArguments();
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (!Same(a[i], b[i])) return false;
                return true;
            }
            return wanted == actual;
        }

        /// A decoded type: the runtime's (null where it cannot be resolved here) and Mono's text
        /// for it (debug-helpers.c, mono_type_get_desc with namespaces); whether the signature
        /// tags it `valuetype` while it is a class of an assembly the same in both players, and
        /// whether it is built from a generic instantiation over such a type, which Mono keeps
        /// apart from the instantiation over the class (D56).
        internal readonly record struct Sig(Type? Type, string Desc, bool Tagged = false, bool Distinct = false);

        internal sealed class Context(Type[] typeArguments)
        {
            public Type[] TypeArguments { get; } = typeArguments;
        }

        sealed class Provider(MetadataReader md) : ISignatureTypeProvider<Sig, Context>
        {
            readonly Dictionary<TypeReferenceHandle, Type?> resolved = new();

            public bool SameInBothPlayers(Type type) => MonoMissingTypes.SameInBothPlayers(type.Assembly);

            public Type? Resolve(TypeReferenceHandle handle)
            {
                if (resolved.TryGetValue(handle, out var known)) return known;
                var reference = md.GetTypeReference(handle);
                Type? type = null;
                var name = md.GetString(reference.Name);
                switch (reference.ResolutionScope.Kind)
                {
                    case HandleKind.AssemblyReference:
                        var assemblyName = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                        var ns = md.GetString(reference.Namespace);
                        var fullName = ns.Length == 0 ? name : ns + "." + name;
                        // The class libraries' facades a mod names may not be loaded: their types
                        // are CoreLib's.
                        type = MonoMissingTypes.Loaded(assemblyName)?.GetType(fullName, throwOnError: false)
                            ?? (assemblyName is "mscorlib" or "netstandard" || assemblyName.StartsWith("System", StringComparison.Ordinal)
                                ? typeof(object).Assembly.GetType(fullName, throwOnError: false) : null);
                        break;
                    case HandleKind.TypeReference:
                        type = Resolve((TypeReferenceHandle)reference.ResolutionScope)?.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
                        break;
                }
                resolved[handle] = type;
                return type;
            }

            static string Primitive(Type type) => Type.GetTypeCode(type) switch
            {
                _ when type == typeof(void) => "void",
                _ when type == typeof(IntPtr) => "intptr",
                _ when type == typeof(UIntPtr) => "uintptr",
                _ when type == typeof(TypedReference) => "typedbyref",
                _ when type.IsEnum => "",
                TypeCode.Boolean => "bool",
                TypeCode.Char => "char",
                TypeCode.SByte => "sbyte",
                TypeCode.Byte => "byte",
                TypeCode.Int16 => "int16",
                TypeCode.UInt16 => "uint16",
                TypeCode.Int32 => "int",
                TypeCode.UInt32 => "uint",
                TypeCode.Int64 => "long",
                TypeCode.UInt64 => "ulong",
                TypeCode.Single => "single",
                TypeCode.Double => "double",
                TypeCode.String => "string",
                _ when type == typeof(object) => "object",
                _ => "",
            };

            public Sig GetPrimitiveType(PrimitiveTypeCode typeCode)
            {
                var type = typeCode switch
                {
                    PrimitiveTypeCode.Boolean => typeof(bool),
                    PrimitiveTypeCode.Char => typeof(char),
                    PrimitiveTypeCode.SByte => typeof(sbyte),
                    PrimitiveTypeCode.Byte => typeof(byte),
                    PrimitiveTypeCode.Int16 => typeof(short),
                    PrimitiveTypeCode.UInt16 => typeof(ushort),
                    PrimitiveTypeCode.Int32 => typeof(int),
                    PrimitiveTypeCode.UInt32 => typeof(uint),
                    PrimitiveTypeCode.Int64 => typeof(long),
                    PrimitiveTypeCode.UInt64 => typeof(ulong),
                    PrimitiveTypeCode.Single => typeof(float),
                    PrimitiveTypeCode.Double => typeof(double),
                    PrimitiveTypeCode.IntPtr => typeof(IntPtr),
                    PrimitiveTypeCode.UIntPtr => typeof(UIntPtr),
                    PrimitiveTypeCode.Object => typeof(object),
                    PrimitiveTypeCode.String => typeof(string),
                    PrimitiveTypeCode.TypedReference => typeof(TypedReference),
                    _ => typeof(void),
                };
                return new Sig(type, Primitive(type));
            }

            string Name(TypeReferenceHandle handle)
            {
                var reference = md.GetTypeReference(handle);
                var ns = md.GetString(reference.Namespace);
                var name = (ns.Length > 0 ? ns + "." : "") + md.GetString(reference.Name);
                return reference.ResolutionScope.Kind == HandleKind.TypeReference ? Name((TypeReferenceHandle)reference.ResolutionScope) + "/" + name : name;
            }

            string Name(TypeDefinitionHandle handle)
            {
                var definition = md.GetTypeDefinition(handle);
                var ns = md.GetString(definition.Namespace);
                var name = (ns.Length > 0 ? ns + "." : "") + md.GetString(definition.Name);
                var outer = definition.GetDeclaringType();
                return outer.IsNil ? name : Name(outer) + "/" + name;
            }

            public Sig GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var type = Resolve(handle);
                bool tagged = rawTypeKind == (byte)SignatureTypeKind.ValueType && type is { IsValueType: false } && SameInBothPlayers(type);
                // A class library type written as a reference is the primitive Mono reads it as.
                return new Sig(type, type != null && Primitive(type) is { Length: > 0 } primitive ? primitive : Name(handle), tagged);
            }

            /// The mod's own type: not loaded yet.
            public Sig GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => new(null, Name(handle));

            public Sig GetTypeFromSpecification(MetadataReader reader, Context genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
                reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

            public Sig GetSZArrayType(Sig elementType) => new(elementType.Type?.MakeArrayType(), elementType.Desc + "[]", Distinct: elementType.Distinct);

            public Sig GetArrayType(Sig elementType, ArrayShape shape) =>
                new(elementType.Type?.MakeArrayType(shape.Rank), elementType.Desc + "[" + new string(',', Math.Max(shape.Rank - 1, 0)) + "]", Distinct: elementType.Distinct);

            public Sig GetByReferenceType(Sig elementType) => new(elementType.Type?.MakeByRefType(), elementType.Desc + "&", Distinct: elementType.Distinct);

            public Sig GetPointerType(Sig elementType) => new(elementType.Type?.MakePointerType(), elementType.Desc + "*", Distinct: elementType.Distinct);

            public Sig GetPinnedType(Sig elementType) => elementType;

            public Sig GetModifiedType(Sig modifier, Sig unmodifiedType, bool isRequired) =>
                unmodifiedType with { Desc = unmodifiedType.Desc + (isRequired ? " modreq(" : " modopt(") + modifier.Desc + ")" };

            public Sig GetFunctionPointerType(MethodSignature<Sig> signature) => new(null, "*()");

            public Sig GetGenericInstantiation(Sig genericType, ImmutableArray<Sig> typeArguments)
            {
                var desc = genericType.Desc + "<" + string.Join(", ", typeArguments.Select(a => a.Desc)) + ">";
                bool distinct = typeArguments.Any(a => a.Tagged || a.Distinct);
                if (genericType.Type is not { IsGenericTypeDefinition: true } definition || typeArguments.Any(a => a.Type == null)
                    || definition.GetGenericArguments().Length != typeArguments.Length)
                    return new(null, desc, Distinct: distinct);
                return new(Type.MakeGenericSignatureType(definition, typeArguments.Select(a => a.Type!).ToArray()), desc, Distinct: distinct);
            }

            public Sig GetGenericTypeParameter(Context genericContext, int index) =>
                new(index < genericContext.TypeArguments.Length ? genericContext.TypeArguments[index] : null, "!" + index);

            public Sig GetGenericMethodParameter(Context genericContext, int index) => new(Type.MakeGenericMethodParameter(index), "!!" + index);
        }
    }
}
