using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Host.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// fmod-steal-fix's gates (src/OniMoono.Host/GameTweaks/FmodStealPatch.cs): FMOD's version,
    /// the file's hash and the expected bytes must all match, or nothing is written.
    public class FmodStealPatchTests
    {
        public static TheoryData<int> Modules()
        {
            var data = new TheoryData<int>();
            for (int i = 0; i < FmodStealPatch.Known.Length; i++) data.Add(i);
            return data;
        }

        /// An image as the module's expected bytes make it: zeros elsewhere (the slack's fill).
        static (FmodStealModule Module, byte[] Image) Image(int index)
        {
            var m = FmodStealPatch.Known[index];
            int size = new[] { m.Site + m.Old.Length, m.CaveCode != null ? m.Cave + m.CaveSpace : 0, m.CaveCode != null ? m.Counter + 16 : 0 }
                .Concat((m.Checks ?? []).Select(c => c.Offset + c.Bytes.Length)).Max() + 64;
            var image = new byte[size];
            m.Old.CopyTo(image, m.Site);
            foreach (var (offset, bytes) in m.Checks ?? []) bytes.CopyTo(image, offset);
            return (m, image);
        }

        static Func<int, int, byte[]> Reader(byte[] image) => (offset, length) => image.AsSpan(offset, length).ToArray();

        [Theory]
        [MemberData(nameof(Modules))]
        public void PatchesTheModuleItWasWrittenFor(int index)
        {
            var (m, image) = Image(index);
            Assert.Same(m, FmodStealPatch.Find(m.Platform, m.File, m.Sha256, out var why));
            Assert.Null(why);
            Assert.Null(FmodStealPatch.Check(m, FmodStealPatch.Version, FmodStealPatch.Build, Reader(image)));
            foreach (var (offset, bytes) in FmodStealPatch.Writes(m)) bytes.CopyTo(image, offset);
            Assert.Equal(m.New, image.AsSpan(m.Site, m.New.Length).ToArray());
            // Patched once: the site no longer matches, so a second attempt writes nothing.
            Assert.NotNull(FmodStealPatch.Check(m, FmodStealPatch.Version, FmodStealPatch.Build, Reader(image)));
        }

        [Fact]
        public void LeavesAnUnknownFileAlone()
        {
            var m = FmodStealPatch.Known[0];
            Assert.Null(FmodStealPatch.Find(m.Platform, m.File, new string('0', 64), out var why));
            Assert.Contains("SHA-256", why);
            Assert.Null(FmodStealPatch.Find(m.Platform, "fmod.dll", m.Sha256, out why));
            Assert.Contains("no patch", why);
            Assert.Null(FmodStealPatch.Find("linux", "fmodstudio.dll", m.Sha256, out _));
        }

        /// Any changed byte the patch relies on (the site's, past it, or a check's) leaves the
        /// module untouched.
        [Theory]
        [MemberData(nameof(Modules))]
        public void LeavesAModifiedModuleAlone(int index)
        {
            var spots = new List<int>();
            var (m, _) = Image(index);
            spots.Add(m.Site);
            spots.Add(m.Site + m.Old.Length - 1);
            foreach (var (offset, bytes) in m.Checks ?? []) spots.Add(offset + bytes.Length / 2);
            foreach (var spot in spots)
            {
                var (_, image) = Image(index);
                image[spot] ^= 0xFF;
                var before = image.ToArray();
                Assert.Contains("bytes", FmodStealPatch.Check(m, FmodStealPatch.Version, FmodStealPatch.Build, Reader(image)));
                Assert.Equal(before, image);
            }
        }

        [Fact]
        public void LeavesAnotherFmodVersionAlone()
        {
            var (m, image) = Image(0);
            Assert.Contains("2.03.13", FmodStealPatch.Check(m, 0x00020313, FmodStealPatch.Build, Reader(image)));
            Assert.Contains("build", FmodStealPatch.Check(m, FmodStealPatch.Version, 160366, Reader(image)));
        }

        [Fact]
        public void NeedsTheSlackEmptyOnWindows()
        {
            int index = Array.FindIndex(FmodStealPatch.Known, k => k.CaveCode != null);
            var (m, image) = Image(index);
            image[m.Cave + 3] = 1;
            Assert.Contains("slack", FmodStealPatch.Check(m, FmodStealPatch.Version, FmodStealPatch.Build, Reader(image)));
            image[m.Cave + 3] = 0;
            image[m.Counter] = 1;
            Assert.Contains("slack", FmodStealPatch.Check(m, FmodStealPatch.Version, FmodStealPatch.Build, Reader(image)));
        }

        /// Every jump, call and RIP-relative operand of the written code, decoded, goes where the
        /// patch says: the site to the stubs and the helper (the site's original call target), the
        /// stubs back into the function (the call, the store, the error path, the return), and the
        /// counter. The only call is the site's, before its jump to the second stub, so the call's
        /// return address is `Return`, in the function; the stubs call nothing.
        [Theory]
        [MemberData(nameof(Modules))]
        public void JumpsWhereItSays(int index)
        {
            var m = FmodStealPatch.Known[index];
            if (m.CaveCode == null)
            {
                // Linux: a relative jump or branch over the same span, to inside the old bytes or
                // just past them (the epilogue's address is checked against the shipped file).
                Assert.Equal(m.New.Length, Math.Min(m.New.Length, m.Old.Length));
                return;
            }
            var site = Targets(m.New, m.Site);
            var cave = Targets(m.CaveCode, m.Cave);
            var old = Targets(m.Old, m.Site);
            int helper = old.First(t => t.Kind == "call").Target;   // the destroy helper (U60's second call logs)
            Assert.Equal(new[] { ("jmp", m.Cave), ("call", helper), ("jmp", m.Cave + m.Post) }, site.Select(t => (t.Kind, t.Target)));
            Assert.Equal(m.Return, site.Single(t => t.Kind == "call").Next);
            Assert.Equal(helper, m.Checks!.Single().Offset);
            Assert.DoesNotContain(cave, t => t.Kind == "call");
            // The pre stub ends by jumping back to the call's setup (the site's second instruction).
            var setup = site[0].Next;
            Assert.Contains(cave, t => t.Kind == "jmp" && t.Target == setup && t.At < m.Cave + m.Post);
            // The post stub: the error path or return, the store, the counter, the return.
            var post = cave.Where(t => t.At >= m.Cave + m.Post).ToArray();
            Assert.Contains(post, t => t.Kind == "rip" && t.Target == m.Counter);
            var store = cave.First(t => t.Kind == "jcc").Target;   // the pre stub's "not released"
            Assert.InRange(store, m.Site, m.Site + m.Old.Length - 1);
            Assert.All(cave.Where(t => t.Kind != "rip"), t => Assert.True(
                t.Target == setup || (t.Target >= m.Site + m.New.Length && t.Target < m.Site + m.Old.Length) || (t.Target >= m.Cave && t.Target < m.Cave + m.CaveCode.Length),
                $"+0x{t.At:X} goes to +0x{t.Target:X}"));
            Assert.True(m.CaveCode.Length <= m.CaveSpace);
            Assert.InRange(m.Return, m.Function, m.Site + m.New.Length);
            Assert.Equal(m.OldReturn, old.First(t => t.Kind == "call").Next);
            // The unwinder reads the instruction at a return address to tell an epilogue: a
            // ret, jump, pop or stack adjustment there would make it unwind the frame wrongly.
            // The new return address and the original one start the same, non-epilogue way.
            Assert.False(EpilogueLike(m.New, m.Return - m.Site), $"+0x{m.Return:X} starts like an epilogue");
            Assert.False(EpilogueLike(m.Old, m.OldReturn - m.Site));
            Assert.Equal(m.Old[m.OldReturn - m.Site], m.New[m.Return - m.Site]);
        }

        /// What x64 unwinding takes for an epilogue (or a tail jump) at code[i].
        static bool EpilogueLike(byte[] code, int i)
        {
            byte b = code[i];
            if (b is 0xC3 or 0xC2 or 0xE9 or 0xEB) return true;
            if (b == 0xF3 && code[i + 1] == 0xC3) return true;                         // rep ret
            if (b >= 0x58 && b <= 0x5F) return true;                                   // pop
            if (b >= 0x40 && b <= 0x4F && code[i + 1] >= 0x58 && code[i + 1] <= 0x5F) return true;   // REX pop
            if (b == 0xFF && (code[i + 1] & 0x38) == 0x20) return true;                 // jmp r/m
            if (b >= 0x40 && b <= 0x4F && code[i + 1] == 0xFF && (code[i + 2] & 0x38) == 0x20) return true;   // REX jmp r/m
            if (b == 0x48 && (code[i + 1] is 0x83 or 0x81) && code[i + 2] == 0xC4) return true;   // add rsp, imm
            if (b == 0x48 && code[i + 1] == 0x8D && (code[i + 2] & 0x38) == 0x20) return true;   // lea rsp, [...]
            return false;
        }

        /// The jumps, calls and RIP-relative operands of the code in `code` placed at `at`: the few
        /// encodings the patch uses.
        static List<(string Kind, int At, int Next, int Target)> Targets(byte[] code, int at)
        {
            var found = new List<(string, int, int, int)>();
            for (int i = 0; i < code.Length;)
            {
                int start = at + i;
                byte b = code[i];
                if (b == 0xE8 || b == 0xE9)
                {
                    int next = start + 5;
                    found.Add((b == 0xE8 ? "call" : "jmp", start, next, next + BitConverter.ToInt32(code, i + 1)));
                    i += 5;
                }
                else if (b == 0x0F && (code[i + 1] & 0xF0) == 0x80)
                {
                    int next = start + 6;
                    found.Add(("jcc", start, next, next + BitConverter.ToInt32(code, i + 2)));
                    i += 6;
                }
                else if (b is 0x74 or 0x75 or 0xEB)
                {
                    int next = start + 2;
                    found.Add(("jcc", start, next, next + (sbyte)code[i + 1]));
                    i += 2;
                }
                else if (b == 0xF0 && code[i + 1] == 0xFF && code[i + 2] == 0x05)
                {
                    int next = start + 7;
                    found.Add(("rip", start, next, next + BitConverter.ToInt32(code, i + 3)));
                    i += 7;
                }
                else i += Length(code, i);
            }
            return found;
        }

        /// The length of the other instructions the patch's code and sites use.
        static int Length(byte[] code, int i) => code[i] switch
        {
            0xF6 or 0x80 => 7,                  // test/cmp byte [reg+disp32], imm8
            0x8B when code[i + 1] == 0x83 => 6,  // mov eax, [rbx+disp32]
            0x89 when code[i + 1] == 0xAB => 6,  // mov [rbx+disp32], ebp
            0x44 when code[i + 1] == 0x89 => 7,  // mov [rbx+disp32], r14d
            0x48 when code[i + 1] == 0x8B && code[i + 2] == 0x83 => 7,   // mov rax, [rbx+disp32]
            0x48 when code[i + 1] == 0x8D => 7,  // lea rdx, [rip+disp32]
            0x48 when code[i + 2] == 0x7C => 5,  // mov rdi, [rsp+disp8]
            0x4C => 5,                           // mov r14/r15, [rsp+disp8]
            0x41 when code[i + 1] == 0xB8 => 6,  // mov r8d, imm32
            0x41 or 0x45 or 0x48 => 3,          // inc/xor/test r15d; mov rcx, rbx
            0xC1 => 3,                           // shr eax, imm8
            0xCC or 0xC3 or 0x5B or 0x5D or 0x5F => 1,
            _ => 2,                              // xor/test/mov reg, reg; inc edi; test al, imm8
        };

        [Fact]
        public void FindsTheLibraryInTheMaps()
        {
            var maps = new[]
            {
                "7f0000000000-7f0000001000 r--p 00000000 08:30 1 /usr/lib/libc.so.6",
                "7f1000001000-7f1000200000 r-xp 00001000 08:30 2 /game/Plugins/x86_64/libfmodstudioL.so",
                "7f1000000000-7f1000001000 r--p 00000000 08:30 2 /game/Plugins/x86_64/libfmodstudioL.so",
            };
            Assert.Equal((0x7f1000000000UL, "/game/Plugins/x86_64/libfmodstudioL.so"), FmodStealPatch.FromMaps(maps));
            Assert.Null(FmodStealPatch.FromMaps(maps.Take(1)));
        }

        /// Every frozen build's x86-64 FMOD Studio library is one listed, with the listed bytes
        /// where they are expected.
        [Fact]
        public void MatchesTheShippedFiles()
        {
            var builds = Environment.GetEnvironmentVariable("ONI_TWEAK_BUILDS") ?? "/mnt/e/oni-builds";
            if (!Directory.Exists(builds)) Assert.Skip($"no frozen game builds at {builds}");
            var paths = Directory.GetDirectories(builds, "oni-*").SelectMany(dir => new[] { "OxygenNotIncluded_Data/Plugins", "OxygenNotIncluded_Data/Plugins/x86_64" }
                .SelectMany(p => FmodStealPatch.Known.Select(k => (Dir: dir, Path: Path.Combine(dir, p, k.File)))));
            int seen = 0;
            foreach (var (dir, path) in paths)
            {
                if (!File.Exists(path)) continue;
                var name = Path.GetFileName(dir);
                var platform = name.StartsWith("oni-win-") ? "windows" : name.StartsWith("oni-linux-") ? "linux" : null;
                if (platform == null) continue;
                var m = FmodStealPatch.Known.First(k => k.Platform == platform && k.File == Path.GetFileName(path));
                var file = File.ReadAllBytes(path);
                Assert.Equal(m.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file)));
                Assert.Equal(m.Old, file.AsSpan(FileOffset(file, m.Platform, m.Site), m.Old.Length).ToArray());
                foreach (var (offset, bytes) in m.Checks ?? [])
                    Assert.Equal(bytes, file.AsSpan(FileOffset(file, m.Platform, offset), bytes.Length).ToArray());
                if (m.CaveCode != null)
                {
                    // The cave and the counter are in the slack of the code and data sections:
                    // past each one's bytes, inside its last page.
                    Assert.Equal(".text", Section(file, m.Cave, out bool cavePast));
                    Assert.True(cavePast);
                    Assert.Equal(".data", Section(file, m.Counter, out bool counterPast));
                    Assert.True(counterPast);
                }
                seen++;
            }
            if (seen == 0) Assert.Skip($"no FMOD Studio library in the frozen builds at {builds}");
        }

        /// The PE section whose pages hold `rva`, and whether it is past the section's own size.
        static string Section(byte[] file, int rva, out bool pastSize)
        {
            int pe = BitConverter.ToInt32(file, 0x3C), sections = BitConverter.ToUInt16(file, pe + 6), optional = BitConverter.ToUInt16(file, pe + 20);
            for (int i = 0; i < sections; i++)
            {
                int o = pe + 24 + optional + 40 * i;
                int size = BitConverter.ToInt32(file, o + 8), va = BitConverter.ToInt32(file, o + 12);
                int pages = (size + 0xFFF) & ~0xFFF;
                if (rva >= va && rva + 16 <= va + pages)
                {
                    pastSize = rva >= va + size;
                    return System.Text.Encoding.ASCII.GetString(file, o, 8).TrimEnd('\0');
                }
            }
            pastSize = false;
            return "";
        }

        /// A virtual offset's place in the file: PE sections, or ELF loadable segments.
        static int FileOffset(byte[] file, string platform, int rva)
        {
            if (platform == "windows")
            {
                int pe = BitConverter.ToInt32(file, 0x3C), sections = BitConverter.ToUInt16(file, pe + 6), optional = BitConverter.ToUInt16(file, pe + 20);
                for (int i = 0; i < sections; i++)
                {
                    int o = pe + 24 + optional + 40 * i;
                    int size = BitConverter.ToInt32(file, o + 8), va = BitConverter.ToInt32(file, o + 12), raw = BitConverter.ToInt32(file, o + 20);
                    if (rva >= va && rva < va + size) return raw + rva - va;
                }
            }
            else
            {
                long phoff = BitConverter.ToInt64(file, 0x20);
                int size = BitConverter.ToUInt16(file, 0x36), count = BitConverter.ToUInt16(file, 0x38);
                for (int i = 0; i < count; i++)
                {
                    int o = (int)phoff + i * size;
                    if (BitConverter.ToUInt32(file, o) != 1) continue;
                    long offset = BitConverter.ToInt64(file, o + 8), va = BitConverter.ToInt64(file, o + 16), filesz = BitConverter.ToInt64(file, o + 32);
                    if (rva >= va && rva < va + filesz) return (int)(offset + rva - va);
                }
            }
            throw new InvalidDataException($"+0x{rva:X} is in no section");
        }
    }
}
