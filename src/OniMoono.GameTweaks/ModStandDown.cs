using System;

namespace OniMoono.GameTweaks
{
    /// Mods a tweak turns itself off for while they are active, whatever they patch at the moment
    /// (some patch only under options they read in play): by static ID.
    internal static class ModStandDown
    {
        /// The title of the first active mod among `staticIds`, or null.
        public static string Active(string[] staticIds)
        {
            var mods = Global.Instance?.modManager?.mods;
            if (mods == null) return null;
            foreach (var mod in mods)
                if (mod.IsActive() && Array.IndexOf(staticIds, mod.staticID) >= 0) return mod.title;
            return null;
        }
    }
}
