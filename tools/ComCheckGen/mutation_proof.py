"""The COM check's mutation proof (docs/research/d3d11-plan.md, M2 part A): in a scratch copy of
the branch, the unchanged copy first and last (each must pass), and between them one deliberate
mismatch per run, each of which must fail scripts/com-check.sh. Files are restored by writing their
text back (a new mtime, so the next build sees the change). Prints one verdict line per run.
  python3 -I tools/ComCheckGen/mutation_proof.py COPY [PREFIX]   (COPY: `git archive HEAD` extracted
  elsewhere; PREFIX: only the mutations whose label starts with it, e.g. M4)"""

import pathlib
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
d = "src/OniMoono.Desktop/D3d11/"
g = "src/OniMoono.Desktop/Gl/Dxgi.cs"
MUTATIONS = [
    ("slot", d + "D3d11.cs", '[FieldOffset(14 * 8), As(4, "UINT")]', '[FieldOffset(15 * 8), As(4, "UINT")]'),
    ("signature", d + "D3d11.cs", "uint, uint, int, void> DrawIndexed", "uint, uint, uint, void> DrawIndexed"),
    ("struct offset", d + "D3d11.cs", "[FieldOffset(12)] public D3D11_TEX2D_DSV Texture2D;", "[FieldOffset(8)] public D3D11_TEX2D_DSV Texture2D;"),
    ("guid", d + "D3d11.cs", 'new("db6f6ddb-ac77-4e88-8253-819df9bbf140")', 'new("db6f6ddb-ac77-4e88-8253-819df9bbf141")'),
    ("1 field [As] over a float", d + "D3d11.cs", '[As("LONG")] public int left;', '[As("LONG")] public float left;'),
    ("1 field [As] signedness", d + "D3d11.cs", '[As("LONG")] public int top;', '[As("LONG")] public uint top;'),
    (
        "2 pointer [As] pointee",
        d + "D3d11.cs",
        "DXGI_FORMAT, D3D11_FORMAT_SUPPORT*, int> CheckFormatSupport",
        "DXGI_FORMAT, D3D11_TEXTURE2D_DESC*, int> CheckFormatSupport",
    ),
    ("2 void* without Opaque", d + "DxgiInterfaces.cs", 'As(1, "const SECURITY_ATTRIBUTES*", Opaque = true)', 'As(1, "const SECURITY_ATTRIBUTES*")'),
    ("2 Opaque not needed", d + "Native.cs", '[FieldOffset(0 * 8), As(1, "REFIID")]', '[FieldOffset(0 * 8), As(1, "REFIID", Opaque = true)]'),
    (
        "2 enum for another enum",
        d + "D3d11.cs",
        "[FieldOffset(24 * 8)] public delegate* unmanaged<ID3D11DeviceContext*, D3D11_PRIMITIVE_TOPOLOGY, void> IASetPrimitiveTopology;",
        '[FieldOffset(24 * 8), As(1, "D3D11_PRIMITIVE_TOPOLOGY")] public delegate* unmanaged<ID3D11DeviceContext*, D3D11_MAP, void> IASetPrimitiveTopology;',
    ),
    ("3 enumerator of another enum", d + "D3d11.cs", "        D3D11_USAGE_STAGING = 3,\n", "        D3D11_USAGE_STAGING = 3,\n        D3D11_MAP_READ = 1,\n"),
    (
        "4 char in a DllImport",
        d + "D3d11.cs",
        "public const uint D3D11_SDK_VERSION = 7;",
        'public const uint D3D11_SDK_VERSION = 7;\n        [DllImport("user32.dll"), Native(Header = "winuser.h"), As(AsAttribute.Return, "SHORT")] public static extern short VkKeyScanW(char ch);',
    ),
    (
        "5 static field that is not a declaration",
        d + "D3d11.cs",
        "public const uint D3D11_SDK_VERSION = 7;",
        "public const uint D3D11_SDK_VERSION = 7;\n        public static readonly uint D3D11_NOT_IN_ANY_HEADER = 5;",
    ),
    (
        "5 function pointer of the wrong type",
        d + "D3d11.cs",
        "public const uint D3D11_SDK_VERSION = 7;",
        'public const uint D3D11_SDK_VERSION = 7;\n        [Native("D3D11CreateDevice", Library = "d3d11.dll")] public static delegate* unmanaged<int, int> CreateDeviceByPointer;',
    ),
    (
        "5 [Native] member of a type without [Native]",
        d + "D3d11Debug.cs",
        "    [Native(Header = \"d3d11sdklayers.h\", Base = typeof(IUnknown))]\n    internal unsafe struct ID3D11Debug",
        "    internal static class NotChecked\n    {\n        [DllImport(\"user32.dll\"), Native(Header = \"winuser.h\")] public static extern long GetSystemMetrics(double index);\n    }\n\n"
        "    [Native(Header = \"d3d11sdklayers.h\", Base = typeof(IUnknown))]\n    internal unsafe struct ID3D11Debug",
    ),
    (
        "5 LibraryImport",
        d + "D3d11Debug.cs",
        "    [Native(Header = \"d3d11sdklayers.h\", Base = typeof(IUnknown))]\n    internal unsafe struct ID3D11Debug",
        "    [Native(Header = \"winuser.h\")]\n    internal static partial class Lib\n    {\n        [LibraryImport(\"user32.dll\")] public static partial int GetSystemMetrics(int index);\n    }\n\n"
        "    [Native(Header = \"d3d11sdklayers.h\", Base = typeof(IUnknown))]\n    internal unsafe struct ID3D11Debug",
    ),
    ("6 DLL that does not export it", d + "DxgiInterfaces.cs", '[DllImport("dxgi.dll"), As(1, "REFIID")]', '[DllImport("d3d11.dll"), As(1, "REFIID")]'),
    (
        "2 enumerator named by an expression",
        d + "D3d11.cs",
        "        D3D11_USAGE_STAGING = 3,\n",
        '        D3D11_USAGE_STAGING = 3,\n        [Native("(D3D11_USAGE)7")] D3D11_USAGE_BOGUS = 7,\n',
    ),
    (
        "2 constant named by a literal",
        g,
        '[Native("DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING")] public const uint SwapChainFlagAllowTearing = 0x800;',
        '[Native("0x400")] public const uint SwapChainFlagAllowTearing = 0x400;',
    ),
    (
        "2 expression naming no SDK identifier",
        g,
        '[NativeExpression("sizeof(DISPLAYCONFIG_PATH_INFO)"), Native(Header = "wingdi.h")]',
        '[NativeExpression("sizeof(int) * 18"), Native(Header = "wingdi.h")]',
    ),
    (
        "3 interface without [Native], slot moved",
        [
            (
                d + "D3d11Debug.cs",
                '    [Native(Header = "d3d11sdklayers.h", Base = typeof(IUnknown))]\n    internal unsafe struct ID3D11Debug',
                "    internal unsafe struct ID3D11Debug",
            ),
            (
                d + "D3d11Debug.cs",
                "[FieldOffset(10 * 8)] public delegate* unmanaged<ID3D11Debug*",
                "[FieldOffset(11 * 8)] public delegate* unmanaged<ID3D11Debug*",
            ),
        ],
    ),
    (
        "3 DllImport in a class the check does not read",
        d + "D3d11Context.cs",
        "        public static bool Validation;",
        '        public static bool Validation;\n\n        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern long GetSystemMetrics(double index);',
    ),
    (
        "3 [UnmanagedFunctionPointer] delegate without [Native] or [NotNative]",
        "src/OniMoono.Desktop/Gl/DxgiTarget.cs",
        '        [NotNative("a WGL_NV_DX_interop function from wglGetProcAddress; wglext.h is not in the Windows SDK '
        '(registry.khronos.org/OpenGL/extensions/NV/WGL_NV_DX_interop.txt)")]\n'
        "        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] delegate IntPtr OpenDevice",
        "        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] delegate IntPtr OpenDevice",
    ),
    (
        "3 function-pointer field without [Native] or [NotNative]",
        "src/OniMoono.Desktop/Gl/Win32GlWindow.cs",
        "        [NotNative(\"WGL_EXT_swap_control's wglSwapIntervalEXT from wglGetProcAddress; wglext.h is not in the Windows SDK "
        '(registry.khronos.org/OpenGL/extensions/EXT/WGL_EXT_swap_control.txt)")]\n        delegate* unmanaged<int, int> swapIntervalExt;',
        "        delegate* unmanaged<int, int> swapIntervalExt;",
    ),
    (
        "4 Opaque IntPtr by value for a HANDLE*",
        [
            (
                d + "DxgiInterfaces.cs",
                "public delegate* unmanaged<IDXGIResource1*, void*, uint, char*, IntPtr*, int> CreateSharedHandle;",
                "public delegate* unmanaged<IDXGIResource1*, void*, uint, char*, IntPtr, int> CreateSharedHandle;",
            ),
            (g, "(IDXGIResource1*)resource1, null, access, null, handle);", "(IDXGIResource1*)resource1, null, access, null, (IntPtr)handle);"),
        ],
    ),
    (
        "4 Opaque void* for a T**",
        d + "D3d11.cs",
        "[FieldOffset(40 * 8)] public delegate* unmanaged<ID3D11Device*, ID3D11DeviceContext**, void> GetImmediateContext;",
        '[FieldOffset(40 * 8), As(1, "ID3D11DeviceContext**", Opaque = true)] public delegate* unmanaged<ID3D11Device*, void*, void> GetImmediateContext;',
    ),
    # M4 (exclusive full screen): one per new declaration.
    ("M4 slot", d + "DxgiInterfaces.cs", '[FieldOffset(10 * 8), As(1, "BOOL")]', '[FieldOffset(12 * 8), As(1, "BOOL")]'),
    (
        "M4 BOOL* signedness",
        d + "DxgiInterfaces.cs",
        'As(1, "BOOL*")] public delegate* unmanaged<IDXGISwapChain*, int*,',
        'As(1, "BOOL*")] public delegate* unmanaged<IDXGISwapChain*, uint*,',
    ),
    ("M4 pointee", d + "DxgiInterfaces.cs", "Dxgi.ModeDesc*, int> ResizeTarget", "Dxgi.SwapChainDesc1*, int> ResizeTarget"),
    ("M4 struct field", g, "            public Rational RefreshRate;\n", "            public uint RefreshRate;\n"),
    ("M4 rational field", g, "            public uint Numerator, Denominator;\n", "            public uint Denominator, Numerator;\n"),
    ("M4 enum width", g, '[As("DXGI_MODE_SCALING")] public uint Scaling;', '[As("DXGI_MODE_SCALING")] public ushort Scaling;'),
    ("M4 constant", g, "public const uint SwapEffectDiscard = 0;", "public const uint SwapEffectDiscard = 1;"),
    ("M4 HRESULT", g, "ErrorNotCurrentlyAvailable = unchecked((int)0x887A0022);", "ErrorNotCurrentlyAvailable = unchecked((int)0x887A0021);"),
    # M5 (robustness): one per new declaration.
    ("M5 slot", d + "DxgiInterfaces.cs", '[FieldOffset(9 * 8), As(1, "REFGUID")]', '[FieldOffset(8 * 8), As(1, "REFGUID")]'),
    (
        "M5 pointee",
        d + "DxgiInterfaces.cs",
        "Guid*, Dxgi.LargeInteger*, int> CheckInterfaceSupport",
        "Guid*, Dxgi.Rational*, int> CheckInterfaceSupport",
    ),
    ("M5 struct field", g, "            public long QuadPart;\n", "            public ulong QuadPart;\n"),
    ("M5 guid", g, 'new("54ec77fa-1377-44e6-8c32-88fd5f44c84c")', 'new("54ec77fa-1377-44e6-8c32-88fd5f44c84d")'),
    ("M5 HRESULT", g, "StatusModeChanged = 0x087A0007;", "StatusModeChanged = 0x087A0008;"),
]
ONLY = sys.argv[2] if len(sys.argv) > 2 else None  # a label prefix: only those mutations ("M4")


def check(name):
    r = subprocess.run([str(root / "scripts/com-check.sh")], capture_output=True, text=True)
    out = (r.stdout + r.stderr).strip().splitlines()
    detail = [l for l in out if "error" in l or "ComCheckGen: " in l and "interfaces" not in l or l.startswith("com-check:")]
    return r.returncode, detail


failures = 0
for label in ["control (unchanged)"]:
    code, detail = check(label)
    print(f"===== {label}: exit {code}", *detail, sep="\n  ")
    failures += code != 0
for item in MUTATIONS:
    if ONLY and not item[0].startswith(ONLY):
        continue
    name, edits = item[0], item[1] if len(item) == 2 else [item[1:]]
    saved = {}
    for rel, old, new in edits:
        p = root / rel
        text = saved.setdefault(p, p.read_text()) if p not in saved else p.read_text()
        assert text.count(old) == 1, (name, old)
        p.write_text(text.replace(old, new))
    try:
        code, detail = check(name)
    finally:
        for p, text in saved.items():
            p.write_text(text)
    print(f"===== {name}: exit {code} ({'fails, as it must' if code else 'PASSED: a hole'})", *detail[:4], sep="\n  ")
    failures += code == 0
code, detail = check("control")
print(f"===== control again (restored): exit {code}", *detail, sep="\n  ")
failures += code != 0
print("mutation proof:", "ok" if failures == 0 else f"{failures} wrong verdicts")
sys.exit(1 if failures else 0)
