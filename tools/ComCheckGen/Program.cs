// Writes com_check.cpp: a C++ file that MSVC compiles (never runs) against the Windows SDK
// headers, asserting that every COM, struct, enum, constant, IID and DLL function declaration in
// OniMoono.Desktop (each carries OniMoono.Desktop.D3d11.NativeAttribute) is the header's:
// - an interface method's slot (offsetof into the header's C vtable struct) and its exact
//   function-pointer type (std::is_same_v), derived from the C# function pointer's types;
// - an interface's IID (__uuidof), its base classes (std::is_base_of) and IUnknown's Release slot;
// - a struct's size and each field's offset, type and size;
// - an enum's size and each value; each constant's value;
// - each DllImport's function type.
// The file is compiled twice: as C with CINTERFACE (the C vtables) and, with COM_CHECK_IIDS, as
// C++ classes (__uuidof, bases). scripts/com-check.sh runs both; a difference fails the compile.
//   ComCheckGen [OUT.cpp]   (stdout without OUT)
// The declarations are read by reflection on the built assembly, not from source: the slots,
// types and layouts checked are the ones the runtime uses (FieldOffset, the function pointer's
// signature as compiled, the runtime's struct layout).
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.Load("OniMoono.Desktop");
            var gen = new Generator(assembly);
            string text = gen.Emit();
            if (args.Length > 0) File.WriteAllText(args[0], text);
            else Console.Write(text);
            Console.Error.WriteLine($"ComCheckGen: {gen.Summary}");
            return 0;
        }
        catch (DeclarationException e)
        {
            Console.Error.WriteLine($"ComCheckGen: {e.Message}");
            return 1;
        }
    }
}

sealed class DeclarationException(string message) : Exception(message);

sealed class Generator(Assembly assembly)
{
    const int Return = -1;
    readonly StringBuilder c = new(), iids = new();
    readonly SortedSet<string> headers = new(StringComparer.Ordinal);
    readonly SortedSet<string> exports = new(StringComparer.Ordinal), opaque = new(StringComparer.Ordinal);
    readonly HashSet<MemberInfo> covered = [];
    readonly SortedSet<string> expressions = new(StringComparer.Ordinal), notNatives = new(StringComparer.Ordinal);
    readonly List<string> uncovered = [];
    int interfaces, methods, structs, fields, enums, values, constants, guids, functions, pointers;

    public string Summary => $"{interfaces} interfaces ({methods} methods), {structs} structs ({fields} fields), {enums} enums ({values} values), " +
        $"{constants} constants, {guids} IIDs, {functions} DLL functions, {pointers} function pointers, {exports.Count} exports, " +
        $"{opaque.Count} opaque [As], {expressions.Count} expressions, {notNatives.Count} [NotNative]";

    static DeclarationException Error(string message) => throw new DeclarationException(message);

    // ----- Reading the attributes (by name: they are internal to OniMoono.Desktop) -----

    static CustomAttributeData Attr(MemberInfo m, string name) => m.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == name);

    static IEnumerable<CustomAttributeData> Attrs(MemberInfo m, string name) => m.GetCustomAttributesData().Where(a => a.AttributeType.Name == name);

    static string Named(CustomAttributeData a, string property)
        => a?.NamedArguments.FirstOrDefault(n => n.MemberName == property).TypedValue.Value as string;

    /// The SDK name a declaration stands for: an identifier the headers declare (a dotted path
    /// of identifiers for a struct field), never an expression or a literal, so a name cannot
    /// carry a value of its own (the compile fails on a name the headers do not declare).
    static string NativeName(MemberInfo m)
    {
        var a = Attr(m, "NativeAttribute");
        string name = a?.ConstructorArguments.Count > 0 && a.ConstructorArguments[0].Value is string s ? s : m.Name;
        string pattern = m is FieldInfo { IsStatic: false } ? @"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$" : @"^[A-Za-z_]\w*$";
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, pattern))
            Error($"{Where(m)}: [Native(\"{name}\")] is not an identifier (an expression goes in [NativeExpression], on a constant)");
        return name;
    }

    static readonly HashSet<string> notIdentifiers = ["sizeof", "alignof", "offsetof", "unsigned", "signed", "int", "long", "short", "char", "const"];

    /// A constant's [NativeExpression]: the expression, which must name at least one SDK
    /// identifier besides operators and keywords (the compile fails when the headers do not
    /// declare it); listed at the top of com_check.cpp.
    string ExpressionOf(FieldInfo f)
    {
        var a = Attr(f, "NativeExpressionAttribute");
        if (a == null) return null;
        string e = (string)a.ConstructorArguments[0].Value;
        if (Attr(f, "NativeAttribute") is { ConstructorArguments: [{ Value: string }, ..] }) Error($"{Where(f)}: both a [Native] name and a [NativeExpression]");
        var ids = System.Text.RegularExpressions.Regex.Matches(e, @"\b[A-Za-z_]\w*\b").Select(x => x.Value).Where(x => !notIdentifiers.Contains(x)).ToList();
        if (ids.Count == 0) Error($"{Where(f)}: [NativeExpression(\"{e}\")] names no SDK identifier");
        expressions.Add($"{Where(f)}: {e}");
        return e;
    }

    static string HeaderOf(MemberInfo m) => Named(Attr(m, "NativeAttribute"), "Header");

    static Type BaseOf(Type t)
        => Attr(t, "NativeAttribute")?.NamedArguments.FirstOrDefault(n => n.MemberName == "Base").TypedValue.Value as Type;

    /// [As(index, type)] by index (int.MinValue: a field's [As(type)]), with its Opaque flag.
    static Dictionary<int, (string Type, bool Opaque)> AsTypes(MemberInfo m)
    {
        var r = new Dictionary<int, (string, bool)>();
        foreach (var a in Attrs(m, "AsAttribute"))
        {
            int index = a.ConstructorArguments.Count == 2 ? (int)a.ConstructorArguments[0].Value! : int.MinValue;
            string type = (string)a.ConstructorArguments[^1].Value!;
            bool isOpaque = a.NamedArguments.Any(n => n.MemberName == "Opaque" && (bool)n.TypedValue.Value!);
            if (!r.TryAdd(index, (type, isOpaque))) Error($"{Where(m)}: two [As] for {index}");
        }
        return r;
    }

    /// The assertion an [As] needs: the native type takes our C# type's place (abi_ok in the
    /// emitted file), and Opaque is there only where it is needed.
    void AbiAssert(string where, string native, string ours, bool isOpaque)
    {
        string o = isOpaque ? "true" : "false";
        Assert(c, $"abi_ok<{native}, {ours}, {o}>()", $"{where}: {native} in place of {ours}{(isOpaque ? " (opaque)" : "")}");
        if (isOpaque)
        {
            Assert(c, $"!abi_ok<{native}, {ours}, false>()", $"{where}: Opaque is not needed for {native}");
            opaque.Add($"{where}: {native} as {ours}");
        }
    }

    static HashSet<int> Consts(MemberInfo m)
    {
        var a = Attr(m, "ConstAttribute");
        if (a == null) return [];
        return [.. ((IReadOnlyCollection<CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!).Select(v => (int)v.Value!)];
    }

    static string Where(MemberInfo m) => m is Type t ? t.FullName! : $"{m.DeclaringType!.FullName}.{m.Name}";

    static bool IsNative(Type t) => Attr(t, "NativeAttribute") != null;

    static bool IsInterface(Type t) => IsNative(t) && t.IsValueType && !t.IsEnum && t.GetField("lpVtbl") != null;

    // ----- C types derived from C# types -----

    /// The C type a C# type stands for: fixed by the C# type, so a wrong C# type gives a wrong C
    /// type and fails the is_same assertion. An int return is HRESULT (no declared method returns
    /// INT; if one did, the assertion would fail).
    string CType(Type t, string where, bool isReturn = false)
    {
        if (t.IsPointer) return CType(t.GetElementType()!, where) + "*";
        if (t == typeof(void)) return "void";
        if (t.IsEnum || (t.IsValueType && !t.IsPrimitive && t != typeof(Guid) && t != typeof(IntPtr) && t != typeof(UIntPtr)))
        {
            if (!IsNative(t)) Error($"{where}: {t.FullName} has no [Native]");
            return NativeName(t);
        }
        if (t == typeof(int)) return isReturn ? "HRESULT" : "INT";
        if (t == typeof(uint)) return "UINT";
        if (t == typeof(long)) return "INT64";
        if (t == typeof(ulong)) return "UINT64";
        if (t == typeof(short)) return "INT16";
        if (t == typeof(ushort)) return "UINT16";
        if (t == typeof(byte)) return "UINT8";
        if (t == typeof(sbyte)) return "INT8";
        if (t == typeof(char)) return "WCHAR";
        if (t == typeof(float)) return "FLOAT";
        if (t == typeof(double)) return "DOUBLE";
        if (t == typeof(IntPtr)) return "INT_PTR";
        if (t == typeof(UIntPtr)) return "SIZE_T";
        if (t == typeof(Guid)) return "GUID";
        throw Error($"{where}: no C type for {t}");
    }

    /// Size in bytes as the runtime lays the type out (x64).
    static int SizeOf(Type t)
    {
        if (t.IsPointer || t.IsFunctionPointer || t == typeof(IntPtr) || t == typeof(UIntPtr)) return IntPtr.Size;
        if (t.IsEnum) return SizeOf(Enum.GetUnderlyingType(t));
        return (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(t).Invoke(null, null)!;
    }

    static string Literal(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    void Assert(StringBuilder to, string condition, string message) => to.Append("static_assert(").Append(condition).Append(", ").Append(Literal(message)).Append(");\n");

    void Header(MemberInfo m, string fallback = null)
    {
        string h = HeaderOf(m) ?? fallback;
        if (h == null) Error($"{Where(m)}: no Header");
        headers.Add(h!);
    }

    // ----- Function types -----

    /// The function-pointer type the C# signature gives, with [Const] and [As] applied, plus the
    /// ABI assertions each [As] needs.
    string FunctionType(MemberInfo m, Type returnType, Type[] parameters, string callingConvention, List<(string, string, bool)> abi)
    {
        var asTypes = AsTypes(m);
        var consts = Consts(m);
        string where = Where(m);
        string Param(int i, Type t)
        {
            string derived = CType(t, where, i == Return);
            if (consts.Contains(i))
            {
                if (!t.IsPointer) Error($"{where}: [Const({i})] on a parameter that is not a pointer");
                if (asTypes.ContainsKey(i)) Error($"{where}: both [Const] and [As] on {i}");
                return CType(t.GetElementType()!, where) + " const*";
            }
            if (asTypes.TryGetValue(i, out var native))
            {
                abi.Add((native.Type, derived, native.Opaque));
                return native.Type;
            }
            return derived;
        }
        foreach (int i in consts.Concat(asTypes.Keys))
            if (i < Return || i >= parameters.Length) Error($"{where}: [Const]/[As] index {i} out of range");
        var p = parameters.Select((t, i) => Param(i, t));
        return $"{Param(Return, returnType)} ({callingConvention}*)({string.Join(", ", p)})";
    }

    static void Blittable(Type t, string where)
    {
        if (t.IsByRef || t == typeof(bool) || t == typeof(string) || (!t.IsValueType && !t.IsPointer && t != typeof(void)))
            Error($"{where}: {t} is not a blittable native type");
    }

    /// A DllImport's parameters are marshalled: a char by value is ANSI by default
    /// (learn.microsoft.com/dotnet/standard/native-interop/charset), so none is allowed there (a
    /// char* is a pointer, passed as it is).
    static void NoChar(Type t, string where)
    {
        if (t == typeof(char)) Error($"{where}: char in a DllImport marshals as ANSI by default; use ushort, or a char*");
    }

    // ----- Declarations -----

    public string Emit()
    {
        var types = assembly.GetTypes().Where(IsNative).OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        foreach (var t in types)
        {
            if (IsInterface(t)) EmitInterface(t);
            else if (t.IsEnum) EmitEnum(t);
            else if (t.IsValueType) EmitStruct(t);
            else if (t.IsClass && t.IsAbstract && t.IsSealed) EmitClass(t);
            else Error($"{t.FullName}: [Native] on something that is not an interface, struct, enum or static class");
            covered.Add(t);
        }
        // A DllImport with its own [Native] in a class that is not one (its header on it).
        foreach (var t in assembly.GetTypes().Where(t => !IsNative(t)).OrderBy(t => t.FullName, StringComparer.Ordinal))
            foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (Attr(m, "NativeAttribute") != null && m.GetCustomAttribute<DllImportAttribute>() is { } import) EmitDllImport(m, import, null);
        // Every member that carries a declaration attribute must have been emitted above: none
        // may sit in a type the check does not read.
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string[] marks = ["NativeAttribute", "AsAttribute", "ConstAttribute", "NativeExpressionAttribute"];
        foreach (var t in assembly.GetTypes())
            foreach (var m in t.GetMembers(all).Append(t))
                if (marks.Any(k => Attr(m, k) != null) && !covered.Contains(m))
                    Error($"{Where(m)}: a declaration the check does not read (its type has no [Native], or it is not a constant, IID, DllImport or function pointer)");
        Sweep();
        var o = new StringBuilder();
        o.Append($"// Generated by tools/ComCheckGen from OniMoono.Desktop's [Native] declarations: do not edit.\n// {Summary}\n");
        o.Append("// Each DLL entry point, checked by com_check.cmd against the DLL's export table (dumpbin /exports):\n");
        foreach (var e in exports) o.Append($"// export: {e}\n");
        o.Append("// Where [As(Opaque = true)] lets an untyped pointer or handle stand for a typed one:\n");
        foreach (var e in opaque) o.Append($"//   {e}\n");
        o.Append("// Constants checked as an expression ([NativeExpression]), each naming SDK identifiers:\n");
        foreach (var e in expressions) o.Append($"//   {e}\n");
        o.Append("// [NotNative]: what the check leaves out, and why:\n");
        foreach (var e in notNatives) o.Append($"//   {e}\n");

        o.Append("""
            // Compile without COM_CHECK_IIDS for the C vtables (CINTERFACE) and with it for the C++
            // classes (__uuidof, bases). Compiled, never run: a failed static_assert is a difference.
            #ifndef COM_CHECK_IIDS
            #define CINTERFACE
            #define COBJMACROS
            #endif
            #define D3D11_NO_HELPERS
            #define WIN32_LEAN_AND_MEAN
            #define NOMINMAX
            #include <windows.h>

            """);
        // d3d10.h after d3d11.h, which includes d3d10_1.h: d3d10_1.h stops with #error when d3d10.h
        // came first.
        foreach (var h in headers.OrderBy(h => h == "d3d10.h")) o.Append($"#include <{h}>\n");
        o.Append("""
            #include <stddef.h>
            #include <type_traits>
            #include <utility>

            // A native type in place of the one derived from C# ([As]) must take its place exactly
            // but for what C# cannot say:
            // - values: the same type; or an integer for an integer of the same size and signedness;
            //   or an enum for an integer of the same size, either way (never an enum for another
            //   enum, never a float for an integer);
            // - pointers and references: pointee by pointee, the same rules, const aside;
            // - an untyped pointer (void*) or a pointer-sized integer for a typed pointer or handle
            //   only when the [As] says Opaque (listed at the top of this file).
            template <class N, class O> constexpr bool value_ok()
            {
                if constexpr (std::is_same_v<N, O>) return true;
                else if constexpr (std::is_integral_v<N> && std::is_integral_v<O>) return sizeof(N) == sizeof(O) && std::is_signed_v<N> == std::is_signed_v<O>;
                else if constexpr ((std::is_enum_v<N> && std::is_integral_v<O>) || (std::is_integral_v<N> && std::is_enum_v<O>)) return sizeof(N) == sizeof(O);
                else return false;
            }
            // What Opaque may stand for: an innermost pointee the code never looks into (void, or a
            // struct such as HWND__ or SECURITY_ATTRIBUTES), never a pointer, so that Opaque keeps
            // every level of indirection: void* for a T* (T a struct), IntPtr for a T* (a handle),
            // IntPtr* for a HANDLE*; never void* for a T**, nor IntPtr for a HANDLE*.
            template <class T0> constexpr bool opaque_target()
            {
                using T = std::remove_cv_t<T0>;
                return std::is_void_v<T> || std::is_class_v<T> || std::is_union_v<T>;
            }
            template <class N0, class O0, bool Opaque> constexpr bool pointee_ok()
            {
                using N = std::remove_cv_t<N0>;
                using O = std::remove_cv_t<O0>;
                if constexpr (std::is_void_v<O>) return std::is_void_v<N> || (Opaque && opaque_target<N>());
                else if constexpr (std::is_pointer_v<N> && std::is_pointer_v<O>) return pointee_ok<std::remove_pointer_t<N>, std::remove_pointer_t<O>, Opaque>();
                else if constexpr (std::is_pointer_v<N> && std::is_integral_v<O>)
                    return Opaque && sizeof(N) == sizeof(O) && opaque_target<std::remove_pointer_t<N>>();
                else if constexpr (std::is_pointer_v<N> || std::is_pointer_v<O>) return false;
                else return value_ok<N, O>();
            }
            template <class N0, class O0, bool Opaque> constexpr bool abi_ok()
            {
                using N = std::remove_cv_t<N0>;
                using O = std::remove_cv_t<O0>;
                if constexpr (std::is_reference_v<N>) return std::is_pointer_v<O> && pointee_ok<std::remove_reference_t<N>, std::remove_pointer_t<O>, Opaque>();
                else if constexpr (std::is_pointer_v<N> && std::is_pointer_v<O>) return pointee_ok<std::remove_pointer_t<N>, std::remove_pointer_t<O>, Opaque>();
                else if constexpr (std::is_pointer_v<N> && std::is_integral_v<O>)
                    return Opaque && sizeof(N) == sizeof(O) && opaque_target<std::remove_pointer_t<N>>();
                else if constexpr (std::is_pointer_v<N> || std::is_pointer_v<O>) return false;
                else return value_ok<N, O>();
            }

            #ifdef COM_CHECK_IIDS

            """);
        o.Append(iids);
        o.Append("\n#else\n\n");
        o.Append(c);
        o.Append("\n#endif\n");
        return o.ToString();
    }

    void EmitGuid(MemberInfo m, string nativeInterface, Guid g)
    {
        var b = g.ToByteArray();
        uint d1 = BitConverter.ToUInt32(b, 0);
        ushort d2 = BitConverter.ToUInt16(b, 4), d3 = BitConverter.ToUInt16(b, 6);
        var u = $"__uuidof({nativeInterface})";
        var cond = new StringBuilder($"{u}.Data1 == 0x{d1:x8}u && {u}.Data2 == 0x{d2:x4} && {u}.Data3 == 0x{d3:x4}");
        for (int i = 0; i < 8; i++) cond.Append($" && {u}.Data4[{i}] == 0x{b[8 + i]:x2}");
        Assert(iids, cond.ToString(), $"{Where(m)}: IID of {nativeInterface} is {g}");
        guids++;
    }

    void EmitInterface(Type t)
    {
        string n = NativeName(t);
        Header(t);
        interfaces++;
        var lp = t.GetField("lpVtbl")!;
        if (t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length != 1 || Marshal.OffsetOf(t, "lpVtbl") != 0 || SizeOf(t) != IntPtr.Size)
            Error($"{t.FullName}: an interface struct holds lpVtbl only");
        var iid = t.GetField("IID", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (iid == null || iid.FieldType != typeof(Guid) || !iid.IsInitOnly) Error($"{t.FullName}: no static readonly Guid IID");
        EmitGuid(t, n, (Guid)iid!.GetValue(null)!);
        covered.Add(iid);
        covered.Add(lp);
        foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (f != iid && !Generated(f)) Error($"{Where(f)}: an interface struct has no static fields but IID");
        if (BaseOf(t) is { } b)
        {
            if (!IsInterface(b)) Error($"{t.FullName}: Base {b} is not an interface");
            Assert(iids, $"std::is_base_of_v<{NativeName(b)}, {n}>", $"{t.FullName}: derives from {NativeName(b)}");
        }
        else if (n != "IUnknown") Error($"{t.FullName}: no Base");
        // The C struct is the vtable pointer alone, and IUnknown's Release is slot 2 (Com.Release).
        Assert(c, $"sizeof({n}) == sizeof(void*) && offsetof({n}, lpVtbl) == 0", $"{t.FullName}: lpVtbl only");
        Assert(c, $"offsetof({n}Vtbl, Release) == 2 * sizeof(void*) && std::is_same_v<decltype({n}Vtbl::Release), ULONG (STDMETHODCALLTYPE*)({n}*)>",
            $"{t.FullName}: IUnknown's Release at slot 2");
        var vtbl = t.GetNestedType("Vtbl", BindingFlags.Public | BindingFlags.NonPublic);
        if (vtbl == null)
        {
            if (lp.FieldType != typeof(void*)) Error($"{t.FullName}: lpVtbl of a type with no Vtbl is void*");
            return;
        }
        if (lp.FieldType != vtbl.MakePointerType()) Error($"{t.FullName}: lpVtbl is Vtbl*");
        if (!vtbl.IsExplicitLayout) Error($"{vtbl.FullName}: not LayoutKind.Explicit");
        foreach (var f in vtbl.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(f => Marshal.OffsetOf(vtbl, f.Name)))
        {
            string where = Where(f);
            if (!f.FieldType.IsFunctionPointer || !f.FieldType.IsUnmanagedFunctionPointer) Error($"{where}: not a delegate* unmanaged");
            var conventions = f.GetModifiedFieldType().GetFunctionPointerCallingConventions();
            if (conventions.Any(cc => cc != typeof(CallConvStdcall))) Error($"{where}: calling convention {string.Join(", ", conventions.Select(cc => cc.Name))}");
            int offset = (int)Marshal.OffsetOf(vtbl, f.Name);
            if (offset % IntPtr.Size != 0) Error($"{where}: offset {offset} is not a slot");
            var ps = f.FieldType.GetFunctionPointerParameterTypes();
            var ret = f.FieldType.GetFunctionPointerReturnType();
            foreach (var p in ps.Append(ret)) Blittable(p, where);
            if (ps.Length == 0 || ps[0] != t.MakePointerType()) Error($"{where}: the first parameter is {t.Name}*");
            string m = NativeName(f);
            var abi = new List<(string, string, bool)>();
            string type = FunctionType(f, ret, ps, "STDMETHODCALLTYPE", abi);
            Assert(c, $"offsetof({n}Vtbl, {m}) == {offset}", $"{where}: slot {offset / IntPtr.Size}");
            Assert(c, $"std::is_same_v<decltype({n}Vtbl::{m}), {type}>", $"{where}: signature {type}");
            foreach (var (native, ours, isOpaque) in abi) AbiAssert(where, native, ours, isOpaque);
            covered.Add(f);
            methods++;
        }
    }

    void EmitEnum(Type t)
    {
        string n = NativeName(t);
        Header(t);
        enums++;
        Assert(c, $"sizeof({n}) == {SizeOf(t)}", $"{t.FullName}: size {SizeOf(t)}");
        foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public))
        {
            long v = Convert.ToInt64(f.GetRawConstantValue());
            Assert(c, $"(long long)({NativeName(f)}) == {v}LL", $"{Where(f)} = {v}");
            Assert(c, $"std::is_same_v<decltype({NativeName(f)}), {n}>", $"{Where(f)}: an enumerator of {n}");
            covered.Add(f);
            values++;
        }
    }

    /// A fixed buffer (`fixed char X[32]`) or an [InlineArray] field: ELEMENT[N].
    (Type Element, int Count)? ArrayOf(FieldInfo f)
    {
        var fixedBuffer = f.GetCustomAttribute<FixedBufferAttribute>();
        if (fixedBuffer != null) return (fixedBuffer.ElementType, fixedBuffer.Length);
        var inline = f.FieldType.GetCustomAttribute<InlineArrayAttribute>();
        if (inline != null) return (f.FieldType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single().FieldType, inline.Length);
        return null;
    }

    void EmitStruct(Type t)
    {
        string n = NativeName(t);
        Header(t);
        structs++;
        if (t.IsAutoLayout) Error($"{t.FullName}: auto layout");
        int size = SizeOf(t);
        if (Marshal.SizeOf(t) != size) Error($"{t.FullName}: not blittable (marshalled size {Marshal.SizeOf(t)}, runtime {size})");
        Assert(c, $"sizeof({n}) == {size}", $"{t.FullName}: size {size}");
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            string where = Where(f), path = NativeName(f);
            int offset = (int)Marshal.OffsetOf(t, f.Name), fieldSize = SizeOf(f.FieldType);
            string member = $"decltype(std::declval<{n}&>().{path})";
            string type = ArrayOf(f) is { } a ? $"{CType(a.Element, where)}[{a.Count}]" : CType(f.FieldType, where);
            if (AsTypes(f).TryGetValue(int.MinValue, out var native))
            {
                if (ArrayOf(f) != null) Error($"{where}: [As] on an array field");
                AbiAssert(where, native.Type, type, native.Opaque);
                type = native.Type;
            }
            else if (AsTypes(f).Count > 0) Error($"{where}: [As(index, ...)] on a field");
            Assert(c, $"offsetof({n}, {path}) == {offset}", $"{where}: offset {offset}");
            Assert(c, $"std::is_same_v<{member}, {type}>", $"{where}: type {type}");
            Assert(c, $"sizeof({member}) == {fieldSize}", $"{where}: size {fieldSize}");
            covered.Add(f);
            fields++;
        }
        foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            if (!Generated(f)) Error($"{Where(f)}: a static field in a [Native] struct");
        foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.GetCustomAttribute<DllImportAttribute>() != null || Attr(m, "LibraryImportAttribute") != null)
                Error($"{Where(m)}: a DLL function in a struct (put it in a [Native] static class)");
        }
    }

    /// [NotNative("why")]: left out of the check, listed with its reason.
    bool NotNative(MemberInfo m)
    {
        var a = Attr(m, "NotNativeAttribute");
        if (a == null) return false;
        covered.Add(m);
        notNatives.Add($"{Where(m)}: {a.ConstructorArguments[0].Value}");
        return true;
    }

    /// Everything native in the assembly, whether or not it says [Native]: every DllImport or
    /// LibraryImport, every unmanaged function-pointer field, every [UnmanagedFunctionPointer]
    /// delegate, every struct with a vtable pointer, and the structs those [NotNative] ones take,
    /// must be [Native] (and emitted above) or [NotNative("why")]; else the generator fails,
    /// listing them all.
    void Sweep()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var structsUsed = new List<(Type Type, string Where)>();
        void Uses(Type t, string where)
        {
            while (t.IsPointer || t.IsByRef || t.IsArray) t = t.GetElementType();
            if (t != typeof(void) && t.IsValueType && !t.IsPrimitive && !t.IsEnum && t != typeof(IntPtr) && t != typeof(UIntPtr) && t != typeof(Guid)) structsUsed.Add((t, where));
        }
        void Signature(MethodInfo m, string where)
        {
            foreach (var p in m.GetParameters()) Uses(p.ParameterType, where);
            Uses(m.ReturnType, where);
        }
        void FunctionPointer(Type t, string where)
        {
            foreach (var p in t.GetFunctionPointerParameterTypes()) Uses(p, where);
            Uses(t.GetFunctionPointerReturnType(), where);
        }
        foreach (var t in assembly.GetTypes())
        {
            if (Generated(t) && t.DeclaringType != null && t.Name.StartsWith('<')) continue;
            if (typeof(Delegate).IsAssignableFrom(t) && t.GetCustomAttribute<UnmanagedFunctionPointerAttribute>() != null && !covered.Contains(t) && !NotNative(t))
                uncovered.Add($"{t.FullName}: an [UnmanagedFunctionPointer] delegate");
            else if (typeof(Delegate).IsAssignableFrom(t) && covered.Contains(t) && t.GetMethod("Invoke") is { } invoke)
                Signature(invoke, t.FullName);
            if (t.IsValueType && t.GetField("lpVtbl", all) != null && !covered.Contains(t) && !NotNative(t))
                uncovered.Add($"{t.FullName}: a struct with a vtable pointer");
            foreach (var m in t.GetMethods(all))
            {
                bool native = (m.Attributes & MethodAttributes.PinvokeImpl) != 0 || Attr(m, "LibraryImportAttribute") != null;
                if (!native || covered.Contains(m) && Attr(m, "NotNativeAttribute") == null) continue;
                if (covered.Contains(m) || NotNative(m)) Signature(m, Where(m));
                else uncovered.Add($"{Where(m)}: a DLL function");
            }
            foreach (var f in t.GetFields(all))
            {
                if (!f.FieldType.IsFunctionPointer || !f.FieldType.IsUnmanagedFunctionPointer) continue;
                if (covered.Contains(f) && Attr(f, "NotNativeAttribute") == null) continue;
                if (covered.Contains(f) || NotNative(f)) FunctionPointer(f.FieldType, Where(f));
                else uncovered.Add($"{Where(f)}: an unmanaged function-pointer field");
            }
        }
        foreach (var (type, where) in structsUsed)
            if (!IsNative(type) && !covered.Contains(type) && !NotNative(type))
                uncovered.Add($"{type.FullName}: a struct {where} takes");
        if (uncovered.Count > 0)
            Error($"native declarations neither [Native] nor [NotNative(\"why\")]:\n  {string.Join("\n  ", uncovered.Distinct())}");
    }

    static bool Generated(MemberInfo m) => m.Name.StartsWith('<') || m.GetCustomAttribute<CompilerGeneratedAttribute>() != null;

    void EmitClass(Type t)
    {
        string classHeader = HeaderOf(t);
        foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (Generated(f)) continue;
            covered.Add(f);
            string where = Where(f);
            if (NotNative(f)) continue;
            if (f.IsLiteral)
            {
                Header(f, classHeader);
                var value = f.GetRawConstantValue();
                if (value is not (int or uint or long or ulong or short or ushort or byte or sbyte)) Error($"{where}: a constant that is not an integer");
                string v = value is ulong u ? $"{u}ULL" : $"{Convert.ToInt64(value)}LL";
                string native = ExpressionOf(f) ?? NativeName(f);
                Assert(c, $"(long long)({native}) == (long long)({v})", $"{where} = {value}");
                constants++;
            }
            else if (f.FieldType == typeof(Guid) && f.IsInitOnly && f.IsStatic)
            {
                if (Attr(f, "NativeAttribute") == null) Error($"{where}: an IID with no [Native(\"Interface\")]");
                Header(f, classHeader);
                EmitGuid(f, NativeName(f), (Guid)f.GetValue(null)!);
            }
            else if (f.FieldType.IsFunctionPointer && f.IsStatic)
            {
                // A function the code looks up (GetProcAddress): its type against the header's.
                if (!f.FieldType.IsUnmanagedFunctionPointer) Error($"{where}: not a delegate* unmanaged");
                string library = Named(Attr(f, "NativeAttribute"), "Library");
                if (library == null) Error($"{where}: a function pointer names its DLL ([Native(Library = ...)])");
                Header(f, classHeader);
                var ps = f.FieldType.GetFunctionPointerParameterTypes();
                var ret = f.FieldType.GetFunctionPointerReturnType();
                foreach (var p in ps.Append(ret)) Blittable(p, where);
                var abi = new List<(string, string, bool)>();
                string type = FunctionType(f, ret, ps, "WINAPI", abi);
                string entry = NativeName(f);
                Assert(c, $"std::is_same_v<decltype(&{entry}), {type}>", $"{where}: {library}!{entry} is {type}");
                foreach (var (native, ours, isOpaque) in abi) AbiAssert(where, native, ours, isOpaque);
                exports.Add($"{library} {entry}");
                pointers++;
            }
            else Error($"{where}: a field of a [Native] class that is not a constant, an IID, a function pointer or [NotNative]");
        }
        foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            string where = Where(m);
            if (Attr(m, "LibraryImportAttribute") != null) Error($"{where}: LibraryImport is not read by the check; use DllImport");
            var import = m.GetCustomAttribute<DllImportAttribute>();
            if (import == null)
            {
                if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) Error($"{where}: a P/Invoke the check cannot read");
                continue;
            }
            if (NotNative(m)) continue;
            EmitDllImport(m, import, classHeader);
        }
    }

    /// A DllImport: its function type against the header's, and its DLL's export.
    void EmitDllImport(MethodInfo m, DllImportAttribute import, string classHeader)
    {
        {
            string where = Where(m);
            covered.Add(m);
            Header(m, classHeader);
            if (!import.PreserveSig) Error($"{where}: PreserveSig off");
            var ps = m.GetParameters().Select(p => p.ParameterType).ToArray();
            foreach (var p in ps.Append(m.ReturnType))
            {
                Blittable(p, where);
                NoChar(p, where);
            }
            var abi = new List<(string, string, bool)>();
            string type = FunctionType(m, m.ReturnType, ps, "WINAPI", abi);
            string entry = import.EntryPoint ?? m.Name;
            string library = import.Value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? import.Value : import.Value + ".dll";
            Assert(c, $"std::is_same_v<decltype(&{entry}), {type}>", $"{where}: {library}!{entry} is {type}");
            foreach (var (native, ours, isOpaque) in abi) AbiAssert(where, native, ours, isOpaque);
            exports.Add($"{library.ToLowerInvariant()} {entry}");
            functions++;
        }
    }
}
