using System.Collections.Generic;
using System.Text;

namespace OniMoono.Desktop.Gl
{
    /// Diagnostics (--frame-stats' "video memory" line): the device memory this device has
    /// asked for, by kind (textures by format, render target surfaces, mesh and stream buffers),
    /// with each kind's peak since the last report. The bytes are computed from the formats and
    /// sizes asked for (drivers pad and align on top of them). Any thread, under a lock:
    /// resources are made and freed a few at a time, not per draw.
    internal static class VideoMemoryLedger
    {
        sealed class Entry
        {
            public int Count;
            public long Bytes, Peak;
        }

        static readonly Dictionary<string, Entry> kinds = new();
        static long total, totalPeak;

        /// Adds `bytes` (negative: frees them) and `count` objects to a kind.
        public static void Add(string kind, long bytes, int count)
        {
            if (kind == null || (bytes == 0 && count == 0)) return;
            lock (kinds)
            {
                if (!kinds.TryGetValue(kind, out var e)) kinds[kind] = e = new Entry();
                e.Count += count;
                e.Bytes += bytes;
                if (e.Bytes > e.Peak) e.Peak = e.Bytes;
                total += bytes;
                if (total > totalPeak) totalPeak = total;
            }
        }

        public static long Total
        {
            get { lock (kinds) return total; }
        }

        /// The kind's bytes and object count now (tests).
        internal static (long Bytes, int Count) Of(string kind)
        {
            lock (kinds) return kinds.TryGetValue(kind, out var e) ? (e.Bytes, e.Count) : (0, 0);
        }

        /// "N MiB (peak P) asked for, kind n: x MiB (peak p), ..." with the larger kinds first
        /// (those under 1 MiB now and at their peak are left out); the peaks restart from now.
        public static string Take()
        {
            lock (kinds)
            {
                var list = new List<(string Kind, Entry E)>();
                foreach (var (k, e) in kinds) list.Add((k, e));
                list.Sort((a, b) => b.E.Peak.CompareTo(a.E.Peak));
                var text = new StringBuilder($"{Mib(total)} MiB (peak {Mib(totalPeak)}) asked for");
                foreach (var (k, e) in list)
                {
                    if (e.Peak < 1 << 20) continue;
                    text.Append(", ").Append(k).Append(' ').Append(e.Count).Append(": ").Append(Mib(e.Bytes)).Append(" MiB");
                    if (e.Peak != e.Bytes) text.Append(" (peak ").Append(Mib(e.Peak)).Append(')');
                }
                foreach (var e in kinds.Values) e.Peak = e.Bytes;
                totalPeak = total;
                return text.ToString();
            }
        }

        static string Mib(long bytes) => (bytes / 1048576.0).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

        /// Tests: forgets everything.
        internal static void Reset()
        {
            lock (kinds)
            {
                kinds.Clear();
                total = totalPeak = 0;
            }
        }
    }
}
