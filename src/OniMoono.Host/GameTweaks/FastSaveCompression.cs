using System.IO;
using System.IO.Compression;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `fast-save-compression` (opt-in, `--game-tweak`): the game compresses and
    /// decompresses saves with Ionic.Zlib, a managed deflate; this uses .NET's zlib instead. Both
    /// write the zlib format (RFC 1950), so saves stay readable by the original game and the
    /// original's saves by this one (checked both ways on a 690 MB colony). On that colony saving
    /// compresses in 1.3–1.5 s instead of 5.0–5.5 s and loading decompresses in 1.1–1.5 s instead
    /// of 2.5–3.7 s; the file is about 14% larger.
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void SaveLoader::CompressContents(System.IO.BinaryWriter,System.Byte[],System.Int32)", "7027ce9ea9fd4d7b linux-744825 win-744825")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Byte[] SaveLoader::DecompressContents(System.Byte[])", "7f27a2ec6323e63c linux-744825 win-744825")]
    internal static class FastSaveCompression
    {
        public const string Name = GameTweakNames.FastSaveCompression;

        /// `compress`: false when background-save does the compressing (with this zlib).
        public static void Install(Harmony harmony, bool compress)
        {
            // Mods' prefixes run first (Better Save takes the body itself).
            if (compress) harmony.Patch(AccessTools.Method("SaveLoader:CompressContents"), prefix: new HarmonyMethod(typeof(FastSaveCompression), nameof(Compress)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method("SaveLoader:DecompressContents"), prefix: new HarmonyMethod(typeof(FastSaveCompression), nameof(Decompress)));
        }

        /// SaveLoader.CompressContents(BinaryWriter, byte[], int): the compressed body follows the
        /// header already written, and the file is closed when the stream is, as before. A writer
        /// that is not the game's file (Better Save's, into memory) is left to the game.
        static bool Compress(BinaryWriter fileWriter, byte[] uncompressed, int length)
        {
            if (fileWriter.BaseStream is not FileStream) return true;
            using var zlib = new ZLibStream(fileWriter.BaseStream, CompressionLevel.Fastest);
            zlib.Write(uncompressed, 0, length);
            return false;
        }

        /// SaveLoader.DecompressContents(byte[]): the save's body.
        static bool Decompress(byte[] compressed, ref byte[] __result)
        {
            using var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var output = new MemoryStream((int)System.Math.Min((long)compressed.Length * 4, System.Array.MaxLength));
            zlib.CopyTo(output);
            __result = output.ToArray();
            return false;
        }
    }
}
