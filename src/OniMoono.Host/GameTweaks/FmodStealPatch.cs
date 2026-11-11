#nullable enable
using System;
using System.Collections.Generic;

namespace OniMoono.Host.GameTweaks
{
    /// One FMOD Studio library fmod-steal-fix knows how to patch (FmodStealPatch.Known): the file
    /// as the game ships it (its name and SHA-256); the patch site (its offset from the module's
    /// base, the bytes expected from there, which run past what is replaced, and the bytes
    /// written); more bytes that must be as expected elsewhere (`Checks`); and, on Windows, two
    /// call-free stubs in the slack at the end of the module's code section (`Cave`, the second
    /// at `Post`), a counter in the slack at the end of its data section, the return address of
    /// the helper's call in the site and in the original (`OldReturn`), and the start of the
    /// function's unwind entry, which must cover both.
    internal sealed record FmodStealModule(
        string Platform, string File, string Sha256, string Game,
        int Site, byte[] Old, byte[] New,
        (int Offset, byte[] Bytes)[]? Checks = null,
        int Cave = 0, int Post = 0, byte[]? CaveCode = null, int CaveSpace = 0,
        int Counter = 0, int Return = 0, int OldReturn = 0, int Function = 0);

    /// What fmod-steal-fix checks and writes (FmodStealFix), in plain .NET: the tests compile it in
    /// and run it on byte arrays.
    ///
    /// The bug, in FMOD 2.03.12 (build 160365), `EventInstance::changeStateStopped`: when an
    /// instance stops, it fires STOPPED, and when the game has released it, it destroys it, and
    /// then clears `this+0x4B0` (the instance's timeline position, in milliseconds, which
    /// `getTimelinePosition` reads and the timeline update advances). When the instance was
    /// destroyed there and then (not inside FMOD's playback update), that store lands in freed
    /// memory: 4 zero bytes at offset 0x4B8 of a freed 1,312-byte block (1,328 in the logging
    /// build). It happens when a bus's instance limit steals an instance that is still loading
    /// its sample data (FMOD's `BusInstance::addPlayingEvent` stops it straight away), the stack
    /// of FMOD's forum thread 24589.
    ///
    /// The patch skips that one store when, and only when, the instance was destroyed before it;
    /// every other path runs the same instructions on the same state:
    ///
    /// - Linux (`libfmodstudio.so`): the destroy is inlined. After its call (the system's
    ///   immediate destroy, which frees the instance), `test eax,eax; jne epilogue` falls through
    ///   to the store and then `xor eax,eax; jmp epilogue`. It becomes `jmp epilogue`: on success
    ///   eax is already 0, the value the function returned after the store; on failure the
    ///   original jumped there with eax too. The deferred paths (released inside the playback
    ///   update, or flagged to be destroyed later) jump to the store as before. U60's logging
    ///   build: the `je store` after the destroy's call is retargeted one instruction past the
    ///   store, to its `xor ebp,ebp; jmp epilogue` (one byte).
    /// - Windows (`fmodstudio.dll`): the destroy is a helper (fmodstudio+0x69790) that sets the
    ///   destroy flag and destroys at once unless the instance is flagged 0x80 at +0x2F8 or the
    ///   system (at +0x2E8) is inside its update (+0x7A8). The site jumps to a stub that tests
    ///   the released flag as before (not released: to the store), reads those two conditions
    ///   (the helper reads them first thing, unchanged; nothing runs between) into edi, and jumps
    ///   back to the helper's call, which stays in the function, so its return address is
    ///   covered by the function's unwind data. The instruction there is the original's
    ///   `test eax, eax`, not a jump: the unwinder takes a jump at the return address for a
    ///   tail-call epilogue. At run time, before anything stays written, the system's own
    ///   unwinder must unwind a synthetic stack from the new return address to the same caller
    ///   as from the original one (else the original bytes are put back). A jump after it goes
    ///   to a second stub: an error is returned as before (the original
    ///   skipped the store too); deferred, back to the store; destroyed, the store is skipped,
    ///   counted, and 0 returned. The stubs call nothing and touch no stack. edi is saved by the
    ///   fragment prolog at +0x6894E, used as a loop variable until +0x68A3A, and dead from there
    ///   to its restore at +0x68A85. U60's logging build: the same with r15 (saved in the prolog,
    ///   restored in the epilogue, not used after the site), the original's `mov edi, eax` after
    ///   the call (at its return address), and an error sent to the original's logging path.
    ///   The two flags are read before the helper reads them on the same thread with no code
    ///   between; were either to change in between, the stub would only store into a live
    ///   instance as before (deferred) or count a destroy that happened (destroyed).
    ///
    /// It is applied only when FMOD's core reports 2.03.12 build 160365 (the core system's
    /// getVersion), the module file's SHA-256 is one listed here, and every byte expected is (on
    /// Windows also the cave and the counter still zero, the slack's fill). Anything else, it does
    /// nothing but log.
    internal static class FmodStealPatch
    {
        public const uint Version = 0x00020312, Build = 160365;

        /// The known modules, by game build.
        public static readonly FmodStealModule[] Known =
        [
            new("windows", "fmodstudio.dll", "635b1f4836e8709a939245881020b31ef9aa91f93321bc0ef7917d63ee09eddb", "U59 744825",
                Site: 0x68A64,
                // +0x68A64..+0x68A8A: the released test, the helper's call, the store, the epilogue.
                Old: [0x8B, 0x83, 0xF8, 0x02, 0x00, 0x00, 0xC1, 0xE8, 0x03, 0xA8, 0x01, 0x74, 0x0C, 0x48, 0x8B, 0xCB, 0xE8, 0x17, 0x0D, 0x00, 0x00, 0x85, 0xC0, 0x75, 0x08, 0x89, 0xAB, 0xB0, 0x04, 0x00, 0x00, 0x33, 0xC0, 0x48, 0x8B, 0x7C, 0x24, 0x38],
                // jmp pre; mov rcx, rbx; call +0x69790; test eax, eax; jmp post; int3 × 5
                New: [0xE9, 0x97, 0xC9, 0x1E, 0x00, 0x48, 0x8B, 0xCB, 0xE8, 0x1F, 0x0D, 0x00, 0x00, 0x85, 0xC0, 0xE9, 0xB7, 0xC9, 0x1E, 0x00, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC],
                Checks: [(0x69790, [0x48, 0x83, 0xEC, 0x28, 0x8B, 0x81, 0xF8, 0x02, 0x00, 0x00, 0x48, 0x8B, 0xD1, 0x83, 0xC8, 0x04, 0x89, 0x81, 0xF8, 0x02, 0x00, 0x00, 0xC1, 0xE8, 0x07, 0xA8, 0x01, 0x75, 0x20, 0x48, 0x8B, 0x89, 0xE8, 0x02, 0x00, 0x00, 0x80, 0xB9, 0xA8, 0x07, 0x00, 0x00, 0x00, 0x75, 0x10, 0x48, 0x81, 0xC1, 0xE8, 0x03, 0x00, 0x00])],   // the helper, up to its destroy
                Cave: 0x255400, Post: 0x2F,
                CaveCode:
                [
                    0xF6, 0x83, 0xF8, 0x02, 0x00, 0x00, 0x08,           // pre: test byte [rbx+0x2F8], 0x08    released?
                    0x0F, 0x84, 0x70, 0x36, 0xE1, 0xFF,                 // jz +0x68A7D                       no: the store, as before
                    0x31, 0xFF,                                         // xor edi, edi                      edi: deferred
                    0xF6, 0x83, 0xF8, 0x02, 0x00, 0x00, 0x80,           // test byte [rbx+0x2F8], 0x80
                    0x75, 0x10,                                         // jnz deferred
                    0x48, 0x8B, 0x83, 0xE8, 0x02, 0x00, 0x00,           // mov rax, [rbx+0x2E8]              the system
                    0x80, 0xB8, 0xA8, 0x07, 0x00, 0x00, 0x00,           // cmp byte [rax+0x7A8], 0           inside its update?
                    0x74, 0x02,                                         // je call
                    0xFF, 0xC7,                                         // deferred: inc edi
                    0xE9, 0x3A, 0x36, 0xE1, 0xFF,                       // call: jmp +0x68A69                the call, in the function
                    0x0F, 0x85, 0x50, 0x36, 0xE1, 0xFF,                 // post: jnz +0x68A85                error: returned as before
                    0x85, 0xFF,                                         // test edi, edi
                    0x0F, 0x85, 0x40, 0x36, 0xE1, 0xFF,                 // jnz +0x68A7D                      deferred: the store, as before
                    0xF0, 0xFF, 0x05, 0xAC, 0x1B, 0x0E, 0x00,           // lock inc dword [+0x336FF0]        destroyed: counted
                    0xE9, 0x3C, 0x36, 0xE1, 0xFF,                       // jmp +0x68A85                      eax is 0: returned
                ],
                CaveSpace: 0x80, Counter: 0x336FF0, Return: 0x68A71, OldReturn: 0x68A79, Function: 0x6894E),
            new("linux", "libfmodstudio.so", "a4eebdd6171fea7b308db6ecedd4e6db0db3840aed59b5dfbdc59570b58169db", "U59 744825",
                Site: 0x1303A5,
                // test eax, eax; jne epilogue; the store; xor eax, eax; jmp epilogue
                Old: [0x85, 0xC0, 0x0F, 0x85, 0xBA, 0xFE, 0xFF, 0xFF, 0xC7, 0x83, 0xB0, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x31, 0xC0, 0xE9, 0xA9, 0xFE, 0xFF, 0xFF],
                New: [0xE9, 0xBD, 0xFE, 0xFF, 0xFF, 0x0F, 0x1F, 0x00],   // jmp epilogue; nop
                Checks: [(0x13036D, [0x8B, 0x83, 0xF8, 0x02, 0x00, 0x00, 0xA8, 0x08, 0x74, 0x36, 0x89, 0xC1, 0x83, 0xC9, 0x04, 0x89, 0x8B, 0xF8, 0x02, 0x00, 0x00, 0x84, 0xC0, 0x78, 0x27, 0x48, 0x8B, 0xBB, 0xE8, 0x02, 0x00, 0x00, 0x80, 0xBF, 0xA8, 0x07, 0x00, 0x00, 0x00, 0x75, 0x17, 0x48, 0x81, 0xC7, 0xE8, 0x03, 0x00, 0x00, 0x48, 0x89, 0xDE, 0xE8, 0x8B, 0xA0, 0x00, 0x00])]),   // the released test and the inlined destroy before it
            new("windows", "fmodstudioL.dll", "f2c41bc120733a682846a344a04a49fe66e1a74979887f98a4647c25023fae71", "U60 761015-762230",
                Site: 0xC1AA7,
                // +0xC1AA7..+0xC1AE8: the released test, the helper's call, its error log, the store, the epilogue.
                Old: [0x8B, 0x83, 0xF8, 0x02, 0x00, 0x00, 0xC1, 0xE8, 0x03, 0xA8, 0x01, 0x74, 0x26, 0x48, 0x8B, 0xCB, 0xE8, 0x14, 0x11, 0x00, 0x00, 0x8B, 0xF8, 0x85, 0xC0, 0x74, 0x18, 0x41, 0xB8, 0x3A, 0x02, 0x00, 0x00, 0x48, 0x8D, 0x15, 0x11, 0xB5, 0x27, 0x00, 0x8B, 0xCF, 0xE8, 0xEA, 0xA4, 0x1B, 0x00, 0x8B, 0xC7, 0xEB, 0x09, 0x44, 0x89, 0xB3, 0xB0, 0x04, 0x00, 0x00, 0x33, 0xC0, 0x4C, 0x8B, 0x74, 0x24, 0x48],
                // jmp pre; mov rcx, rbx; call +0xC2BD0; mov edi, eax; jmp post; int3 × 7
                New: [0xE9, 0xD4, 0x31, 0x27, 0x00, 0x48, 0x8B, 0xCB, 0xE8, 0x1C, 0x11, 0x00, 0x00, 0x8B, 0xF8, 0xE9, 0xF6, 0x31, 0x27, 0x00, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC],
                Checks: [(0xC2BD0, [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x8B, 0x81, 0xF8, 0x02, 0x00, 0x00, 0x48, 0x8B, 0xD1, 0x83, 0xC8, 0x04, 0x89, 0x81, 0xF8, 0x02, 0x00, 0x00, 0xC1, 0xE8, 0x07, 0xA8, 0x01, 0x75, 0x52, 0x48, 0x8B, 0x89, 0xE8, 0x02, 0x00, 0x00, 0x80, 0xB9, 0xA8, 0x07, 0x00, 0x00, 0x00, 0x75, 0x42, 0x48, 0x81, 0xC1, 0xE8, 0x03, 0x00, 0x00])],   // the helper, up to its destroy
                Cave: 0x334C80, Post: 0x31,
                CaveCode:
                [
                    0xF6, 0x83, 0xF8, 0x02, 0x00, 0x00, 0x08,           // pre: test byte [rbx+0x2F8], 0x08    released?
                    0x0F, 0x84, 0x4D, 0xCE, 0xD8, 0xFF,                 // jz +0xC1ADA                       no: the store, as before
                    0x45, 0x31, 0xFF,                                   // xor r15d, r15d                    r15d: deferred
                    0xF6, 0x83, 0xF8, 0x02, 0x00, 0x00, 0x80,           // test byte [rbx+0x2F8], 0x80
                    0x75, 0x10,                                         // jnz deferred
                    0x48, 0x8B, 0x83, 0xE8, 0x02, 0x00, 0x00,           // mov rax, [rbx+0x2E8]              the system
                    0x80, 0xB8, 0xA8, 0x07, 0x00, 0x00, 0x00,           // cmp byte [rax+0x7A8], 0           inside its update?
                    0x74, 0x03,                                         // je call
                    0x41, 0xFF, 0xC7,                                   // deferred: inc r15d
                    0xE9, 0xFB, 0xCD, 0xD8, 0xFF,                       // call: jmp +0xC1AAC                the call, in the function
                    0x85, 0xC0,                                         // post: test eax, eax
                    0x0F, 0x85, 0x09, 0xCE, 0xD8, 0xFF,                 // jnz +0xC1AC2                      error: logged and returned as before
                    0x45, 0x85, 0xFF,                                   // test r15d, r15d
                    0x0F, 0x85, 0x18, 0xCE, 0xD8, 0xFF,                 // jnz +0xC1ADA                      deferred: the store, as before
                    0xF0, 0xFF, 0x05, 0x27, 0xA3, 0x10, 0x00,           // lock inc dword [+0x43EFF0]        destroyed: counted
                    0xE9, 0x13, 0xCE, 0xD8, 0xFF,                       // jmp +0xC1AE1                      xor eax, return
                ],
                CaveSpace: 0x80, Counter: 0x43EFF0, Return: 0xC1AB4, OldReturn: 0xC1ABC, Function: 0xC19A3),
            new("linux", "libfmodstudioL.so", "8ad890c0afd8e72cefa445feeb73d486a167302dd79793cd6891bf8358cc6582", "U60 761015-762230",
                Site: 0x1A4A4D,
                Old: [0x85, 0xC0, 0x74, 0xDC],   // test eax, eax; je store
                New: [0x85, 0xC0, 0x74, 0xE6],   // test eax, eax; je past the store (xor ebp, ebp; jmp epilogue)
                Checks: [(0x1A4A04, [0x8B, 0x83, 0xF8, 0x02, 0x00, 0x00, 0xA8, 0x08, 0x74, 0x1F, 0x89, 0xC1, 0x83, 0xC9, 0x04, 0x89, 0x8B, 0xF8, 0x02, 0x00, 0x00, 0x84, 0xC0, 0x78, 0x10, 0x48, 0x8B, 0xBB, 0xE8, 0x02, 0x00, 0x00, 0x80, 0xBF, 0xA8, 0x07, 0x00, 0x00, 0x00, 0x74, 0x11, 0xC7, 0x83, 0xB0, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x31, 0xED, 0xE9, 0x60, 0xFE, 0xFF, 0xFF, 0x48, 0x81, 0xC7, 0xE8, 0x03, 0x00, 0x00, 0x48, 0x89, 0xDE, 0xE8, 0x63, 0xFD, 0x00, 0x00])]),   // the released test, the store and the destroy's call
        ];

        /// The known module for this platform, file name and hash, or why there is none.
        public static FmodStealModule? Find(string platform, string file, string sha256, out string? why)
        {
            bool named = false;
            foreach (var m in Known)
            {
                if (m.Platform != platform || !string.Equals(m.File, file, StringComparison.OrdinalIgnoreCase)) continue;
                named = true;
                if (string.Equals(m.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    why = null;
                    return m;
                }
            }
            why = named ? $"{file}'s SHA-256 {sha256} is not one it was written for" : $"no patch for {file} on {platform}";
            return null;
        }

        /// Null when the module can be patched: FMOD's version, then the bytes `read(offset,
        /// length)` gives at the site and at each check (and the cave and counter, still empty).
        /// Else why not.
        public static string? Check(FmodStealModule m, uint version, uint build, Func<int, int, byte[]> read)
        {
            if (version != Version || build != Build)
                return $"FMOD reports {version >> 16:x}.{(version >> 8) & 0xFF:x2}.{version & 0xFF:x2} build {build}, not 2.03.12 build {Build}";
            if (!read(m.Site, m.Old.Length).AsSpan().SequenceEqual(m.Old))
                return $"the bytes at +0x{m.Site:X} are not the ones it was written for";
            foreach (var (offset, bytes) in m.Checks ?? [])
                if (!read(offset, bytes.Length).AsSpan().SequenceEqual(bytes))
                    return $"the bytes at +0x{offset:X} are not the ones it was written for";
            if (m.CaveCode != null)
            {
                if (read(m.Cave, m.CaveSpace).AsSpan().ContainsAnyExcept((byte)0))
                    return $"the code section's slack at +0x{m.Cave:X} is not empty";
                if (read(m.Counter, 16).AsSpan().ContainsAnyExcept((byte)0))
                    return $"the data section's slack at +0x{m.Counter:X} is not empty";
            }
            return null;
        }

        /// The writes, in order: the cave first (so the site never jumps to nothing), then the site.
        public static (int Offset, byte[] Bytes)[] Writes(FmodStealModule m) =>
            m.CaveCode != null ? [(m.Cave, m.CaveCode), (m.Site, m.New)] : [(m.Site, m.New)];

        /// Linux: the base address and file of the FMOD Studio library among /proc/self/maps'
        /// lines (`start-end perms offset dev inode path`), the mapping of file offset 0.
        public static (ulong Base, string Path)? FromMaps(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6) continue;
                var file = parts[5].Trim();
                if (!file.EndsWith("/libfmodstudio.so", StringComparison.Ordinal) && !file.EndsWith("/libfmodstudioL.so", StringComparison.Ordinal)) continue;
                if (Convert.ToUInt64(parts[2], 16) != 0) continue;
                return (Convert.ToUInt64(parts[0].Split('-')[0], 16), file);
            }
            return null;
        }
    }
}
