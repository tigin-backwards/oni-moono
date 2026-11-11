using System.Collections.Generic;
using System.Linq;

namespace OniMoono.Desktop.Gl
{
    /// Diagnostics (ONI_GL_BATCH_PROFILE=1): the GL thread's time in each batch of commands
    /// that took 30 ms or more, by what the commands did (a draw's, a target's, an upload's by
    /// texture size and format or by mesh name, made or updated), logged after the batch with
    /// the 25 largest (`gl batch frame F took T ms: ...`). Entries indented by two spaces are
    /// parts of others (the driver calls of uploads: glTexSubImage2D, glBufferData, a copy
    /// kept for partial uploads). It times every command, so the batches take longer.
    internal static class GlBatchProfile
    {
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("ONI_GL_BATCH_PROFILE") == "1";
        const double SlowMs = 30;

        sealed class Entry { public long Count, Bytes, Ticks; }
        static readonly Dictionary<string, Entry> entries = new();

        /// GL thread.
        public static void Add(string key, long ticks, long bytes = 0)
        {
            if (!entries.TryGetValue(key, out var e)) entries[key] = e = new Entry();
            e.Count++;
            e.Bytes += bytes;
            e.Ticks += ticks;
        }

        /// A mesh upload's entry: its name with the digits left out, made or updated, and its
        /// vertex count's order of magnitude.
        public static string MeshKey(bool made, string name, int vertices)
            => $"mesh {(made ? "made" : "updated")} '{System.Text.RegularExpressions.Regex.Replace(name ?? "", "[0-9]+", "#")}' {(vertices < 1000 ? "<1k" : vertices < 10000 ? "<10k" : "10k+")} vertices";

        static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// GL thread, after a batch of `ticks`.
        public static void BatchDone(long ticks)
        {
            if (Ms(ticks) >= SlowMs)
            {
                var top = entries.OrderByDescending(p => p.Value.Ticks).Take(25)
                    .Select(p => $"{p.Key}: {Ms(p.Value.Ticks):F1} ms x{p.Value.Count}" + (p.Value.Bytes > 0 ? $" {p.Value.Bytes / 1000} KB" : ""));
                OniMoono.Engine.EngineLog.Engine($"gl batch frame {UnityEngine.Time.frameCount} took {Ms(ticks):F0} ms: " + string.Join("; ", top));
            }
            entries.Clear();
        }
    }
}
