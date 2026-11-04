using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace OniMoono.Engine.Rendering
{
    /// Texture2D.LoadRawTextureData's copy. The game hands its five sim property textures over
    /// every frame (PropertyTextures.LateUpdate: the whole grid, 19 MB a frame on a 1192x768
    /// cluster) and then calls Apply, often with bytes equal to the texture's own (perf-wins.md,
    /// candidate 3). The copy skips the equal leading bytes and reports whether any differed;
    /// Apply then asks for no upload when none did, since the device already has those texels
    /// (TexelTextureNative.TexelsChanged). The bytes are in the texture before the call returns,
    /// as Unity's are: the caller may reuse its buffer at once.
    ///
    /// Verify mode (ONI_VERIFY_RAW_LOADS=1): each load also checks that the texture holds exactly
    /// the bytes a full copy would have made, and each Apply that skips its upload that those
    /// texels equal the ones the device was last given (a snapshot taken at every upload of a
    /// texture loaded this way). A summary every 3,000 loads ("raw texture loads: N verified,
    /// N wrong"), which qa-tools.py's verify gate fails on a non-zero count; the first 20
    /// mismatches get a line each.
    ///
    /// Diagnostics (ONI_CAPTURE_TWICE=full-uploads): each super-sized capture renders its frame
    /// again after every texture loaded this way is uploaded in full, into PATH.again.png: an
    /// upload skipped for texels the device lacks makes the two differ.
    ///
    /// Measurement only (ONI_RAW_LOADS_ALT=1, with --frame-stats): every other 60-frame window
    /// copies everything and uploads at every Apply, as before this change. With frame stats on,
    /// each window logs the loads' time ("raw texture loads (per frame over 60): ..."), ending
    /// with "variant=0" (before) or "variant=1" (this change) when alternating.
    internal static class RawTextureLoads
    {
        public static readonly bool Verify = Environment.GetEnvironmentVariable("ONI_VERIFY_RAW_LOADS") == "1";
        static readonly bool alternate = Environment.GetEnvironmentVariable("ONI_RAW_LOADS_ALT") == "1";

        /// This window runs as before the change: a full copy and an upload at every Apply.
        static bool before = alternate;

        public static bool Before => before;

        /// Makes `into` equal to `from`; whether any byte differed. The equal prefix is found by
        /// a comparison that stops at the first difference, so changed texels cost about the copy,
        /// and unchanged ones a comparison and no writes.
        public static bool CopyChanged(ReadOnlySpan<byte> from, Span<byte> into)
        {
            if (from.Length != into.Length) throw new ArgumentException("texel spans of different sizes");
            if (before)
            {
                from.CopyTo(into);
                return true;
            }
            int same = from.CommonPrefixLength(into);
            if (same == from.Length) return false;
            from[same..].CopyTo(into[same..]);
            return true;
        }

        // ----- Captures rendered again (main thread) -----

        static readonly bool recapture = Environment.GetEnvironmentVariable("ONI_CAPTURE_TWICE") == "full-uploads";
        static readonly ConditionalWeakTable<TexelTextureNative, object> loaded = new();

        public static void Register(TexelTextureNative t)
        {
            if (recapture) loaded.TryAdd(t, null);
        }

        /// Before a capture renders again: every texture loaded this way uploads in full.
        public static void UploadAll()
        {
            if (!recapture) return;
            foreach (var entry in loaded)
                if (!entry.Key.Destroyed) entry.Key.ContentChanged();
        }

        // ----- Frame stats (main thread) -----

        static long ticks, calls, unchanged;

        public static long Start() => OniMoono.Engine.PlayerLoop.FrameStats ? Stopwatch.GetTimestamp() : 0;

        public static void Loaded(long started, bool changed)
        {
            if (started == 0) return;
            ticks += Stopwatch.GetTimestamp() - started;
            calls++;
            if (!changed) unchanged++;
        }

        /// The ending window's line, or null when nothing was loaded; the next window runs the
        /// other variant when alternating.
        public static string EndWindow(int frames)
        {
            string line = null;
            if (calls > 0)
                line = $"raw texture loads (per frame over {frames}): {ticks * 1000.0 / Stopwatch.Frequency / frames:F3} ms, {(double)calls / frames:F2} calls, {(double)unchanged / frames:F2} unchanged"
                       + (alternate ? $"; variant={(before ? 0 : 1)}" : "");
            ticks = calls = unchanged = 0;
            if (alternate) before = !before;
            return line;
        }

        // ----- Verify mode (main thread) -----

        /// The texels each texture loaded this way had when the device was last told to upload it.
        static readonly ConditionalWeakTable<TexelTextureNative, byte[]> uploaded = new();
        static long verified, wrong;
        static int incidents;

        /// After a load: the texture's level 0 against the bytes handed over.
        public static void VerifyLoad(TexelTextureNative t, ReadOnlySpan<byte> data, ReadOnlySpan<byte> texels)
        {
            if (!texels.SequenceEqual(data)) Wrong(t, "the texels differ from the bytes loaded");
            uploaded.TryAdd(t, null);
            Verified();
        }

        /// At an Apply that uploads: what the device is given from now on.
        public static void VerifyUpload(TexelTextureNative t)
        {
            if (!uploaded.TryGetValue(t, out _)) return;
            uploaded.AddOrUpdate(t, t.Texels?.Span.ToArray());
        }

        /// At an Apply that skips its upload: the device's texels against the texture's.
        public static void VerifySkip(TexelTextureNative t)
        {
            if (!uploaded.TryGetValue(t, out var device)) return;
            var texels = t.Texels;
            if (device == null || texels == null || !texels.Span.SequenceEqual(device)) Wrong(t, "an upload was skipped for texels the device does not have");
            Verified();
        }

        static void Verified()
        {
            if (++verified % 3000 == 0) EngineLog.Engine(Summary());
        }

        static void Wrong(TexelTextureNative t, string what)
        {
            wrong++;
            if (incidents++ < 20) EngineLog.Engine($"raw texture loads: mismatch: {t.Name}: {what}");
        }

        public static string Summary() => $"raw texture loads: {verified} verified, {wrong} wrong";
    }
}
