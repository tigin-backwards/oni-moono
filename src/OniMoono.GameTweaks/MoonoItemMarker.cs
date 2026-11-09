using System;
using System.IO;
using System.Text.RegularExpressions;

namespace OniMoono.GameTweaks
{
    /// How a folder is told to be Moono's own Workshop item (docs/INSTALL-MODES.md): a moono.json
    /// in its root saying `"workshopItem": "oni-moono"`, which the packaging writes beside each
    /// version's mod_info.yaml. Plain .NET, compiled into the host (VersionedInstall reads the
    /// same file) and the tests too. No Workshop ID: the item has none yet.
    public static class MoonoItemMarker
    {
        public const string FileName = "moono.json";
        public const string Item = "oni-moono";

        static readonly Regex marker = new Regex("\"workshopItem\"\\s*:\\s*\"" + Item + "\"", RegexOptions.CultureInvariant);

        /// Whether `folder` (a mod's root) is Moono's item. Never throws: an unreadable folder is not.
        public static bool IsMoonoItem(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            try
            {
                var path = Path.Combine(folder, FileName);
                if (!File.Exists(path)) return false;
                var info = new FileInfo(path);
                if (info.Length > 64 * 1024) return false;
                return marker.IsMatch(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                return false;
            }
        }
    }
}
