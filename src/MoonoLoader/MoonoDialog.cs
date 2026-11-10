#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoonoLoader
{
    /// The game's own info dialog (InfoDialogScreen), which stays open until Not now: a line on
    /// what Moono does, three numbered steps with the launch-option line in a box (the game's
    /// text field, shortened in the middle when long), and a muted line on how to stop it.
    /// Copy launch option installs or refreshes Moono (ItemInstall), puts the whole line on the
    /// clipboard and says so on its label and in step 1, so the player can switch to Steam,
    /// paste and come back. The text is in the game's language where Moono has a translation.
    internal static class MoonoDialog
    {
        /// The line the last Copy put on the clipboard (the e2e spec reads it).
        internal static string? LastCopied;

        /// How much of the line the box shows (LoaderStrings.Shorten).
        const int ShownLength = 54;

        /// The install the loader's version makes, into the folder Moono's installers use.
        static ItemInstall CurrentInstall() =>
            new ItemInstall(Loader.VersionDir, ItemInstall.DefaultRoot(ItemInstall.ItemRootOf(Loader.VersionDir), Loader.OnWindows,
                Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), Loader.OnWindows);

        /// One of LoaderStrings' strings in the game's language.
        internal static string Text(string name) => LoaderStrings.Text(Translation(CurrentInstall()), name);

        public static void Show(GameObject parent)
        {
            var install = CurrentInstall();
            var strings = Translation(install);
            string T(string name) => LoaderStrings.Text(strings, name);

            var dialog = Util.KInstantiateUI<InfoDialogScreen>(ScreenPrefabs.Instance.InfoDialogScreen.gameObject, parent, force_active: true);
            dialog.name = "MoonoDialog";
            dialog.SetHeader(T(LoaderStrings.Title));
            AddText(dialog, "Intro", T(LoaderStrings.Intro));
            var step1 = AddText(dialog, "Step1", "1. " + T(LoaderStrings.StepCopy));
            AddLine(dialog, LoaderStrings.Shorten(install.LaunchOption, ShownLength));
            AddText(dialog, "Step2", "2. " + T(LoaderStrings.StepPaste));
            AddText(dialog, "Step3", "3. " + T(LoaderStrings.StepRestart));
            var hint = AddText(dialog, "Hint", T(LoaderStrings.DisableHint));
            hint.color = new Color(hint.color.r, hint.color.g, hint.color.b, 0.55f);
            hint.fontSize *= 0.85f;

            dialog.AddOption(false, out var notNow, out var notNowLabel);
            notNowLabel.text = T(LoaderStrings.NotNow);
            notNow.onClick += dialog.Deactivate;
            dialog.AddOption(true, out var copy, out var copyLabel);
            copyLabel.text = T(LoaderStrings.Copy);
            copy.onClick += () =>
            {
                try
                {
                    var done = install.Install(Process.GetCurrentProcess().Id, Loader.OnWindows ? null : (Action<string>)Loader.MakeExecutable);
                    SetClipboard(install.LaunchOption);
                    LastCopied = install.LaunchOption;
                    copyLabel.text = T(LoaderStrings.Copied);
                    step1.text = "1. " + T(LoaderStrings.Installed);
                    global::Debug.Log($"[MoonoLoader] {done}; launch option copied: {install.LaunchOption}");
                }
                catch (ItemInstall.Failure e)
                {
                    step1.text = "1. " + string.Format(T(LoaderStrings.Failed), e.Message);
                    global::Debug.LogWarning("[MoonoLoader] not installed: " + e.Message);
                }
            };
            // Escape closes it too.
            Traverse.Create(dialog).Field("escapeCloses").SetValue(true);
            global::Debug.Log("[MoonoLoader] asked whether to play with Moono");
        }

        /// A line of the dialog's text, named for the e2e spec; its text component.
        static LocText AddText(InfoDialogScreen dialog, string name, string text)
        {
            dialog.AddUI(dialog.GetPlainTextPrefab(), out var item);
            item.name = name;
            item.SetText(text);
            return item.GetComponentInChildren<LocText>();
        }

        /// The launch-option line in the box of the game's own text field (the file name
        /// dialog's), its editing taken out: one line, each character as wide as the next.
        static void AddLine(InfoDialogScreen dialog, string shown)
        {
            var prefab = Traverse.Create(ScreenPrefabs.Instance.FileNameDialog).Field("inputField").GetValue() as TMP_InputField;
            if (prefab == null)
            {
                global::Debug.LogWarning("[MoonoLoader] no text field to show the line in: plain text instead");
                AddText(dialog, "LaunchOption", shown);
                return;
            }
            dialog.AddUI(prefab, out var field);
            field.name = "LaunchOption";
            var box = field.gameObject;
            var text = field.textComponent;
            var placeholder = field.placeholder;
            UnityEngine.Object.DestroyImmediate(field);
            if (placeholder != null) placeholder.gameObject.SetActive(false);
            foreach (Transform child in text.transform.parent)
                if (child.name.IndexOf("Caret", StringComparison.OrdinalIgnoreCase) >= 0) child.gameObject.SetActive(false);
            // The dialog's layout sizes it: one line high, the dialog's width.
            var layout = box.GetComponent<UnityEngine.UI.LayoutElement>() ?? box.AddComponent<UnityEngine.UI.LayoutElement>();
            layout.minHeight = layout.preferredHeight = 32f;
            layout.flexibleWidth = 1f;
            text.fontSize = 14f;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.richText = true;
            // Obsolete in U60's TextMeshPro (textWrappingMode there), the only one U59's has.
#pragma warning disable CS0618
            text.enableWordWrapping = false;
#pragma warning restore CS0618
            text.overflowMode = TextOverflowModes.Overflow;
            text.text = "<mspace=0.6em><noparse>" + shown + "</noparse></mspace>";
        }

        /// The game language's .po among the translations of the player the loader's version
        /// carries (<platform>/player/translations/CODE.po, as Moono's own screen finds its own):
        /// the locale's code, then its part before `_` or `-`; null in English or without one.
        static Dictionary<string, string>? Translation(ItemInstall install)
        {
            try
            {
                var code = Localization.GetLocale()?.Code;
                var info = ItemInstall.ReadInfo(install.VersionDir, install.Platform, out _);
                if (string.IsNullOrEmpty(code) || info == null) return null;
                var dir = System.IO.Path.Combine(install.VersionDir, info.Folder, "player", "translations");
                int cut = code.IndexOfAny(new[] { '_', '-' });
                foreach (var name in cut > 0 ? new[] { code, code.Substring(0, cut) } : new[] { code })
                {
                    var path = System.IO.Path.Combine(dir, name + ".po");
                    if (System.IO.File.Exists(path)) return Localization.LoadStringsFile(path, false);
                }
            }
            catch (Exception e)
            {
                global::Debug.LogWarning("[MoonoLoader] the translation could not be read, English instead: " + e.Message);
            }
            return null;
        }

        /// GUIUtility.systemCopyBuffer, in UnityEngine.IMGUIModule (not among the build's
        /// reference assemblies, so by name).
        static void SetClipboard(string text) =>
            Type.GetType("UnityEngine.GUIUtility, UnityEngine.IMGUIModule")?.GetProperty("systemCopyBuffer")?.SetValue(null, text);
    }
}
