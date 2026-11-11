using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Always on (not a Moono Tweaks row), U60: the colony images the Timelapser writes in the
    /// background are whole before the game reads or deletes them.
    ///
    /// U60's Timelapser reads its render back asynchronously and encodes and writes the PNG on a
    /// task (a save's colony preview, the daily snapshots). It waits for those writes only when
    /// it is destroyed (FlushPendingScreenshots, from OnForcedCleanUp), not when the game reads
    /// the files: the Load screen opened from the pause menu right after a save reads the
    /// preview the save is still writing (LoadImage of a partial file gives no picture, and on
    /// Windows the read can fail outright), the Retired Colonies screen reads the snapshots, and
    /// deleting a save deletes its preview. Here each of those first waits for the pending
    /// read-backs and writes (FlushPendingScreenshots, which does nothing when there are none).
    /// The files and what the game shows of them are the game's; only when they are read
    /// changes. The game's own HasPendingScreenshotWrites, which no code reads, says so.
    [GameMember(GameUse.Prefix, "UnityEngine.Sprite RetireColonyUtility::LoadColonyPreview(System.String,System.String,System.Boolean)")]
    [GameMember(GameUse.Prefix, "UnityEngine.Sprite RetireColonyUtility::LoadRetiredColonyPreview(System.String,System.String)")]
    [GameMember(GameUse.Prefix, "UnityEngine.Sprite[] RetireColonyUtility::LoadColonySlideshow(System.String)")]
    [GameMember(GameUse.Prefix, "System.String[] RetireColonyUtility::LoadColonySlideshowFiles(System.String,System.String)")]
    [GameMember(GameUse.Prefix, "System.Void LoadScreen::DeleteFileAndEmptyFolder(System.String)")]
    [GameMember(GameUse.Uses, "Timelapser Game::timelapser")]
    [GameMember(GameUse.Uses, "System.Void Timelapser::FlushPendingScreenshots()")]
    public static class TimelapseWrites
    {
        public const string Name = "wait-timelapse-writes";

        public static void Install(Harmony harmony)
        {
            var wait = new HarmonyMethod(typeof(TimelapseWrites), nameof(WaitForWrites));
            harmony.Patch(AccessTools.Method(typeof(RetireColonyUtility), nameof(RetireColonyUtility.LoadColonyPreview)), prefix: wait);
            harmony.Patch(AccessTools.Method(typeof(RetireColonyUtility), nameof(RetireColonyUtility.LoadRetiredColonyPreview)), prefix: wait);
            harmony.Patch(AccessTools.Method(typeof(RetireColonyUtility), nameof(RetireColonyUtility.LoadColonySlideshow)), prefix: wait);
            harmony.Patch(AccessTools.Method(typeof(RetireColonyUtility), nameof(RetireColonyUtility.LoadColonySlideshowFiles)), prefix: wait);
            harmony.Patch(AccessTools.Method(typeof(LoadScreen), "DeleteFileAndEmptyFolder"), prefix: wait);
        }

        /// While a colony is loaded, its Timelapser's pending read-backs and writes are done.
        static void WaitForWrites()
        {
            var timelapser = Game.Instance != null ? Game.Instance.timelapser : null;
            if (timelapser != null) timelapser.FlushPendingScreenshots();
        }
    }
}
