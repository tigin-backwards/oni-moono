using System;
using System.IO;
using System.IO.Compression;
using OniMoono.Host.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The background-save game tweak (src/OniMoono.Host/GameTweaks/BackgroundSave.cs, compiled
    /// in here) with saves overlapping: an autosave's write still running when the next save
    /// starts (a save from the pause menu, Save and Quit, the next autosave). The game saves one
    /// at a time on its main thread, so each save finds the one before it complete on disk; with
    /// the tweak, SaveLoader.Save waits for the writes in flight first (its prefix, WaitAll), and
    /// every save is written whole, in order.
    [Collection("Console")]
    public class BackgroundSaveTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), $"background-save-{Guid.NewGuid():N}");

        public BackgroundSaveTests() => Directory.CreateDirectory(dir);

        public void Dispose()
        {
            BackgroundSave.WaitAll();
            Directory.Delete(dir, true);
        }

        /// What SaveLoader.Save does with the tweak: its prefix waits for the writes in flight, it
        /// opens the target (FileMode.Create), writes the header and hands the body to
        /// CompressContents, which the tweak replaces; then the file is closed.
        static void Save(string path, byte[] header, byte[] body)
        {
            BackgroundSave.WaitAll();
            using var writer = new BinaryWriter(File.Open(path, FileMode.Create));
            writer.Write(header);
            Assert.False(BackgroundSave.Compress(writer, body, body.Length));
        }

        /// Better Save swaps the game's file for a MemoryStream and writes the save itself: the
        /// tweak leaves such a writer to the method's other patches and the game.
        [Fact]
        public void AWriterIntoMemoryIsLeftToTheGame()
        {
            using var writer = new BinaryWriter(new MemoryStream());
            writer.Write(Header("header"));
            var body = Body(1, 1024);
            Assert.True(BackgroundSave.Compress(writer, body, body.Length));
            Assert.Equal(6, writer.BaseStream.Length);
        }

        /// A colony's serialized body: compressible, as a save is, and different per seed.
        static byte[] Body(int seed, int length = 24 << 20)
        {
            var body = new byte[length];
            var random = new Random(seed);
            for (int i = 0; i < length; i += 64)
                body[i + random.Next(64)] = (byte)random.Next(256);
            return body;
        }

        static byte[] Header(string text) => System.Text.Encoding.ASCII.GetBytes(text);

        /// The file holds the header, then the body as zlib (RFC 1950), as the game writes it.
        static void AssertSaved(string path, byte[] header, byte[] body)
        {
            var file = File.ReadAllBytes(path);
            Assert.Equal(header, file.AsSpan(0, header.Length).ToArray());
            using var zlib = new ZLibStream(new MemoryStream(file, header.Length, file.Length - header.Length), CompressionMode.Decompress);
            var read = new MemoryStream();
            zlib.CopyTo(read);
            Assert.True(body.AsSpan().SequenceEqual(read.ToArray()), $"{path}: the body differs");
            Assert.False(File.Exists(path + ".writing"));
        }

        [NeedsGameAssembly("Ionic.Zip")]
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASaveStartedDuringAnAutosavesWriteWaitsForIt(bool fastCompression)
        {
            BackgroundSave.fast = fastCompression;
            string autosave = Path.Combine(dir, "Colony Cycle 12.sav"), manual = Path.Combine(dir, "Colony.sav");
            byte[] autoBody = Body(1), manualBody = Body(2);
            Save(autosave, Header("autosave"), autoBody);
            // Straight after it, as a save from the pause menu right after an autosave.
            Save(manual, Header("manual"), manualBody);
            // The second save began only once the first was on disk.
            AssertSaved(autosave, Header("autosave"), autoBody);
            BackgroundSave.WaitAll();
            AssertSaved(manual, Header("manual"), manualBody);
        }

        [NeedsGameAssembly("Ionic.Zip")]
        [Fact]
        public void TheSameFileSavedTwiceInARowHoldsTheSecondSave()
        {
            BackgroundSave.fast = false;
            string path = Path.Combine(dir, "Colony.sav");
            byte[] first = Body(3), second = Body(4);
            Save(path, Header("first"), first);
            Save(path, Header("second"), second);
            BackgroundSave.WaitAll();
            AssertSaved(path, Header("second"), second);
        }

        [NeedsGameAssembly("Ionic.Zip")]
        [Fact]
        public void AFailedWriteLeavesTheNextSaveWhole()
        {
            BackgroundSave.fast = true;
            string failing = Path.Combine(dir, "Failing.sav"), next = Path.Combine(dir, "Next.sav");
            // The write's temporary file cannot be created.
            Directory.CreateDirectory(failing + ".writing");
            byte[] body = Body(5, 1 << 20);
            Save(failing, Header("failing"), body);
            Save(next, Header("next"), body);
            BackgroundSave.WaitAll();
            AssertSaved(next, Header("next"), body);
            // The failed save keeps the header the game wrote, as the target holds until the write.
            Assert.Equal(Header("failing"), File.ReadAllBytes(failing));
        }

        /// A finished save that cannot take the save's place (another program holds the save, as an
        /// antivirus scan or a cloud client can on Windows) is kept, never deleted: here the target
        /// is a folder, so neither the move nor the copy goes through.
        [Fact]
        public void ACompleteSaveThatCannotReplaceTheTargetIsKept()
        {
            string temporary = Path.Combine(dir, "Kept.sav.writing"), target = Path.Combine(dir, "Kept.sav");
            File.WriteAllBytes(temporary, [1, 2, 3]);
            Directory.CreateDirectory(target);
            Assert.False(BackgroundSave.Replace(temporary, target, waitMilliseconds: 50));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(temporary));
            // Once the target is free, the same call moves the save into place.
            Directory.Delete(target);
            Assert.True(BackgroundSave.Replace(temporary, target, waitMilliseconds: 50));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
            Assert.False(File.Exists(temporary));
        }
    }
}
