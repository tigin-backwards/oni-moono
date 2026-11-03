using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine
{
    /// Decoded images kept while it is open, for LoadImage: the same file's bytes loaded again
    /// get the texels decoded the first time, copied into the texture as a decode would leave
    /// them (the texture is still the caller's own, its mips built and its readability set as
    /// for a decode). It is keyed by the data itself: an entry is used only for bytes equal to
    /// the ones it was decoded from, so what LoadImage makes of them is the same by
    /// construction. Failed decodes are not kept.
    ///
    /// The Load screen keeps it open while it is shown (the fast-load-screen game tweak): each
    /// refresh (Load more) decodes the previews of every page shown again. Main thread only, as
    /// LoadImage is; at most Budget bytes are kept (later images are decoded as usual), all
    /// dropped when it closes.
    internal static class ImageDecodeMemo
    {
        public const long Budget = 512L << 20;

        sealed class Entry
        {
            public byte[] Data;
            public TextureFormat Format;
            public int Width, Height;
            public byte[] Texels;
        }

        static readonly Dictionary<long, List<Entry>> entries = new();
        static long kept;
        static bool open;

        public static int Hits { get; private set; }
        public static int Decodes { get; private set; }

        public static bool IsOpen => open;

        public static void Open()
        {
            open = true;
            Hits = Decodes = 0;
        }

        public static void Close()
        {
            open = false;
            entries.Clear();
            kept = 0;
        }

        /// The texels decoded from these very bytes, if kept.
        public static bool TryGet(byte[] data, out TextureFormat format, out int width, out int height, out byte[] texels)
        {
            format = default;
            width = height = 0;
            texels = null;
            if (!open || !entries.TryGetValue(Key(data), out var list)) return false;
            foreach (var e in list)
                if (e.Data.AsSpan().SequenceEqual(data))
                {
                    Hits++;
                    (format, width, height, texels) = (e.Format, e.Width, e.Height, e.Texels);
                    return true;
                }
            return false;
        }

        /// A decode of `data`: its level-0 texels, kept while the memo is open and under budget.
        public static void Keep(byte[] data, TextureFormat format, int width, int height, ReadOnlySpan<byte> texels)
        {
            if (!open) return;
            Decodes++;
            long size = data.Length + texels.Length;
            if (kept + size > Budget) return;
            long key = Key(data);
            if (!entries.TryGetValue(key, out var list)) entries[key] = list = new List<Entry>(1);
            list.Add(new Entry { Data = (byte[])data.Clone(), Format = format, Width = width, Height = height, Texels = texels.ToArray() });
            kept += size;
        }

        /// A cheap key: the length and 64 bytes spread over the data (equal data, equal key; the
        /// bytes themselves are compared on a match).
        public static long Key(byte[] data)
        {
            var h = new HashCode();
            h.Add(data.Length);
            int n = data.Length;
            for (int i = 0; i < 64 && n > 0; i++) h.Add(data[(int)((long)n * i / 64)]);
            if (n > 0) h.Add(data[n - 1]);
            return (uint)h.ToHashCode() | (long)n << 32;
        }
    }
}
