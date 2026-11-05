#!/usr/bin/env python3
"""Checks the Windows player's executable (src/OniMoono.NativeHost) from its PE file alone, so it
runs anywhere: the GPU hint exports NvOptimusEnablement and AmdPowerXpressRequestHighPerformance
are DWORDs of value 1 in a writable section (read at the export RVA's section offset, as the
original OxygenNotIncluded.exe's were), the native crash records' exports, the GUI subsystem,
the icon group 32512 that GlfwPlatform.SetClassIcon loads, the application manifest and the
version information.

    scripts/check-windows-host.py out/win/host/OniMoono.exe [--like OTHER.exe]

--like compares the subsystem, manifest, icon and version strings with another executable (the
SDK apphost's OniMoono.exe, or the original game's exports with --exports-only). Exits 1 on a
failed check. Building the exe needs Windows (scripts/build-native-host.sh); this does not.
"""
import argparse
import re
import struct
import sys

EXPORTS = ("NvOptimusEnablement", "AmdPowerXpressRequestHighPerformance")
CRASH_EXPORTS = ("moono_crash_install", "moono_crash_exiting", "moono_crash_test")
RT_ICON, RT_GROUP_ICON, RT_VERSION, RT_MANIFEST = 3, 14, 16, 24
IMAGE_SCN_MEM_WRITE = 0x80000000


class Pe:
    def __init__(self, path):
        with open(path, "rb") as f:
            self.data = f.read()
        d = self.data
        if d[:2] != b"MZ":
            raise ValueError(f"{path}: not a PE file")
        pe = struct.unpack_from("<I", d, 0x3C)[0]
        if d[pe:pe + 4] != b"PE\0\0":
            raise ValueError(f"{path}: not a PE file")
        nsections, = struct.unpack_from("<H", d, pe + 6)
        optsize, = struct.unpack_from("<H", d, pe + 20)
        opt = pe + 24
        magic, = struct.unpack_from("<H", d, opt)
        if magic != 0x20B:
            raise ValueError(f"{path}: not a PE32+ (x64) file")
        self.subsystem, self.dll_characteristics = struct.unpack_from("<HH", d, opt + 68)
        ndirs, = struct.unpack_from("<I", d, opt + 108)
        self.dirs = [struct.unpack_from("<II", d, opt + 112 + 8 * i) for i in range(ndirs)]
        self.sections = []
        for i in range(nsections):
            s = opt + optsize + 40 * i
            name = d[s:s + 8].rstrip(b"\0").decode()
            vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", d, s + 8)
            flags, = struct.unpack_from("<I", d, s + 36)
            self.sections.append((name, va, max(vsize, rawsize), rawptr, flags))

    def section(self, rva):
        for s in self.sections:
            if s[1] <= rva < s[1] + s[2]:
                return s
        raise ValueError(f"RVA {rva:#x} is in no section")

    def offset(self, rva):
        _, va, _, rawptr, _ = self.section(rva)
        return rawptr + rva - va

    def cstr(self, rva):
        o = self.offset(rva)
        return self.data[o:self.data.index(b"\0", o)].decode()

    def exports(self):
        """{name: rva}"""
        rva, size = self.dirs[0]
        if not size:
            return {}
        o = self.offset(rva)
        _, _, _, nnames, funcs, names, ords = struct.unpack_from("<IIIIIII", self.data, o + 12)
        result = {}
        for i in range(nnames):
            name_rva, = struct.unpack_from("<I", self.data, self.offset(names) + 4 * i)
            ordinal, = struct.unpack_from("<H", self.data, self.offset(ords) + 2 * i)
            func, = struct.unpack_from("<I", self.data, self.offset(funcs) + 4 * ordinal)
            result[self.cstr(name_rva)] = func
        return result

    def resources(self):
        """{(type, name): [data bytes per language]}"""
        rva, size = self.dirs[2]
        if not size:
            return {}
        root = self.offset(rva)

        def entries(o):
            named, ids = struct.unpack_from("<HH", self.data, o + 12)
            for i in range(named + ids):
                key, target = struct.unpack_from("<II", self.data, o + 16 + 8 * i)
                if key & 0x80000000:
                    so = root + (key & 0x7FFFFFFF)
                    n, = struct.unpack_from("<H", self.data, so)
                    key = self.data[so + 2:so + 2 + 2 * n].decode("utf-16-le")
                yield key, target

        result = {}
        for rtype, t in entries(root):
            for name, n in entries(root + (t & 0x7FFFFFFF)):
                for _, leaf in entries(root + (n & 0x7FFFFFFF)):
                    data_rva, data_size = struct.unpack_from("<II", self.data, root + leaf)
                    o = self.offset(data_rva)
                    result.setdefault((rtype, name), []).append(self.data[o:o + data_size])
        return result


def version_strings(blob):
    """The StringFileInfo values of a VS_VERSIONINFO resource."""
    text = blob.decode("utf-16-le", errors="replace")
    keys = ("CompanyName", "FileDescription", "FileVersion", "InternalName", "LegalCopyright",
            "OriginalFilename", "ProductName", "ProductVersion")
    result = {}
    for key in keys:
        m = re.search(re.escape(key) + "\0+([^\0]*)", text)
        if m:
            result[key] = m.group(1)
    return result


def manifest_text(blob):
    text = blob.decode("utf-8-sig")
    text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
    return re.sub(r">\s+<", "><", text).strip()


def describe(pe):
    res = pe.resources()
    groups = sorted(str(n) for (t, n) in res if t == RT_GROUP_ICON)
    icons = sum(1 for (t, _) in res if t == RT_ICON)
    manifest = [manifest_text(b[0]) for (t, n), b in res.items() if t == RT_MANIFEST]
    version = [version_strings(b[0]) for (t, _), b in res.items() if t == RT_VERSION]
    return groups, icons, manifest, version[0] if version else {}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("exe")
    ap.add_argument("--like", help="another exe to compare the subsystem, manifest, icon and version strings with")
    ap.add_argument("--exports-only", action="store_true", help="check only the two exports")
    a = ap.parse_args()
    pe = Pe(a.exe)
    failed = []

    def check(ok, text):
        print(("ok   " if ok else "FAIL ") + text)
        if not ok:
            failed.append(text)

    exports = pe.exports()
    for name in EXPORTS:
        if name not in exports:
            check(False, f"export {name} missing")
            continue
        rva = exports[name]
        sname, va, _, rawptr, flags = pe.section(rva)
        offset = rawptr + rva - va
        value, = struct.unpack_from("<I", pe.data, offset)
        check(value == 1 and flags & IMAGE_SCN_MEM_WRITE,
              f"export {name}: RVA {rva:#x} in {sname} (file offset {offset:#x}, {'writable' if flags & IMAGE_SCN_MEM_WRITE else 'read-only'}), value {value}")
    if a.exports_only:
        return 1 if failed else 0

    crash = [name for name in CRASH_EXPORTS if name not in exports]
    check(not crash, f"crash record exports (NativeCrashes.cs): {'missing ' + ', '.join(crash) if crash else ', '.join(CRASH_EXPORTS)}")
    check(pe.subsystem == 2, f"subsystem {pe.subsystem} ({'Windows GUI' if pe.subsystem == 2 else 'not GUI'}), DllCharacteristics {pe.dll_characteristics:#06x}")
    groups, icons, manifest, version = describe(pe)
    check("32512" in groups, f"icon group(s) {', '.join(groups) or 'none'} ({icons} images); 32512 for SetClassIcon")
    check(len(manifest) == 1, f"manifest: {manifest[0] if manifest else 'none'}")
    check(bool(version.get("ProductVersion")), f"version: {version}")
    if a.like:
        other = Pe(a.like)
        ogroups, oicons, omanifest, oversion = describe(other)
        check(other.subsystem == pe.subsystem, f"subsystem as {a.like}'s ({other.subsystem})")
        check(other.dll_characteristics == pe.dll_characteristics, f"DllCharacteristics as {a.like}'s ({other.dll_characteristics:#06x})")
        check(omanifest == manifest, f"manifest as {a.like}'s")
        check((ogroups, oicons) == (groups, icons), f"icon resources as {a.like}'s ({', '.join(ogroups)}; {oicons} images)")
        res, ores = pe.resources(), other.resources()
        same_images = sorted(b[0] for (t, _), b in res.items() if t == RT_ICON) == sorted(b[0] for (t, _), b in ores.items() if t == RT_ICON)
        check(same_images, f"icon images byte for byte as {a.like}'s")
        strip = lambda v: {k: (s.split("+")[0] if k == "ProductVersion" else s) for k, s in v.items()}
        check(strip(oversion) == strip(version), f"version strings as {a.like}'s (ProductVersion's commit aside): {oversion}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
