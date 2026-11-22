"""The COM check's mutation proof (docs/research/d3d11-plan.md, M2 part A): in a scratch copy of
the branch, the unchanged copy first and last (each must pass), and between them one deliberate
mismatch per run, each of which must fail scripts/com-check.sh. Files are restored by writing their
text back (a new mtime, so the next build sees the change). Prints one verdict line per run.
  python3 -I tools/ComCheckGen/mutation_proof.py COPY   (COPY: `git archive HEAD` extracted elsewhere)"""

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
]


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
for name, rel, old, new in MUTATIONS:
    p = root / rel
    text = p.read_text()
    assert text.count(old) == 1, (name, old)
    p.write_text(text.replace(old, new))
    try:
        code, detail = check(name)
    finally:
        p.write_text(text)
    print(f"===== {name}: exit {code} ({'fails, as it must' if code else 'PASSED: a hole'})", *detail[:4], sep="\n  ")
    failures += code == 0
code, detail = check("control")
print(f"===== control again (restored): exit {code}", *detail, sep="\n  ")
failures += code != 0
print("mutation proof:", "ok" if failures == 0 else f"{failures} wrong verdicts")
sys.exit(1 if failures else 0)
