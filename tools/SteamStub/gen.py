#!/usr/bin/env python3
"""Generates the offline Steam test double exporting every entry point Steamworks.NET binds
(src/ThirdParty/Steamworks.NET), for runs on machines without a logged-in Steam client, and
for Windows test runs, which must not reach the user's Steam account: x86-64 assembly for
libsteam_api.so (System V calling convention; assembled and linked by build.sh), or, for an
output path ending in .dll, steam_api64.dll itself (Win64 calling convention; written here as
a PE32+ image, since neither side has a native toolchain for Windows). TEST INFRASTRUCTURE
ONLY: the product loads the game's own library; this one is used only when a run passes
`--native-override steam_api=<path>` (scripts/run-offline.sh, scripts/run-windows.sh).

Every function returns zero (false, null, invalid handle, empty count) except those in
VALUES, which describe a logged-in Steam user who owns the base game, has no DLC and no
Workshop subscriptions, with English as the language. No callback is ever delivered.

  gen.py NATIVE_METHODS OUT [--dlc | --dlc-except APP_ID] [--ugc]

--dlc describes a user who owns and has installed every DLC (BIsSubscribedApp,
BIsDlcInstalled and UserHasLicenseForApp answer yes for any app), as the e2e and QA runs'
all-DLC double does; --dlc-except APP_ID the same, but for that one app (U60 QA spec A4:
COSMETIC2_ID, app 5315880, whose bundle the data does not ship). --ugc leaves out the
functions tools/SteamStub/ugc.s defines (one Workshop subscription and its call results, for
U60 QA spec M1 and M2; Linux only), which build.sh assembles and links beside them."""

import re, sys

STEAM_ID = 0x0110000100000001  # individual account 1, public universe
APP_ID = 457140

# name -> ("int", value) | ("str", text) | ("ptr",) | ("cmp", argument, value, "eq"|"ne"): the
# last returns (argument == value) or (argument != value) for the 32-bit argument at that
# position (1 is the interface pointer).
VALUES = {
    "SteamAPI_Init": ("int", 1),
    "SteamAPI_IsSteamRunning": ("int", 1),
    "SteamAPI_GetHSteamPipe": ("int", 1),
    "SteamAPI_GetHSteamUser": ("int", 1),
    "SteamAPI_GetSteamInstallPath": ("str", ""),
    "SteamInternal_CreateInterface": ("ptr",),
    "SteamAPI_ISteamUser_BLoggedOn": ("int", 1),
    "SteamAPI_ISteamUser_GetSteamID": ("int", STEAM_ID),
    "SteamAPI_ISteamUser_GetPlayerSteamLevel": ("int", 1),
    "SteamAPI_ISteamFriends_GetPersonaName": ("str", "Player"),
    "SteamAPI_ISteamFriends_GetFriendPersonaName": ("str", ""),
    "SteamAPI_ISteamApps_BIsSubscribed": ("int", 1),
    # (self, appID): owned only for the base game.
    "SteamAPI_ISteamApps_BIsSubscribedApp": ("cmp", 2, APP_ID, "eq"),
    # (self, steamID, appID): k_EUserHasLicenseResultHasLicense (0) for the base game,
    # k_EUserHasLicenseResultDoesNotHaveLicense (1) for anything else (the DLCs).
    "SteamAPI_ISteamUser_UserHasLicenseForApp": ("cmp", 3, APP_ID, "ne"),
    "SteamAPI_ISteamApps_GetCurrentGameLanguage": ("str", "english"),
    "SteamAPI_ISteamApps_GetAvailableGameLanguages": ("str", "english"),
    "SteamAPI_ISteamApps_GetAppOwner": ("int", STEAM_ID),
    "SteamAPI_ISteamApps_GetEarliestPurchaseUnixTime": ("int", 1500000000),
    "SteamAPI_ISteamApps_GetAppBuildId": ("int", 1),
    "SteamAPI_ISteamUtils_GetAppID": ("int", APP_ID),
    "SteamAPI_ISteamUtils_GetSteamUILanguage": ("str", "english"),
    "SteamAPI_ISteamUtils_GetIPCountry": ("str", "US"),
    "SteamAPI_ISteamUtils_GetServerRealTime": ("int", 1700000000),
    "SteamAPI_ISteamUGC_CreateQueryUserUGCRequest": ("int", 0xFFFFFFFFFFFFFFFF),
    "SteamAPI_ISteamUGC_CreateQueryAllUGCRequestPage": ("int", 0xFFFFFFFFFFFFFFFF),
    "SteamAPI_ISteamUGC_CreateQueryAllUGCRequestCursor": ("int", 0xFFFFFFFFFFFFFFFF),
    "SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest": ("int", 0xFFFFFFFFFFFFFFFF),
}
# Interface accessors: a non-null dummy object (calls only pass it back).
POINTER_PATTERNS = [re.compile(r"^SteamAPI_ISteamClient_GetISteam"), re.compile(r"^SteamAPI_Steam[A-Za-z]+_v\d+$"), re.compile(r"^SteamInternal_FindOrCreate")]


def entry_points(path):
    names = re.findall(r'EntryPoint\s*=\s*"([A-Za-z0-9_]+)"', open(path, encoding="utf-8").read())
    return sorted(set(names))


def value_of(name):
    v = VALUES.get(name)
    if v is None and any(p.match(name) for p in POINTER_PATTERNS):
        v = ("ptr",)
    return v


SYSV_ARGS = {2: "esi", 3: "edx"}


def write_elf_assembly(names, out):
    lines = [".intel_syntax noprefix", ".text"]
    strings = {}
    for name in names:
        v = value_of(name)
        if v is None:
            body = "xor eax, eax"
        elif v[0] == "int":
            body = f"mov rax, {v[1]:#x}"
        elif v[0] == "ptr":
            body = "lea rax, [rip + stub_object]"
        elif v[0] == "str":
            label = strings.setdefault(v[1], f"stub_str{len(strings)}")
            body = f"lea rax, [rip + {label}]"
        else:
            _, arg, value, test = v
            body = f"xor eax, eax\n  cmp {SYSV_ARGS[arg]}, {value}\n  set{'e' if test == 'eq' else 'ne'} al"
        lines += [f".globl {name}", f".type {name}, @function", f"{name}:", f"  {body}", "  pxor xmm0, xmm0", "  ret"]
    lines += [".section .rodata"]
    for text, label in strings.items():
        lines += [f"{label}:", f'  .asciz "{text}"']
    lines += [".bss", ".balign 64", "stub_object:", "  .zero 4096", '.section .note.GNU-stack,"",@progbits']
    open(out, "w").write("\n".join(lines) + "\n")


# Win64: the 32-bit argument registers (ecx, edx, r8d) as `cmp reg, imm32` encodings.
WIN64_CMP = {2: bytes([0x81, 0xFA]), 3: bytes([0x41, 0x81, 0xF8])}
PXOR_XMM0 = bytes([0x66, 0x0F, 0xEF, 0xC0])
RET = bytes([0xC3])


def write_pe(names, out):
    """steam_api64.dll: a PE32+ DLL with no entry point and no imports. Sections: .text (the
    functions, each 16-byte aligned), .rdata (strings and the export directory) and .data (the
    zeroed dummy object). Code addresses data only RIP-relatively, so the image needs no base
    relocations."""
    import struct

    FILE_ALIGN, SECT_ALIGN, HEADERS = 0x200, 0x1000, 0x400
    TEXT, RDATA, DATA = 0x1000, None, None
    align = lambda n, a: (n + a - 1) // a * a
    # Lay out code first with placeholder displacements, then patch them once rdata is placed.
    code = bytearray()
    fixups = []  # (offset of disp32, end of instruction, target kind, key)
    starts = {}
    strings = {}
    for name in names:
        while len(code) % 16:
            code.append(0xCC)
        starts[name] = len(code)
        v = value_of(name)
        if v is None:
            code += bytes([0x31, 0xC0])
        elif v[0] == "int":
            code += bytes([0x48, 0xB8]) + struct.pack("<Q", v[1] & 0xFFFFFFFFFFFFFFFF)
        elif v[0] in ("ptr", "str"):
            code += bytes([0x48, 0x8D, 0x05]) + b"\0\0\0\0"
            key = None
            if v[0] == "str":
                key = strings.setdefault(v[1], len(strings))
            fixups.append((len(code) - 4, len(code), v[0], key))
        else:
            _, arg, value, test = v
            code += bytes([0x31, 0xC0]) + WIN64_CMP[arg] + struct.pack("<I", value) + bytes([0x0F, 0x94 if test == "eq" else 0x95, 0xC0])
        code += PXOR_XMM0 + RET
    RDATA = TEXT + align(len(code), SECT_ALIGN)
    # .rdata: strings, then the export directory and its tables.
    rdata = bytearray()
    string_rva = {}
    for text, key in strings.items():
        string_rva[key] = RDATA + len(rdata)
        rdata += text.encode() + b"\0"
    dll_name = b"steam_api64.dll\0"
    name_rva = RDATA + len(rdata)
    rdata += dll_name
    name_rvas = []
    for name in names:
        name_rvas.append(RDATA + len(rdata))
        rdata += name.encode() + b"\0"
    while len(rdata) % 4:
        rdata.append(0)
    functions_rva = RDATA + len(rdata)
    rdata += b"".join(struct.pack("<I", TEXT + starts[n]) for n in names)
    names_rva = RDATA + len(rdata)
    rdata += b"".join(struct.pack("<I", r) for r in name_rvas)
    ordinals_rva = RDATA + len(rdata)
    rdata += b"".join(struct.pack("<H", i) for i in range(len(names)))
    while len(rdata) % 4:
        rdata.append(0)
    export_rva = RDATA + len(rdata)
    rdata += struct.pack("<IIHHIIIIIII", 0, 0, 0, 0, name_rva, 1, len(names), len(names), functions_rva, names_rva, ordinals_rva)
    export_size = 40
    DATA = RDATA + align(len(rdata), SECT_ALIGN)
    object_size = 4096
    for at, end, kind, key in fixups:
        target = DATA if kind == "ptr" else string_rva[key]
        struct.pack_into("<i", code, at, target - (TEXT + end))
    size_of_image = DATA + align(object_size, SECT_ALIGN)
    text_raw = align(len(code), FILE_ALIGN)
    rdata_raw = align(len(rdata), FILE_ALIGN)
    # Headers.
    dos = bytearray(64)
    dos[0:2] = b"MZ"
    struct.pack_into("<I", dos, 0x3C, 64)
    coff = struct.pack("<HHIIIHH", 0x8664, 3, 0, 0, 0, 0xF0, 0x2022)  # AMD64; executable, large address aware, DLL
    data_dirs = [(export_rva, export_size)] + [(0, 0)] * 15
    opt = struct.pack(
        "<HBBIIIIIQIIHHHHHHIIIIHHQQQQII",
        0x20B,
        14,
        0,
        text_raw,
        rdata_raw,
        0,
        0,
        TEXT,
        0x180000000,
        SECT_ALIGN,
        FILE_ALIGN,
        6,
        0,
        0,
        0,
        6,
        0,
        0,
        size_of_image,
        HEADERS,
        0,
        3,
        0x0160,  # console subsystem; high-entropy VA, dynamic base, NX
        0x100000,
        0x1000,
        0x100000,
        0x1000,
        0,
        16,
    )
    opt += b"".join(struct.pack("<II", r, s) for r, s in data_dirs)

    def section(name, vsize, vaddr, rawsize, rawptr, flags):
        return name.ljust(8, b"\0") + struct.pack("<IIIIIIHHI", vsize, vaddr, rawsize, rawptr, 0, 0, 0, 0, flags)

    sections = section(b".text", len(code), TEXT, text_raw, HEADERS, 0x60000020)  # code, execute, read
    sections += section(b".rdata", len(rdata), RDATA, rdata_raw, HEADERS + text_raw, 0x40000040)  # initialized data, read
    sections += section(b".data", object_size, DATA, 0, 0, 0xC0000080)  # uninitialized data, read, write
    headers = bytes(dos) + b"PE\0\0" + coff + opt + sections
    image = headers.ljust(HEADERS, b"\0") + bytes(code).ljust(text_raw, b"\0") + bytes(rdata).ljust(rdata_raw, b"\0")
    open(out, "wb").write(image)


def own_dlc(except_app=None):
    """VALUES for a user who owns every DLC, or every DLC but EXCEPT_APP."""
    if except_app is None:
        VALUES["SteamAPI_ISteamApps_BIsSubscribedApp"] = ("int", 1)
        VALUES["SteamAPI_ISteamApps_BIsDlcInstalled"] = ("int", 1)
        VALUES["SteamAPI_ISteamUser_UserHasLicenseForApp"] = ("int", 0)
    else:
        VALUES["SteamAPI_ISteamApps_BIsSubscribedApp"] = ("cmp", 2, except_app, "ne")
        VALUES["SteamAPI_ISteamApps_BIsDlcInstalled"] = ("cmp", 2, except_app, "ne")
        VALUES["SteamAPI_ISteamUser_UserHasLicenseForApp"] = ("cmp", 3, except_app, "eq")


def ugc_names():
    """The functions ugc.s defines."""
    path = __file__.rsplit("/", 1)[0] + "/ugc.s"
    return set(re.findall(r"^\.globl (\w+)", open(path, encoding="utf-8").read(), re.M))


def main(native_methods, out, options=()):
    options = list(options)
    ugc = "--ugc" in options
    if ugc:
        options.remove("--ugc")
    if options[:1] == ["--dlc"] and len(options) == 1:
        own_dlc()
    elif options[:1] == ["--dlc-except"] and len(options) == 2:
        own_dlc(int(options[1]))
    elif options:
        sys.exit("usage: gen.py NATIVE_METHODS OUT [--dlc | --dlc-except APP_ID] [--ugc]")
    names = entry_points(native_methods)
    if ugc:
        if out.endswith(".dll"):
            sys.exit("--ugc: Linux only")
        names = [n for n in names if n not in ugc_names()]
    if out.endswith(".dll"):
        write_pe(names, out)
    else:
        write_elf_assembly(names, out)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3:])
