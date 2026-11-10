#nullable enable
using System;
using System.Diagnostics;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoonoLoader
{
    /// The game's own info dialog (InfoDialogScreen), which stays open: a short explanation, the
    /// launch-option line (selectable), the steps in Steam, and two buttons. Copy installs or
    /// refreshes Moono (ItemInstall) and puts the line on the clipboard, then says so on its
    /// label, so the player can switch to Steam, paste and come back; Close dismisses it, and the
    /// game carries on.
    internal static class MoonoDialog
    {
        /// The line the last Copy put on the clipboard (the e2e spec reads it).
        internal static string? LastCopied;

        public static void Show(GameObject parent)
        {
            var root = ItemInstall.DefaultRoot(ItemInstall.ItemRootOf(Loader.VersionDir), Loader.OnWindows,
                Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var install = new ItemInstall(Loader.VersionDir, root, Loader.OnWindows);
            var dialog = Util.KInstantiateUI<InfoDialogScreen>(ScreenPrefabs.Instance.InfoDialogScreen.gameObject, parent, force_active: true);
            dialog.name = "MoonoDialog";
            dialog.SetHeader("Play Oxygen Not Included with Moono?");
            dialog.AddPlainText("Moono runs Oxygen Not Included on its own engine (unofficial). Steam starts it with this line in the game's launch options:");
            AddSelectableLine(dialog, install.LaunchOption);
            dialog.AddPlainText("In Steam: right-click Oxygen Not Included → Properties → Launch Options, paste, then restart the game.");
            dialog.AddUI(dialog.GetPlainTextPrefab(), out var statusItem);
            var status = statusItem.GetComponentInChildren<LocText>();
            status.text = "";
            dialog.AddOption(false, out var close, out var closeLabel);
            closeLabel.text = "Close";
            close.onClick += dialog.Deactivate;
            dialog.AddOption(true, out var copy, out var copyLabel);
            copyLabel.text = "Copy";
            copy.onClick += () =>
            {
                try
                {
                    var done = install.Install(Process.GetCurrentProcess().Id, Loader.OnWindows ? null : (Action<string>)Loader.MakeExecutable);
                    SetClipboard(install.LaunchOption);
                    LastCopied = install.LaunchOption;
                    copyLabel.text = "Copied ✓";
                    status.text = done + ".";
                    global::Debug.Log($"[MoonoLoader] {done}; launch option copied: {install.LaunchOption}");
                }
                catch (ItemInstall.Failure e)
                {
                    status.text = "Moono could not be installed: " + e.Message;
                    global::Debug.LogWarning("[MoonoLoader] not installed: " + e.Message);
                }
            };
            // Escape closes it too.
            Traverse.Create(dialog).Field("escapeCloses").SetValue(true);
            global::Debug.Log("[MoonoLoader] asked whether to play with Moono");
        }

        /// The line as a read-only text field, so it can be selected and copied by hand as well.
        /// The plain text item keeps the dialog's layout (its own text, the line, transparent);
        /// the field lies over it, outside the layout, its text a child it can scroll.
        static void AddSelectableLine(InfoDialogScreen dialog, string line)
        {
            var prefab = dialog.GetPlainTextPrefab().gameObject;
            dialog.AddUI(dialog.GetPlainTextPrefab(), out var item);
            item.name = "LaunchOption";
            var shown = item.GetComponentInChildren<LocText>();
            shown.text = line;
            try
            {
                var area = Util.KInstantiateUI(prefab, item.gameObject, force_active: true);
                area.name = "Field";
                Cover(area);
                area.GetComponent<LocText>().text = "";
                var text = Util.KInstantiateUI(prefab, area, force_active: true);
                text.name = "Text";
                Cover(text);
                var label = text.GetComponent<LocText>();
                label.text = line;
                var field = area.AddComponent<TMP_InputField>();
                field.textViewport = area.GetComponent<RectTransform>();
                field.textComponent = label;
                field.lineType = TMP_InputField.LineType.MultiLineNewline;
                field.richText = false;
                field.readOnly = true;
                field.text = line;
                shown.color = new Color(shown.color.r, shown.color.g, shown.color.b, 0f);
            }
            catch (Exception e)
            {
                global::Debug.LogWarning("[MoonoLoader] the line is shown as plain text: " + e.Message);
            }
        }

        /// Fills its parent's rectangle, outside the parent's layout.
        static void Cover(GameObject go)
        {
            if (go.GetComponent<UnityEngine.UI.LayoutElement>() is { } layout) layout.ignoreLayout = true;
            else go.AddComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        /// GUIUtility.systemCopyBuffer, in UnityEngine.IMGUIModule (not among the build's
        /// reference assemblies, so by name).
        static void SetClipboard(string text) =>
            Type.GetType("UnityEngine.GUIUtility, UnityEngine.IMGUIModule")?.GetProperty("systemCopyBuffer")?.SetValue(null, text);
    }
}
