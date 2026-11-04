using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// ARCHITECTURE D34, the live frames. Mods patch the game while its mod loader runs: each
    /// mod's UserMod2.OnLoad from DLLLoader.LoadDLLs, in KMod.Manager.Load's loop over the mods,
    /// and OnAllModsLoaded in the loop after it, all under Global.Awake. PatchInliners rebuilds
    /// the code that had inlined a method patched then, but a rebuild serves later calls only: a
    /// frame already running keeps the code it started in. Optimized code of those loops exists
    /// before any mod loads when tiering is off (DOTNET_TieredCompilation=0), or once a loop goes
    /// to on-stack replacement (a thousand iterations by default: about a thousand mods, or
    /// Global.Awake's loop over the sprites of its forced atlases); it inlines small methods, as
    /// Manager.Load inlined Mod.PostLoad, which PLib patches from Fast Track's OnLoad, so PLib's
    /// start-up hook never ran and its later stages never came (issue #52). With the default
    /// tiering these frames run at tier 0, which inlines nothing, so a patch reaches their later
    /// calls.
    ///
    /// The host marks the methods those frames call after a mod's code may have run not to be
    /// inlined (MonoMod's TryDisableInlining, as Harmony marks a patched method), before any game
    /// code is compiled, so optimized code of the frames calls them as tier 0 does. They are called
    /// once per mod or once per start, so a call each costs nothing measurable; the same methods
    /// run in the same order.
    internal static class ModLoaderInlining
    {
        /// "Type:Method", types of Assembly-CSharp or its firstpass; every overload is marked. The
        /// general-purpose methods these frames call too (Debug's logging, the BCL's lists and
        /// enumerators, Harmony's own, GlobalResources.Instance, DistributionPlatform's
        /// accessors) are left as they are.
        internal static readonly string[] Methods =
        [
            // KMod.Manager.Load: the loops over the mods loading them, post-loading them, and
            // marking those that failed.
            "KMod.Mod:IsEnabledForActiveDlc", "KMod.Mod:Load", "KMod.Mod:PostLoad", "KMod.Mod:get_loaded_content",
            "KMod.Mod:get_available_content", "KMod.Mod:SetCrashed", "KMod.Mod:get_title", "KMod.Manager:Save",
            // KMod.Mod.Load, after DLLLoader.LoadDLLs returns.
            "KMod.Mod:set_loaded_content",
            // DLLLoader's loops over a mod's UserMod2 instances (the base methods, which a mod's
            // class may not override).
            "KMod.UserMod2:OnLoad", "KMod.UserMod2:OnAllModsLoaded",
            // Global.Awake, after Manager.Load(Content.DLL).
            "KMod.Manager:Load", "KSerialization.Manager:Initialize", "Global:InitializeGlobalInput",
            "Global:InitializeGlobalSound", "Global:InitializeGlobalAnimation", "Localization:Initialize",
            "KMod.Local:.ctor", "Global:RestoreLegacyMetricsSetting", "Global:TestDataLocations", "Global:OnGetUserIdKey",
            "KleiAccount:AuthenticateUser", "KleiItems:LoadInventoryCache", "ProcGenGame.WorldGen:LoadSettings",
            "ProcGenGame.WorldGen:ListenForLoadSettingsErrorRoutine",
        ];

        static bool installed;
        /// The methods marked and the names not found, for --check-types; MonoMod's marking.
        internal static readonly List<MethodBase> Marked = new();
        internal static readonly List<string> Missing = new();
        internal static Func<MethodBase, bool> Disable = null!;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            var tryDisable = AccessTools.Method("MonoMod.Core.Platforms.PlatformTriple:TryDisableInlining")
                ?? throw new MissingMethodException("MonoMod.Core.Platforms.PlatformTriple", "TryDisableInlining");
            var current = AccessTools.Property("MonoMod.Core.Platforms.PlatformTriple:Current")?.GetValue(null)
                ?? throw new MissingMemberException("MonoMod.Core.Platforms.PlatformTriple", "Current");
            Disable = (Func<MethodBase, bool>)Delegate.CreateDelegate(typeof(Func<MethodBase, bool>), current, tryDisable);
            var game = new List<Assembly>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name is "Assembly-CSharp" or "Assembly-CSharp-firstpass") game.Add(assembly);
            foreach (var name in Methods)
            {
                int split = name.LastIndexOf(':');
                Type? type = null;
                foreach (var assembly in game) type ??= assembly.GetType(name[..split], false);
                string method = name[(split + 1)..];
                bool found = false;
                if (type != null)
                    foreach (var candidate in method == ".ctor"
                                 ? (IEnumerable<MethodBase>)type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                 : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        if (candidate.Name == method && candidate.GetMethodBody() != null && Disable(candidate))
                        {
                            Marked.Add(candidate);
                            found = true;
                        }
                if (!found) Missing.Add(name);
            }
            if (Missing.Count > 0)
                EngineLog.Engine($"mod loader inlining: not found, left as they are: {string.Join(", ", Missing)}");
        }
    }
}
