using System;
using HarmonyLib;
using KMod;
using OniMoono.Tweaks;
using System.Reflection;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Always on (not a Moono Tweaks row): Moono's own Workshop item (docs/INSTALL-MODES.md),
    /// which carries Moono's players and is no mod, never reaches the game's mod manager while
    /// Moono runs the game: no mod is made for it and no event reported, so it is neither a
    /// broken mod nor a mod event. U59's KMod.Steam.MakeMod takes a Workshop item only as a legacy
    /// zip and reports a folder (as this item is) as DownloadFailed; U60's
    /// MakeModAndReportOutcome takes folders and lists this one with no supported content. Each
    /// build has one of the two returning a KMod.Mod, and that one is patched. The item is told by
    /// its moono.json marker (MoonoItemMarker), not by an ID. Every other item goes through the
    /// game's own code.
    ///
    /// Steamworks is reached by reflection (the tweaks are built against the game's assemblies
    /// CI has, which leave Steamworks.NET out).
    [GameMember(GameUse.Prefix | GameUse.Replaces, "KMod.Mod KMod.Steam::MakeMod(SteamUGCService/Mod)", "d35a653a89ae4871 linux-744825 win-744825", Optional = true)]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "KMod.Mod KMod.Steam::MakeModAndReportOutcome(SteamUGCService/Mod)", Optional = true)]
    [GameMember(GameUse.Uses, "Steamworks.PublishedFileId_t SteamUGCService/Mod::get_fileId()")]
    public static class OwnWorkshopItem
    {
        public const string Name = "skip-own-workshop-item";

        static bool logged;
        static MethodInfo installInfo;
        static PropertyInfo fileId;

        public static void Install(Harmony harmony)
        {
            installInfo = AccessTools.Method("Steamworks.SteamUGC:GetItemInstallInfo")
                ?? throw new MissingMethodException("Steamworks.SteamUGC", "GetItemInstallInfo");
            fileId = AccessTools.Property(typeof(SteamUGCService.Mod), "fileId") ?? throw new MissingMemberException("SteamUGCService.Mod", "fileId");
            var target = AccessTools.Method(typeof(Steam), "MakeModAndReportOutcome", new[] { typeof(SteamUGCService.Mod) });
            if (target == null || target.ReturnType != typeof(Mod)) target = AccessTools.Method(typeof(Steam), "MakeMod", new[] { typeof(SteamUGCService.Mod) });
            if (target == null || target.ReturnType != typeof(Mod)) throw new MissingMethodException("KMod.Steam", "MakeMod");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(OwnWorkshopItem), nameof(MakeMod)));
        }

        /// Null for Moono's item, as the game returns for an item it makes no mod of (its caller
        /// subscribes nothing then); the game's own code for any other.
        static bool MakeMod(SteamUGCService.Mod subscribed, ref Mod __result)
        {
            if (subscribed == null) return true;
            // SteamUGC.GetItemInstallInfo(fileId, out sizeOnDisk, out folder, 1024, out timeStamp), as MakeMod asks.
            var id = fileId.GetValue(subscribed);
            var args = new object[] { id, 0UL, null, 1024u, 0u };
            if (!(installInfo.Invoke(null, args) is bool found) || !found || !(args[2] is string folder)) return true;
            if (!MoonoItemMarker.IsMoonoItem(folder)) return true;
            if (!logged)
            {
                logged = true;
                Debug.Log($"[Moono] Workshop item {subscribed.title} is Moono itself, not a mod: left out of the mod list");
            }
            __result = null;
            return false;
        }
    }
}
