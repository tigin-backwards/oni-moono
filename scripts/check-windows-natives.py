#!/usr/bin/env python3
"""Checks the Windows player folder's native libraries from their PE files alone, so it runs
anywhere: no DLL or EXE imports MSVCP140.dll (or its _1, _2, _atomic_wait and _codecvt_ids
parts) or VCRUNTIME140_1.dll, whose versions vary with the Visual C++ Redistributable a
machine has (#126: vma.dll's std::mutex faulted under an MSVCP140.dll older than 14.40); vma.dll
and shaderc_shared.dll (scripts/build-native-libs.sh) export exactly the C API that BRUTAL's
packages' builds do (src/OniMoono.NativeLibs/*.exports); and SPIRV-Tools-shared.dll, which the
player never loads, is not there.

    scripts/check-windows-natives.py out/win/host

Exits 1 on a failed check. Managed assemblies (PE32 or no x64 code) are skipped.
"""

import importlib.util
import os
import sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
spec = importlib.util.spec_from_file_location("check_windows_host", os.path.join(root, "scripts", "check-windows-host.py"))
host = importlib.util.module_from_spec(spec)
spec.loader.exec_module(host)

FORBIDDEN = ("msvcp140", "vcruntime140_1.dll")
EXPORTS = ("vma.dll", "shaderc_shared.dll")
ABSENT = ("spirv-tools-shared.dll",)


def main():
    if len(sys.argv) != 2:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    folder = sys.argv[1]
    failures = []
    names = sorted(os.listdir(folder))
    lower = {n.lower(): n for n in names}
    for name in names:
        if not name.lower().endswith((".dll", ".exe")):
            continue
        try:
            pe = host.Pe(os.path.join(folder, name))
        except ValueError:
            continue  # a managed assembly (PE32), or not a PE file
        bad = [i for i in pe.imports() if i.startswith(FORBIDDEN)]
        if bad:
            failures.append(f"{name} imports {', '.join(bad)}")
    for name in EXPORTS:
        if name not in lower:
            failures.append(f"{name} missing")
            continue
        expected_path = os.path.join(root, "src", "OniMoono.NativeLibs", name[: -len(".dll")] + ".exports")
        with open(expected_path) as f:
            expected = {line.strip() for line in f if line.strip()}
        exports = set(host.Pe(os.path.join(folder, lower[name])).exports())
        if exports != expected:
            missing, extra = sorted(expected - exports), sorted(exports - expected)
            failures.append(f"{name} exports differ: missing {missing or 'none'}, extra {extra or 'none'}")
    for name in ABSENT:
        if name in lower:
            failures.append(f"{lower[name]} is there (the player never loads it)")
    for f in failures:
        print(f"check-windows-natives: {f}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
