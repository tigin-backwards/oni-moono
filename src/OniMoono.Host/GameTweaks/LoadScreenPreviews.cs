#nullable disable
using System;
using System.Collections.Generic;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Host.GameTweaks
{
    /// The engine side of game tweak `fast-load-screen` (OniMoono.GameTweaks.FastLoadScreen):
    /// while the Load screen is shown, the previews of pages already shown are not made again
    /// from their files at each refresh (Load more).
    ///
    /// - LoadImage keeps what it decodes (ImageDecodeMemo): the same file's bytes loaded again
    ///   get the same texels without a decode (U59's previews, and U60's up to 1024 pixels).
    /// - U60 draws a preview over 1024 pixels into a render texture at its new size and reads it
    ///   back (RetireColonyUtility.LoadPreviewTexture's DownscaleToFit: Graphics.Blit, then
    ///   ReadPixels into a new RGBA32 texture made unreadable); the read-back waits for the GPU,
    ///   about 12 ms a preview in ours. The read-back texels are kept by the file's bytes, and
    ///   the same bytes again get a new texture made as DownscaleToFit makes it
    ///   (`new Texture2D(w, h, RGBA32, false)`, Apply(false, true)) holding those texels.
    ///
    /// Both are keyed by the bytes themselves (compared whole on a match): each preview is still
    /// read from its file, and still becomes a new texture and sprite of the game's own, which
    /// the game destroys as before. Everything kept is dropped when the screen closes.
    internal static class LoadScreenPreviews
    {
        public const string Name = "fast-load-screen";
        const long Budget = 256L << 20;

        sealed class Downscaled
        {
            public byte[] Bytes;
            public int Width, Height;
            public byte[] Texels;
        }

        static readonly Dictionary<long, List<Downscaled>> downscaled = new();
        static long kept;
        static int hits, verified, differed;
        static bool bypass;
        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static System.Reflection.MethodInfo load;

        public static void Install(Harmony harmony)
        {
            var screen = AccessTools.TypeByName("LoadScreen");
            harmony.Patch(AccessTools.Method(screen, "OnActivate"), prefix: new HarmonyMethod(typeof(LoadScreenPreviews), nameof(Open)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(screen, "OnDeactivate"), postfix: new HarmonyMethod(typeof(LoadScreenPreviews), nameof(Close)) { priority = Priority.Last });
            // U60's previews (U59 has no LoadPreviewTexture: its previews are LoadImage's alone).
            load = AccessTools.Method(AccessTools.TypeByName("RetireColonyUtility"), "LoadPreviewTexture", [typeof(byte[])]);
            if (load != null && load.ReturnType == typeof(Texture2D))
                harmony.Patch(load, prefix: new HarmonyMethod(typeof(LoadScreenPreviews), nameof(LoadPreview)) { priority = Priority.Last },
                    postfix: new HarmonyMethod(typeof(LoadScreenPreviews), nameof(LoadedPreview)) { priority = Priority.First });
        }

        /// While this screen is up: loading a save destroys it without OnDeactivate, and nothing
        /// is kept past it (the memo checks the screen at each use).
        static void Open(MonoBehaviour __instance)
        {
            var screen = __instance;
            ImageDecodeMemo.Open(() => screen != null && screen.isActiveAndEnabled, Dropped);
        }

        static void Close() => ImageDecodeMemo.Close();

        static void Dropped()
        {
            if (Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1")
                EngineLog.Engine($"{Name}: {ImageDecodeMemo.Hits} images from the decode memo, {ImageDecodeMemo.Decodes} decoded; {hits} downscaled previews kept from earlier, {verified} checked against the game's, {differed} differed");
            downscaled.Clear();
            kept = 0;
            hits = verified = differed = 0;
        }

        static Downscaled Find(byte[] bytes)
        {
            if (bytes == null || !downscaled.TryGetValue(ImageDecodeMemo.Key(bytes), out var list)) return null;
            foreach (var d in list)
                if (d.Bytes.AsSpan().SequenceEqual(bytes)) return d;
            return null;
        }

        /// The same bytes downscaled before: a new texture as DownscaleToFit makes it.
        static bool LoadPreview(byte[] bytes, ref Texture2D __result, bool __runOriginal)
        {
            if (bypass || !__runOriginal || !ImageDecodeMemo.IsOpen || Find(bytes) is not { } d) return true;
            var texture = new Texture2D(d.Width, d.Height, TextureFormat.RGBA32, false);
            d.Texels.CopyTo(((Texture2DNative)texture.NativeOrThrow).Memory.Span);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            __result = texture;
            hits++;
            if (verify) Check(bytes, d);
            return false;
        }

        /// ONI_TWEAK_VERIFY: the game's own LoadPreviewTexture of the same bytes reads back the
        /// same texels.
        static void Check(byte[] bytes, Downscaled d)
        {
            verified++;
            bypass = true;
            try
            {
                var game = (Texture2D)load.Invoke(null, [bytes]);
                var native = (Texture2DNative)game.NativeOrThrow;
                var texels = native.HasTexels ? native.Memory : null;
                if (texels == null || game.width != d.Width || game.height != d.Height || !texels.Span.SequenceEqual(d.Texels))
                {
                    if (differed++ < 5) EngineLog.Engine($"{Name}: a downscaled preview differs from the game's ({game.width}x{game.height}, texels {(texels == null ? "gone" : "differ")})");
                }
                UnityEngine.Object.Destroy(game);
            }
            finally
            {
                bypass = false;
            }
        }

        /// A downscaled preview (DownscaleToFit's RGBA32 texture; LoadImage's are ARGB32): its
        /// texels, read back once.
        static void LoadedPreview(byte[] bytes, Texture2D __result, bool __runOriginal)
        {
            if (bypass || !__runOriginal || !ImageDecodeMemo.IsOpen || bytes == null || __result == null || __result.format != TextureFormat.RGBA32) return;
            if (Find(bytes) != null) return;
            var native = (Texture2DNative)__result.NativeOrThrow;
            if (native.MipCount != 1 || !native.HasTexels) return;
            var texels = native.Memory;
            if (texels == null) return;
            long size = bytes.Length + texels.Size;
            if (kept + size > Budget) return;
            long key = ImageDecodeMemo.Key(bytes);
            if (!downscaled.TryGetValue(key, out var list)) downscaled[key] = list = new List<Downscaled>(1);
            list.Add(new Downscaled { Bytes = (byte[])bytes.Clone(), Width = native.Width, Height = native.Height, Texels = texels.Span.ToArray() });
            kept += size;
        }
    }
}
