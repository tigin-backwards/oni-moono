using System;
using System.IO;
using System.Linq;
using Xunit;

namespace OniMoono.Tests
{
    /// File.Replace and File.Move as U60's SteamUGCService.SwapDownloadedFile calls them (U60 QA
    /// spec L2), on our BCL, against the original player's results (tools/UnityProbe,
    /// `PROBE_SCENARIO=u60`, lines `U60 FR`, measured 2026-11-01 on U60 761211, Linux ext4). Each
    /// case gives the outcome ("ok" or the exception's type) and the folder afterwards: each
    /// file's name and content, "(ro)" when read-only. Two cases the game never reaches differ
    /// and are left out: a source that is the destination (Mono copies it to the backup and
    /// succeeds; CoreCLR throws IOException), and a missing source (both throw
    /// FileNotFoundException, but Mono has made the backup copy first).
    public sealed class MonoFileReplaceTests : IDisposable
    {
        readonly string root = Directory.CreateTempSubdirectory("oni-moono-replace-").FullName;

        public void Dispose()
        {
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, true);
        }

        string P(string f) => Path.Combine(root, f);
        void W(string f, string content) => File.WriteAllText(P(f), content);
        void RO(string f) => File.SetAttributes(P(f), FileAttributes.ReadOnly);

        string Run(Action act)
        {
            string result;
            try { act(); result = "ok"; }
            catch (Exception e) { result = e.GetType().Name; }
            var entries = Directory.GetFileSystemEntries(root).OrderBy(x => x, StringComparer.Ordinal).Select(e =>
            {
                string name = Path.GetFileName(e);
                if (Directory.Exists(e)) return name + "/";
                bool ro = (File.GetAttributes(e) & FileAttributes.ReadOnly) != 0;
                return name + "=" + File.ReadAllText(e) + (ro ? " (ro)" : "");
            });
            return result + " | " + string.Join(" ", entries);
        }

        void Replace(string backup, bool ignore = true) => File.Replace(P("tmp"), P("final"), backup == null ? null : P(backup), ignore);

        [Fact]
        public void OverAnExistingDestinationWithANewBackup()
        {
            W("tmp", "new"); W("final", "old");
            Assert.Equal("ok | final=new final.bak=old", Run(() => Replace("final.bak")));
        }

        [Fact]
        public void OverAnExistingDestinationAndAnExistingBackup()
        {
            W("tmp", "new"); W("final", "old"); W("final.bak", "older");
            Assert.Equal("ok | final=new final.bak=old", Run(() => Replace("final.bak")));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WithNoBackup(bool ignoreMetadataErrors)
        {
            W("tmp", "new"); W("final", "old");
            Assert.Equal("ok | final=new", Run(() => Replace(null, ignoreMetadataErrors)));
        }

        [Theory]
        [InlineData("final.bak")]
        [InlineData(null)]
        public void OverAMissingDestination(string backup)
        {
            W("tmp", "new");
            Assert.Equal("FileNotFoundException | tmp=new", Run(() => Replace(backup)));
        }

        [Fact]
        public void OverAReadOnlyBackup()
        {
            W("tmp", "new"); W("final", "old"); W("final.bak", "older"); RO("final.bak");
            Assert.Equal("ok | final=new final.bak=old", Run(() => Replace("final.bak")));
        }

        [Fact]
        public void OverAReadOnlyDestination()
        {
            W("tmp", "new"); W("final", "old"); RO("final");
            Assert.Equal("ok | final=new final.bak=old (ro)", Run(() => Replace("final.bak")));
        }

        [Fact]
        public void FromAReadOnlySource()
        {
            W("tmp", "new"); W("final", "old"); RO("tmp");
            Assert.Equal("ok | final=new (ro) final.bak=old", Run(() => Replace("final.bak")));
        }

        [Fact]
        public void WithAFolderAsTheBackup()
        {
            W("tmp", "new"); W("final", "old"); Directory.CreateDirectory(P("final.bak"));
            Assert.Equal("IOException | final=old final.bak/ tmp=new", Run(() => Replace("final.bak")));
        }

        [Fact]
        public void OverAFolder()
        {
            W("tmp", "new"); Directory.CreateDirectory(P("final"));
            Assert.Equal("UnauthorizedAccessException | final/ tmp=new", Run(() => Replace("final.bak")));
        }

        [Fact]
        public void MoveToANewName()
        {
            W("tmp", "new");
            Assert.Equal("ok | final=new", Run(() => File.Move(P("tmp"), P("final"))));
        }

        [Fact]
        public void MoveOverAnExistingFile()
        {
            W("tmp", "new"); W("final", "old");
            Assert.Equal("IOException | final=old tmp=new", Run(() => File.Move(P("tmp"), P("final"))));
        }

        [Fact]
        public void MoveOfAMissingFile()
        {
            Assert.Equal("FileNotFoundException | ", Run(() => File.Move(P("tmp"), P("final"))));
        }

        [Fact]
        public void MoveOfAReadOnlyFile()
        {
            W("tmp", "new"); RO("tmp");
            Assert.Equal("ok | final=new (ro)", Run(() => File.Move(P("tmp"), P("final"))));
        }
    }
}
