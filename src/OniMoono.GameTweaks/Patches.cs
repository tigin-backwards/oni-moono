using HarmonyLib;

namespace OniMoono.GameTweaks
{
    /// The host's entry point (OniMoono.Host.GameTweaks): installs a tweak of this assembly by name.
    public static class Patches
    {
        public static bool Install(Harmony harmony, string name)
        {
            switch (name)
            {
                case OffscreenConveyors.Name: OffscreenConveyors.Install(harmony); return true;
                case FastSaveTypeChecks.Name: FastSaveTypeChecks.Install(harmony); return true;
                case FastAnimCleanup.Name: FastAnimCleanup.Install(harmony); return true;
                case FastSublimation.Name: FastSublimation.Install(harmony); return true;
                case FastAnimEvents.Name: FastAnimEvents.Install(harmony); return true;
                case FastSymbolOverrides.Name: FastSymbolOverrides.Install(harmony); return true;
                case FastJobs.Name: FastJobs.Install(harmony); return true;
                case FastOffsetTables.Name: FastOffsetTables.Install(harmony); return true;
                case InstantReachability.Name: InstantReachability.Install(harmony); return true;
                case OffWorldFallers.Name: OffWorldFallers.Install(harmony); return true;
                case FastPartitionerCleanup.Name: FastPartitionerCleanup.Install(harmony); return true;
                case FastAnimBatches.Name: FastAnimBatches.Install(harmony); return true;
                case FastOverlays.Name: FastOverlays.Install(harmony); return true;
                case FastConveyorPaths.Name: FastConveyorPaths.Install(harmony); return true;
                case FastOverlayTextures.Name: FastOverlayTextures.Install(harmony); return true;
                case BrainRate.Name: BrainRate.Install(harmony); return true;
                case ProbeRepair.Name: ProbeRepair.Install(harmony); return true;
                case FastLoadScreen.Name: FastLoadScreen.Install(harmony); return true;
                case FixLoadScreenClicks.Name: FixLoadScreenClicks.Install(harmony); return true;
                case FastSmiLookups.Name: FastSmiLookups.Install(harmony); return true;
                case EndlessLoopSounds.Name: EndlessLoopSounds.Install(harmony); return true;
                case OneShotPiles.Name: OneShotPiles.Install(harmony); return true;
                case FetchByCell.Name: FetchByCell.Install(harmony); return true;
                case GeothermalVentMass.Name: GeothermalVentMass.Install(harmony); return true;
                case TimelapseSnapshots.Name: TimelapseSnapshots.Install(harmony); return true;
                case SharedDuplicates.Name: SharedDuplicates.Install(harmony); return true;
                case CompactReports.Name: CompactReports.Install(harmony); return true;
                case OwnWorkshopItem.Name: OwnWorkshopItem.Install(harmony); return true;
                default: return false;
            }
        }
    }
}
