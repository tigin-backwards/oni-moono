#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OniMoono.Host
{
    /// Where an option's effective value came from.
    internal enum OptionSource { Default, SettingsFile, CommandLine }

    /// The player's own settings file, written by the main menu's Moono Tweaks screen:
    /// `<persistent data path>/oni-moono/settings.json`, beside the game's own data in the player's
    /// per-user folder (a test run's run home has its own, so it never reads the user's; updating
    /// the player never touches it). It records only the choices that differ from the built-in
    /// defaults, so a default that changes in an update (a new tweak among them) reaches every user
    /// who has not chosen otherwise:
    ///
    ///     { "gameTweaks": { "brain-rate": true, "fast-jobs": false }, "splash": "moo", "frameStats": true, "logPaths": "full", "gpu": "discrete", "renderer": "opengl",
    ///       "dxgi": false, "updateCheck": false, "gc": "low-memory" }
    ///
    /// Tweaks no longer known (one removed in an update) are ignored and kept.
    internal sealed class PlayerSettings
    {
        /// The tweaks chosen on or off, by name; a tweak not named has its default.
        public readonly SortedDictionary<string, bool> GameTweaks = new(StringComparer.Ordinal);
        /// "moo" or "none"; null when not chosen.
        public string? Splash;
        public bool? FrameStats;
        /// "hidden" or "full"; null when not chosen.
        public string? LogPaths;
        /// "default", "discrete" or "integrated" (GpuChoice); null when not chosen.
        public string? Gpu;
        /// "auto", "vulkan" or "opengl" (OniMoono.Desktop.Renderers); null when not chosen.
        public string? Renderer;
        /// Windows: presenting through DXGI (OniMoono.Desktop.Gl.DxgiTarget); null when not chosen.
        public bool? Dxgi;
        /// The main menu's update check (UpdateCheck); null when not chosen.
        public bool? UpdateCheck;
        /// "fast" or "low-memory" (GcMode); null when not chosen. OniMoono.exe reads it too.
        public string? Gc;

        public static string PathIn(string persistentDataPath) => Path.Combine(persistentDataPath, "oni-moono", "settings.json");
        /// Where players from before the Moono name (oni-brutal) kept the file.
        public static string OldPathIn(string persistentDataPath) => Path.Combine(persistentDataPath, "oni-brutal", "settings.json");

        /// The file from before the Moono name, copied to its place once: when there is no file
        /// at PathIn and there is one at OldPathIn (left where it was, for an older release).
        /// Returns the old file's path when it was copied, else null; a copy that fails leaves
        /// the reason in `problem` (and defaults then apply, as without a file).
        public static string? MigrateIn(string persistentDataPath, out string? problem)
        {
            problem = null;
            string path = PathIn(persistentDataPath), old = OldPathIn(persistentDataPath);
            if (File.Exists(path) || !File.Exists(old)) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(old, path);
                return old;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problem = e.Message;
                return null;
            }
        }

        /// The file at `path`: empty when there is none. A file that cannot be read or parsed counts
        /// as empty too, with the reason in `problem` (it is not overwritten until the screen saves).
        public static PlayerSettings Read(string path, out string? problem)
        {
            problem = null;
            var settings = new PlayerSettings();
            if (!File.Exists(path)) return settings;
            try
            {
                return Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
            {
                problem = e.Message;
                return settings;
            }
        }

        public static PlayerSettings Parse(string json)
        {
            var settings = new PlayerSettings();
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                       ?? throw new FormatException("the settings file is not a JSON object");
            if (root["gameTweaks"] is JsonObject tweaks)
                foreach (var (name, value) in tweaks)
                    settings.GameTweaks[name] = value?.GetValue<bool>() ?? throw new FormatException($"gameTweaks.{name} is null");
            if (root["splash"] is JsonNode splash)
            {
                var value = splash.GetValue<string>();
                settings.Splash = IsSplash(value) ? value : throw new FormatException($"splash \"{value}\" is not one of: moo, none");
            }
            if (root["frameStats"] is JsonNode frameStats) settings.FrameStats = frameStats.GetValue<bool>();
            if (root["logPaths"] is JsonNode logPaths)
            {
                var value = logPaths.GetValue<string>();
                settings.LogPaths = IsLogPaths(value) ? value : throw new FormatException($"logPaths \"{value}\" is not one of: hidden, full");
            }
            if (root["gpu"] is JsonNode gpu)
            {
                var value = gpu.GetValue<string>();
                settings.Gpu = GpuChoice.IsChoice(value) ? value : throw new FormatException($"gpu \"{value}\" is not one of: {string.Join(", ", GpuChoice.Choices)}");
            }
            if (root["renderer"] is JsonNode renderer)
            {
                var value = renderer.GetValue<string>();
                settings.Renderer = OniMoono.Desktop.Renderers.IsChoice(value) ? value
                    : throw new FormatException($"renderer \"{value}\" is not one of: {string.Join(", ", OniMoono.Desktop.Renderers.Choices)}");
            }
            if (root["dxgi"] is JsonNode dxgi) settings.Dxgi = dxgi.GetValue<bool>();
            if (root["updateCheck"] is JsonNode updateCheck) settings.UpdateCheck = updateCheck.GetValue<bool>();
            if (root["gc"] is JsonNode gc)
            {
                var value = gc.GetValue<string>();
                settings.Gc = GcMode.IsChoice(value) ? value : throw new FormatException($"gc \"{value}\" is not one of: {string.Join(", ", GcMode.Choices)}");
            }
            return settings;
        }

        /// The file's text (indented); `indented: false` gives one line, for the log.
        public string ToJson(bool indented = true)
        {
            var root = new JsonObject();
            if (GameTweaks.Count > 0)
            {
                var tweaks = new JsonObject();
                foreach (var (name, on) in GameTweaks) tweaks[name] = on;
                root["gameTweaks"] = tweaks;
            }
            if (Splash != null) root["splash"] = Splash;
            if (FrameStats != null) root["frameStats"] = FrameStats;
            if (LogPaths != null) root["logPaths"] = LogPaths;
            if (Gpu != null) root["gpu"] = Gpu;
            if (Renderer != null) root["renderer"] = Renderer;
            if (Dxgi != null) root["dxgi"] = Dxgi;
            if (UpdateCheck != null) root["updateCheck"] = UpdateCheck;
            if (Gc != null) root["gc"] = Gc;
            return indented ? root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n" : root.ToJsonString();
        }

        /// Writes the file whole: to a temporary file beside it, then moved over it, so a crash
        /// mid-write leaves the previous file.
        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, ToJson(), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }

        public static bool IsSplash(string value) => value is "moo" or "none";
        public static bool IsLogPaths(string value) => value is "hidden" or "full";
    }

    /// The options the Moono Tweaks screen sets, as this run has them: built-in defaults, then the
    /// settings file, then the command line. A command line naming any game tweak
    /// (`--game-tweak NAME|-NAME|all|exact|none`, repeatable) sets the whole set of tweaks for the
    /// run, as before the settings file existed; `--splash`, `--frame-stats`, `--log-paths`,
    /// `--gpu`, `--dxgi` and `--gc` set theirs.
    internal sealed class EffectiveOptions
    {
        public readonly IReadOnlyList<string> AllTweaks;
        public readonly Dictionary<string, (bool On, OptionSource Source)> Tweaks = new(StringComparer.Ordinal);
        /// The splash without a settings file or command line saying otherwise: the Gassy Moos.
        public const string DefaultSplash = "moo";
        public string Splash = DefaultSplash;
        public OptionSource SplashSource;
        public bool FrameStats;
        public OptionSource FrameStatsSource;
        /// Personal paths in Player.log and the console: "hidden" (LogScrubber) unless chosen
        /// otherwise, or "full".
        public const string DefaultLogPaths = "hidden";
        public string LogPaths = DefaultLogPaths;
        public OptionSource LogPathsSource;
        public bool HideLogPaths => LogPaths == "hidden";
        /// The GPU choice (GpuChoice) without a settings file or command line saying otherwise:
        /// what the original does.
        public const string DefaultGpu = GpuChoice.Default;
        public string Gpu = DefaultGpu;
        public OptionSource GpuSource;
        /// The renderer without a settings file or command line saying otherwise: Vulkan where
        /// it starts, else OpenGL.
        public const string DefaultRenderer = OniMoono.Desktop.Renderers.Auto;
        public string Renderer = DefaultRenderer;
        public OptionSource RendererSource;
        /// Windows: present through DXGI (on unless chosen otherwise; it falls back to OpenGL's
        /// presentation by itself when it can't start).
        public const bool DefaultDxgi = true;
        public bool Dxgi = DefaultDxgi;
        public OptionSource DxgiSource;
        /// The main menu's update check (on unless chosen otherwise; no launch option).
        public const bool DefaultUpdateCheck = true;
        public bool UpdateCheck = DefaultUpdateCheck;
        public OptionSource UpdateCheckSource;
        /// The garbage collection (GcMode) without a settings file or command line saying
        /// otherwise: fast (Server GC with DATAS).
        public const string DefaultGc = GcMode.Default;
        public string Gc = DefaultGc;
        public OptionSource GcSource;
        /// The command line's `--game-tweak` values, in order (empty when it names none).
        public readonly IReadOnlyList<string> CommandLineTweaks;
        /// Names in the settings file that are not tweaks of this player.
        public readonly List<string> UnknownTweaks = new();

        EffectiveOptions(IReadOnlyList<string> all, IReadOnlyList<string> commandLineTweaks)
        {
            AllTweaks = all;
            CommandLineTweaks = commandLineTweaks;
        }

        /// A tweak's built-in default: on unless it is one of `offByDefault` (GameTweakNames.OffByDefault:
        /// those that change what the game does, but the few on by default all the same).
        public static bool DefaultOn(string name, IReadOnlyCollection<string> offByDefault) => !offByDefault.Contains(name);

        /// A tweak as the command line's `--game-tweak` values have it: on when named, or by `all`,
        /// or by `exact` unless it changes behaviour; `-NAME` leaves it out of `all` or `exact`;
        /// `none` turns on nothing (alone, it asks for the game without tweaks).
        public static bool OnByCommandLine(IReadOnlyCollection<string> values, string name, IReadOnlyCollection<string> changing)
            => !values.Contains("-" + name) && (values.Contains(name) || values.Contains("all") || values.Contains("exact") && !changing.Contains(name));

        /// `changing`: the tweaks `exact` leaves out; `offByDefault`: those off without a choice
        /// (null: `changing`).
        public static EffectiveOptions Resolve(IReadOnlyList<string> allTweaks, IReadOnlyCollection<string> changing,
            IReadOnlyList<string> commandLineTweaks, string? commandLineSplash, bool commandLineFrameStats, PlayerSettings file,
            string? commandLineLogPaths = null, string? commandLineGpu = null, string? commandLineRenderer = null, bool? commandLineDxgi = null,
            IReadOnlyCollection<string>? offByDefault = null, string? commandLineGc = null)
        {
            var options = new EffectiveOptions(allTweaks, commandLineTweaks);
            foreach (var name in allTweaks)
            {
                if (commandLineTweaks.Count > 0) options.Tweaks[name] = (OnByCommandLine(commandLineTweaks, name, changing), OptionSource.CommandLine);
                else if (file.GameTweaks.TryGetValue(name, out bool on)) options.Tweaks[name] = (on, OptionSource.SettingsFile);
                else options.Tweaks[name] = (DefaultOn(name, offByDefault ?? changing), OptionSource.Default);
            }
            foreach (var name in file.GameTweaks.Keys)
                if (!allTweaks.Contains(name)) options.UnknownTweaks.Add(name);
            if (commandLineSplash != null) (options.Splash, options.SplashSource) = (commandLineSplash, OptionSource.CommandLine);
            else if (file.Splash != null) (options.Splash, options.SplashSource) = (file.Splash, OptionSource.SettingsFile);
            if (commandLineFrameStats) (options.FrameStats, options.FrameStatsSource) = (true, OptionSource.CommandLine);
            else if (file.FrameStats != null) (options.FrameStats, options.FrameStatsSource) = (file.FrameStats.Value, OptionSource.SettingsFile);
            if (commandLineLogPaths != null) (options.LogPaths, options.LogPathsSource) = (commandLineLogPaths, OptionSource.CommandLine);
            else if (file.LogPaths != null) (options.LogPaths, options.LogPathsSource) = (file.LogPaths, OptionSource.SettingsFile);
            if (commandLineGpu != null) (options.Gpu, options.GpuSource) = (commandLineGpu, OptionSource.CommandLine);
            else if (file.Gpu != null) (options.Gpu, options.GpuSource) = (file.Gpu, OptionSource.SettingsFile);
            if (commandLineRenderer != null) (options.Renderer, options.RendererSource) = (commandLineRenderer, OptionSource.CommandLine);
            else if (file.Renderer != null) (options.Renderer, options.RendererSource) = (file.Renderer, OptionSource.SettingsFile);
            if (commandLineDxgi != null) (options.Dxgi, options.DxgiSource) = (commandLineDxgi.Value, OptionSource.CommandLine);
            else if (file.Dxgi != null) (options.Dxgi, options.DxgiSource) = (file.Dxgi.Value, OptionSource.SettingsFile);
            if (file.UpdateCheck != null) (options.UpdateCheck, options.UpdateCheckSource) = (file.UpdateCheck.Value, OptionSource.SettingsFile);
            if (commandLineGc != null) (options.Gc, options.GcSource) = (commandLineGc, OptionSource.CommandLine);
            else if (file.Gc != null) (options.Gc, options.GcSource) = (file.Gc, OptionSource.SettingsFile);
            return options;
        }

        public IEnumerable<string> EnabledTweaks => AllTweaks.Where(n => Tweaks[n].On);

        /// The file with the screen's choices applied: each choice that equals its default is
        /// dropped from the file, any other recorded; options not chosen (those the command line
        /// set, shown but not changeable) keep what the file had.
        public static PlayerSettings Apply(PlayerSettings file, IReadOnlyCollection<string> offByDefault,
            IReadOnlyDictionary<string, bool> tweaks, bool? splashMoo, bool? frameStats, bool? hideLogPaths = null, string? gpu = null, string? renderer = null,
            bool? dxgi = null, bool? updateCheck = null, string? gc = null)
        {
            var result = new PlayerSettings { Splash = file.Splash, FrameStats = file.FrameStats, LogPaths = file.LogPaths, Gpu = file.Gpu, Renderer = file.Renderer, Dxgi = file.Dxgi,
                UpdateCheck = file.UpdateCheck, Gc = file.Gc };
            foreach (var (name, on) in file.GameTweaks) result.GameTweaks[name] = on;
            foreach (var (name, on) in tweaks)
            {
                if (on == DefaultOn(name, offByDefault)) result.GameTweaks.Remove(name);
                else result.GameTweaks[name] = on;
            }
            if (splashMoo != null) result.Splash = (splashMoo.Value ? "moo" : "none") is var splash && splash == DefaultSplash ? null : splash;
            if (frameStats != null) result.FrameStats = frameStats.Value ? true : null;
            if (hideLogPaths != null) result.LogPaths = (hideLogPaths.Value ? "hidden" : "full") is var logPaths && logPaths == DefaultLogPaths ? null : logPaths;
            if (gpu != null) result.Gpu = gpu == DefaultGpu ? null : gpu;
            if (renderer != null) result.Renderer = renderer == DefaultRenderer ? null : renderer;
            if (dxgi != null) result.Dxgi = dxgi == DefaultDxgi ? null : dxgi;
            if (updateCheck != null) result.UpdateCheck = updateCheck == DefaultUpdateCheck ? null : updateCheck;
            if (gc != null) result.Gc = gc == DefaultGc ? null : gc;
            return result;
        }

        public static string From(OptionSource source) => source switch
        {
            OptionSource.CommandLine => "the command line",
            OptionSource.SettingsFile => "the settings file",
            _ => "default",
        };

        /// For the log: each option with where it came from.
        public IEnumerable<string> Describe()
        {
            if (CommandLineTweaks.Count > 0)
            {
                var on = EnabledTweaks.ToArray();
                yield return $"game tweaks from the command line (--game-tweak {string.Join(" --game-tweak ", CommandLineTweaks)}): "
                             + (on.Length == 0 ? "none" : string.Join(", ", on));
            }
            else
            {
                foreach (var group in AllTweaks.GroupBy(n => Tweaks[n]).OrderByDescending(g => g.Key.On).ThenBy(g => g.Key.Source))
                    yield return $"game tweaks {(group.Key.On ? "on" : "off")} ({From(group.Key.Source)}): {string.Join(", ", group)}";
            }
            yield return $"splash {Splash} ({From(SplashSource)})";
            yield return $"frame stats {(FrameStats ? "on" : "off")} ({From(FrameStatsSource)})";
            yield return $"log paths {LogPaths} ({From(LogPathsSource)})";
            yield return $"gpu {Gpu} ({From(GpuSource)})";
            yield return $"renderer {Renderer} ({From(RendererSource)})";
            yield return $"dxgi {(Dxgi ? "on" : "off")} ({From(DxgiSource)})";
            yield return $"update check {(UpdateCheck ? "on" : "off")} ({From(UpdateCheckSource)})";
            yield return $"gc {Gc} ({From(GcSource)})";
        }

        /// Where the renderer choice came from, for the log's renderer line.
        public string RendererFrom => From(RendererSource);
    }
}
