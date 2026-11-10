#nullable enable
using System;
using System.Collections;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace MoonoLoader
{
    /// The loader mod of Moono's Workshop item (docs/INSTALL-MODES.md, "The loader mod"). The
    /// original game loads it from the item's U60 root; at the main menu, once each start, it
    /// asks whether to play with Moono (MoonoDialog), and the item's row in the Mods screen has a
    /// button that asks again. Under Moono's own player it does nothing: Moono leaves the item
    /// out of its mod list.
    public sealed class Loader : UserMod2
    {
        internal static Mod? Own;
        internal static string VersionDir = "";
        static bool asked;

        public override void OnLoad(Harmony harmony)
        {
            if (MoonoRuns())
            {
                Debug.Log("[MoonoLoader] Moono's player runs the game: nothing to do");
                return;
            }
            Own = mod;
            VersionDir = path;
            harmony.Patch(AccessTools.Method(typeof(MainMenu), "OnSpawn"), postfix: new HarmonyMethod(typeof(Loader), nameof(MainMenuShown)));
            harmony.Patch(AccessTools.Method(typeof(ModsScreen), "BuildDisplay"), postfix: new HarmonyMethod(typeof(Loader), nameof(ModRowsBuilt)));
            Debug.Log("[MoonoLoader] loaded from " + path);
        }

        /// Moono runs the game on .NET, not on Unity's Mono, with its own OniMoono assemblies.
        static bool MoonoRuns() =>
            Type.GetType("Mono.Runtime") == null
            || AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is string n && (n == "OniMoono" || n.StartsWith("OniMoono.", StringComparison.Ordinal)));

        static void MainMenuShown(MainMenu __instance)
        {
            if (asked) return;
            asked = true;
            MoonoDialog.Show(__instance.gameObject);
        }

        /// The item's own row gets a button beside its Manage button that shows the dialog.
        static void ModRowsBuilt(ModsScreen __instance)
        {
            if (!(Traverse.Create(__instance).Field("displayedMods").GetValue() is IList rows))
            {
                Debug.LogWarning("[MoonoLoader] the Mods screen's rows aren't where they were: no button on the item's row");
                return;
            }
            var mods = Global.Instance.modManager.mods;
            foreach (var row in rows)
            {
                var fields = Traverse.Create(row);
                int index = fields.Field<int>("mod_index").Value;
                var rect = fields.Field<RectTransform>("rect_transform").Value;
                if (Own == null || index < 0 || index >= mods.Count || !mods[index].label.Match(Own.label) || rect == null) continue;
                var manage = rect.GetComponent<HierarchyReferences>()?.GetReference<KButton>("ManageButton");
                if (manage == null)
                {
                    Debug.LogWarning("[MoonoLoader] the item's row in the Mods screen has no Manage button to put a button beside");
                    continue;
                }
                var copy = Util.KInstantiateUI(manage.gameObject, manage.transform.parent.gameObject, force_active: true);
                copy.name = "MoonoButton";
                copy.transform.SetSiblingIndex(manage.transform.GetSiblingIndex() + 1);
                var button = copy.GetComponent<KButton>();
                button.ClearOnClick();
                button.isInteractable = true;
                button.onClick += () => MoonoDialog.Show(__instance.gameObject);
                var label = copy.GetComponentInChildren<LocText>();
                if (label != null) label.text = "Play with Moono";
                copy.GetComponent<ToolTip>()?.SetSimpleTooltip("Install Moono and copy the line for Steam's launch options");
            }
        }

        public static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        static extern int Chmod(string path, uint mode);

        /// Linux: rwxr-xr-x for the player's executables and the entry point.
        internal static void MakeExecutable(string path) => Chmod(path, 0b111_101_101);
    }
}
