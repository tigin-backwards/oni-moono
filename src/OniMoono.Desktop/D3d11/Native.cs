using System;
using System.Runtime.InteropServices;

namespace OniMoono.Desktop.D3d11
{
    /// The Windows SDK declaration a C# declaration stands for. tools/ComCheckGen reads every one
    /// (by reflection on this assembly) and writes a C++ file that MSVC compiles against the SDK's
    /// headers in the Windows build (scripts/com-check.sh); any difference fails that compile.
    /// - On a COM interface (a struct with `lpVtbl`, its methods in a nested `Vtbl` struct at
    ///   `FieldOffset(slot * 8)`), struct or enum: its native name (default: the C# name) and its
    ///   header. An interface also carries `static readonly Guid IID`.
    /// - On a static class: its constants, IIDs (static readonly Guid) and DllImports are all
    ///   declarations, each named by its own [Native] or else by its C# name; Header is the default
    ///   header of its members.
    /// - On a member (a struct field, a Vtbl method, a constant, an IID, a DllImport): the native
    ///   name when it differs from the C# one (a struct field may name a dotted path into nested
    ///   structs, `SampleDesc.Count`; a constant may name an expression, `sizeof(X)`).
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Field | AttributeTargets.Method)]
    internal sealed class NativeAttribute(string name = null) : Attribute
    {
        public string Name { get; } = name;

        /// The SDK header that declares it, as `#include <...>` names it ("d3d11.h").
        public string Header { get; init; }

        /// On an interface: a base interface it may be used as (a C++ base class in the header).
        public Type Base { get; init; }

        /// On a function pointer field of a [Native] class (a function looked up at run time): the
        /// DLL that exports it, checked against its export table.
        public string Library { get; init; }
    }

    /// The native type of a parameter (by index; `this` is 0 on a Vtbl method, -1 is the return)
    /// or, without an index, of a struct field, where the C# type differs from the one the check
    /// derives (HWND for IntPtr, DWORD for uint, LONG for int, an enum for uint). The check asserts
    /// that the native type is exactly this one and that the C# type takes its place: the same
    /// value type, or an integer of the same size and signedness, or an enum for an integer of the
    /// same size (never another enum, never float for an integer); pointers and references
    /// pointee by pointee by the same rules. An untyped pointer (void*) or IntPtr for a typed
    /// pointer or a handle needs Opaque, and the check lists every one.
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class AsAttribute : Attribute
    {
        public const int Return = -1;

        public int Index { get; }
        public string Type { get; }

        /// The C# type is an untyped pointer or a pointer-sized integer for this typed pointer or
        /// handle (HWND, HANDLE, a struct the code only passes as null or as a buffer).
        public bool Opaque { get; init; }

        public AsAttribute(int index, string type) => (Index, Type) = (index, type);
        public AsAttribute(string type) => (Index, Type) = (int.MinValue, type);
    }

    /// The pointer parameters (by index, as AsAttribute) whose pointee the native signature
    /// declares const (`const D3D11_BUFFER_DESC*`, `ID3D11Buffer *const *`): C# has no const, so
    /// the check adds it to the signature it derives.
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Method)]
    internal sealed class ConstAttribute(params int[] indexes) : Attribute
    {
        public int[] Indexes { get; } = indexes;
    }

    /// A constant in a [Native] class that is not an SDK declaration (why: the text), so the check
    /// leaves it out.
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class NotNativeAttribute(string why) : Attribute
    {
        public string Why { get; } = why;
    }

    /// IUnknown (unknwn.h): every COM interface's first three slots. Release goes through it for
    /// any interface (the check asserts each declared interface has IUnknown's Release slot).
    [Native(Header = "unknwn.h")]
    internal unsafe struct IUnknown
    {
        public static readonly Guid IID = new("00000000-0000-0000-c000-000000000046");
        public Vtbl* lpVtbl;

        [StructLayout(LayoutKind.Explicit)]
        public struct Vtbl
        {
            [FieldOffset(0 * 8), As(1, "REFIID")] public delegate* unmanaged<IUnknown*, Guid*, void**, int> QueryInterface;
            [FieldOffset(1 * 8), As(AsAttribute.Return, "ULONG")] public delegate* unmanaged<IUnknown*, uint> AddRef;
            [FieldOffset(2 * 8), As(AsAttribute.Return, "ULONG")] public delegate* unmanaged<IUnknown*, uint> Release;
        }
    }

    /// LUID (winnt.h), passed by value (EnumAdapterByLuid) and held in DXGI's adapter descriptions.
    [Native(Header = "winnt.h")]
    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        [As("DWORD")] public uint LowPart;
        [As("LONG")] public int HighPart;

        public static LUID Of(long value) => new() { LowPart = (uint)value, HighPart = (int)(value >> 32) };
        public readonly long Value => (long)HighPart << 32 | LowPart;
    }

    /// Reference counting on any interface pointer, through IUnknown's slots.
    internal static unsafe class Com
    {
        /// Releases the reference `p` holds, once: `p` is cleared first.
        public static void Release<T>(ref T* p) where T : unmanaged
        {
            var o = (IUnknown*)p;
            p = null;
            if (o != null) o->lpVtbl->Release(o);
        }

        public static int QueryInterface<T>(T* o, in Guid iid, void** result) where T : unmanaged
        {
            fixed (Guid* g = &iid) return ((IUnknown*)o)->lpVtbl->QueryInterface((IUnknown*)o, g, result);
        }
    }
}
