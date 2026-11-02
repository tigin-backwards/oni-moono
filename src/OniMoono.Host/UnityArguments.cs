#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace OniMoono.Host
{
    /// Unity's standalone player arguments, which players put in the game's Steam launch options:
    /// the ones the player acts on are read here, the rest are ignored with a note for the log, as
    /// Unity's player ignores what it does not know. Unity documents them in "Unity Standalone
    /// Player command line arguments" (docs.unity.com, Unity 6000.3: unity-editor/command-line-
    /// arguments/player). A malformed value is a note too, and the setting keeps its default.
    internal static class UnityArguments
    {
        /// Unity's documented arguments that take the next argument as their value (-parentHWND
        /// takes a second word, "delayed", which is then a stray word and ignored as one).
        static readonly HashSet<string> WithValue = new(StringComparer.OrdinalIgnoreCase)
        {
            "-logFile", "-monitor", "-screen-fullscreen", "-screen-height", "-screen-width", "-screen-quality",
            "-force-device-index", "-max-async-pso-job-count", "-parentHWND", "-window-mode",
        };

        /// One of Unity's renderer options (-force-glcore, -force-opengl, -force-vulkan): true when
        /// args[i] is one. Ours renders with OpenGL Core only, which U59's Linux player opens for
        /// the first two (measured on 744825, spec p2-renderer): they need nothing more. Given
        /// -force-vulkan that player tries Vulkan, and exits where the only Vulkan device is a
        /// software one ("Forced renderer 21 is not supported, exiting"); ours stays on OpenGL,
        /// with a note.
        public static bool Renderer(string arg, List<string> notes)
        {
            switch (arg.ToLowerInvariant())
            {
                case "-force-glcore" or "-force-opengl":
                    return true;
                case "-force-vulkan":
                    notes.Add($"{arg}: this player renders with OpenGL only; OpenGL Core is used");
                    return true;
                default:
                    return false;
            }
        }

        /// An argument for Unity's player rather than ours: ours start with "--".
        public static bool IsUnitys(string arg) => !arg.StartsWith("--", StringComparison.Ordinal);

        public static bool TakesValue(string arg) => WithValue.Contains(arg);

        /// The value of args[i] (an argument that takes one), i moved onto it; null with a note
        /// when it is missing.
        public static string? Value(string[] args, ref int i, List<string> notes)
        {
            if (i + 1 < args.Length) return args[++i];
            notes.Add($"{args[i]} has no value; ignored");
            return null;
        }

        /// A whole number from `min` up for args[i], or null with a note.
        public static int? Int(string[] args, ref int i, List<string> notes, int min = 0, int max = int.MaxValue)
        {
            string name = args[i];
            if (Value(args, ref i, notes) is not { } text) return null;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= min && value <= max) return value;
            notes.Add($"{name} {text}: not a number from {min}{(max == int.MaxValue ? " up" : $" to {max}")}; ignored");
            return null;
        }

        /// An argument the player does not act on (args[i]), and its value if Unity documents
        /// one: skipped, with a note.
        public static void Ignore(string[] args, ref int i, List<string> notes)
        {
            string name = args[i];
            if (TakesValue(name) && i + 1 < args.Length)
            {
                notes.Add($"{name} {args[++i]}: not read by this player; ignored");
                return;
            }
            notes.Add($"{name}: not read by this player; ignored");
        }
    }
}
