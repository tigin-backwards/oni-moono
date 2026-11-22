#!/usr/bin/env python3
"""Checks the Linux player's executable (src/OniMoono.NativeHost, scripts/build-native-host-linux.sh)
from its ELF file alone: an x86-64 position-independent executable for /lib64/ld-linux-x86-64.so.2,
its newest glibc symbol version at most 2.31 (the Steam Runtime's sniper, so it can always say
why something else fails), no C++ runtime, no RPATH or RUNPATH, only libc's own libraries needed,
immediate binding and a non-executable stack; and the exports the player looks up
(NativeCrashes.cs, FmodQuarantine.cs) in its dynamic symbol table, and nothing else exported.

    scripts/check-linux-host.py OniMoono

Exits 1 on a failed check.
"""

import struct
import sys

EXPORTS = (
    "moono_crash_install",
    "moono_crash_describe",
    "moono_crash_exiting",
    "moono_crash_test",
    "moono_fmod_heap_init",
    "moono_fmod_alloc",
    "moono_fmod_realloc",
    "moono_fmod_free",
    "moono_fmod_heap_stats",
    "moono_fmod_heap_events",
    "moono_fmod_heap_flush",
    "moono_fmod_heap_leave",
)
NEWEST_GLIBC = (2, 31)
ALLOWED_NEEDED = {"libc.so.6", "libdl.so.2", "libpthread.so.0", "libm.so.6", "ld-linux-x86-64.so.2"}
INTERPRETER = "/lib64/ld-linux-x86-64.so.2"
PT_INTERP, PT_DYNAMIC, PT_GNU_STACK = 3, 2, 0x6474E551
DT_NEEDED, DT_STRTAB, DT_SYMTAB, DT_STRSZ, DT_RPATH, DT_RUNPATH, DT_FLAGS, DT_FLAGS_1 = 1, 5, 6, 10, 15, 29, 30, 0x6FFFFFFB
DT_VERNEED, DT_VERNEEDNUM, DT_GNU_HASH, DT_HASH = 0x6FFFFFFE, 0x6FFFFFFF, 0x6FFFFEF5, 4
DF_BIND_NOW, DF_1_NOW, DF_1_PIE = 0x8, 0x1, 0x08000000


class Elf:
    def __init__(self, path):
        with open(path, "rb") as f:
            self.d = f.read()
        d = self.d
        if d[:4] != b"\x7fELF" or d[4] != 2 or d[5] != 1:
            raise ValueError(f"{path}: not a 64-bit little-endian ELF file")
        self.type, self.machine = struct.unpack_from("<HH", d, 16)
        (phoff,) = struct.unpack_from("<Q", d, 32)
        (shoff,) = struct.unpack_from("<Q", d, 40)
        phentsize, phnum, shentsize, shnum = struct.unpack_from("<HHHH", d, 54)
        self.segments = [struct.unpack_from("<IIQQQQQQ", d, phoff + i * phentsize) for i in range(phnum)]
        self.sections = [struct.unpack_from("<IIQQQQIIQQ", d, shoff + i * shentsize) for i in range(shnum)]

    def offset_of(self, vaddr):
        for p_type, _flags, offset, va, _pa, filesz, _memsz, _align in self.segments:
            if p_type == 1 and va <= vaddr < va + filesz:
                return offset + vaddr - va
        raise ValueError(f"address {vaddr:#x} is in no segment")

    def cstring(self, at):
        return self.d[at : self.d.index(b"\0", at)].decode()

    def dynamic(self):
        seg = next(s for s in self.segments if s[0] == PT_DYNAMIC)
        entries, at = [], seg[2]
        while True:
            tag, value = struct.unpack_from("<qQ", self.d, at)
            if tag == 0:
                return entries
            entries.append((tag, value))
            at += 16


def check(path):
    elf = Elf(path)
    problems, notes = [], []
    if elf.machine != 62:
        problems.append(f"machine {elf.machine}, not x86-64")
    if elf.type != 3:
        problems.append("not position independent (ET_DYN)")
    interp = next((s for s in elf.segments if s[0] == PT_INTERP), None)
    got = elf.cstring(interp[2]) if interp else None
    if got != INTERPRETER:
        problems.append(f"interpreter {got}, not {INTERPRETER}")
    stack = next((s for s in elf.segments if s[0] == PT_GNU_STACK), None)
    if stack is None or stack[1] & 1:
        problems.append("the stack is executable")
    dyn = elf.dynamic()
    tags = {}
    for tag, value in dyn:
        tags.setdefault(tag, []).append(value)
    strtab = elf.offset_of(tags[DT_STRTAB][0])
    name = lambda off: elf.cstring(strtab + off)  # noqa: E731
    needed = [name(v) for v in tags.get(DT_NEEDED, [])]
    bad = [n for n in needed if n not in ALLOWED_NEEDED]
    if bad:
        problems.append(f"needs {', '.join(bad)} (only libc's own libraries may be: the rest is loaded at run time)")
    if DT_RPATH in tags or DT_RUNPATH in tags:
        problems.append(f"has an RPATH or RUNPATH ({', '.join(name(v) for v in tags.get(DT_RPATH, []) + tags.get(DT_RUNPATH, []))})")
    flags, flags_1 = tags.get(DT_FLAGS, [0])[0], tags.get(DT_FLAGS_1, [0])[0]
    if not (flags & DF_BIND_NOW or flags_1 & DF_1_NOW):
        problems.append("no immediate binding (-z now)")
    # The symbol versions it needs.
    versions = set()
    if DT_VERNEED in tags:
        at = elf.offset_of(tags[DT_VERNEED][0])
        for _ in range(tags[DT_VERNEEDNUM][0]):
            _version, count, _file, aux, nxt = struct.unpack_from("<HHIII", elf.d, at)
            a = at + aux
            for _ in range(count):
                _hash, _flags, _other, vname, anext = struct.unpack_from("<IHHII", elf.d, a)
                versions.add(name(vname))
                if not anext:
                    break
                a += anext
            if not nxt:
                break
            at += nxt
    glibc = sorted(tuple(int(x) for x in v[6:].split(".")) for v in versions if v.startswith("GLIBC_") and v[6:].replace(".", "").isdigit())
    if glibc and glibc[-1] > NEWEST_GLIBC:
        problems.append(f"needs GLIBC_{'.'.join(map(str, glibc[-1]))}, newer than {'.'.join(map(str, NEWEST_GLIBC))}")
    if any(v.startswith(("GLIBCXX_", "CXXABI_")) for v in versions):
        problems.append("needs the C++ runtime")
    if any(v.startswith("GCC_") for v in versions):
        problems.append("needs libgcc_s (build with -static-libgcc)")
    # The dynamic symbol table, from its section.
    dynsym = next((s for s in elf.sections if s[1] == 11), None)
    exported = set()
    if dynsym:
        _n, _t, _f, _a, off, size, link, _i, _al, entsize = dynsym
        strs = elf.sections[link][4]
        for i in range(size // entsize):
            st_name, st_info, st_other, st_shndx, _value, _size = struct.unpack_from("<IBBHQQ", elf.d, off + i * entsize)
            # Defined functions (glibc's environ and stderr come in as copied data objects).
            if st_shndx != 0 and st_info >> 4 in (1, 2) and st_info & 15 == 2 and st_other & 3 == 0 and st_name:
                exported.add(elf.cstring(strs + st_name))
    missing = [e for e in EXPORTS if e not in exported]
    if missing:
        problems.append(f"does not export {', '.join(missing)}")
    extra = sorted(e for e in exported - set(EXPORTS) if not e.startswith("_"))
    if extra:
        problems.append(f"exports more than the player looks up: {', '.join(extra[:8])}")
    notes.append(f"glibc {'.'.join(map(str, glibc[-1])) if glibc else 'none'}, needs {', '.join(needed)}, {len(EXPORTS) - len(missing)} exports")
    return problems, notes


def main(argv):
    if len(argv) != 2:
        print("usage: scripts/check-linux-host.py OniMoono", file=sys.stderr)
        return 2
    problems, notes = check(argv[1])
    for p in problems:
        print(f"FAIL {p}")
    for n in notes:
        print(f"{'ok  ' if not problems else '    '} {argv[1]}: {n}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
