#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OniMoono.Host.GameTweaks
{
    /// What each phase of a save's last load took (LoadProgress's five), by the save's path and
    /// size, kept in `oni-moono/load-times.json` beside the settings so the loading screen's bar
    /// (game tweak loading-animation) moves about evenly from a save's second load on. At most
    /// `Capacity` saves, the least recently loaded dropped first. Read once at start-up; written
    /// on a worker after a load, never during one. A file that does not parse is started anew.
    internal sealed class LoadTimes
    {
        public const int Capacity = 32;

        readonly string path;
        // Most recently loaded last.
        readonly List<(string Key, double[] Seconds)> entries = new();
        readonly object gate = new();

        public LoadTimes(string path)
        {
            this.path = path;
            try
            {
                if (!File.Exists(path)) return;
                var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (root?["saves"] is not JsonArray saves) return;
                foreach (var item in saves)
                {
                    if (item?["key"]?.GetValue<string>() is not { } key || item["seconds"] is not JsonArray s) continue;
                    var seconds = s.Select(v => v?.GetValue<double>() ?? -1).ToArray();
                    if (Valid(seconds)) entries.Add((key, seconds));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
            {
                entries.Clear();
            }
        }

        /// A save's key: its path and size (a save rewritten in place to another size is
        /// another load's profile).
        public static string Key(string savePath, long size) => $"{size}|{savePath}";

        static bool Valid(double[] seconds) => seconds.Length == Engine.LoadProgress.Phases && seconds.All(v => v >= 0 && v < 3600) && seconds.Sum() > 0;

        /// The phases' seconds the last load of `key` took; null: none kept.
        public double[]? Find(string key)
        {
            lock (gate)
                foreach (var (k, s) in entries)
                    if (k == key) return (double[])s.Clone();
            return null;
        }

        /// Keeps a load's seconds for `key` (the most recent), and the file's text to write.
        public string Record(string key, double[] seconds)
        {
            lock (gate)
            {
                if (Valid(seconds))
                {
                    entries.RemoveAll(e => e.Key == key);
                    entries.Add((key, (double[])seconds.Clone()));
                    while (entries.Count > Capacity) entries.RemoveAt(0);
                }
                var saves = new JsonArray();
                foreach (var (k, s) in entries)
                    saves.Add(new JsonObject { ["key"] = k, ["seconds"] = new JsonArray(s.Select(v => (JsonNode)Math.Round(v, 3)).ToArray()) });
                return new JsonObject { ["saves"] = saves }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
        }

        /// Writes the text Record gave (a worker thread); failures are logged, never thrown.
        public void Write(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Engine.EngineLog.Engine($"loading animation: the load times were not kept: {e.Message}");
            }
        }
    }
}
