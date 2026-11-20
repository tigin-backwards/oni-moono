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
    /// text field, shortened in the middle when long) and a small Copy button beside it, and a
    /// muted line on how to stop it. Install & copy launch option installs or refreshes Moono
    /// (ItemInstall), puts the whole line on the clipboard and says so in step 1 and on the Copy
    /// button, so the player can switch to Steam, paste and come back; the button then says
    /// Close game and quits the game as its main menu's Quit does (App.Quit). The Copy button
    /// copies the line again at any time. A first install copies the player off the main thread
    /// (InstallRun), the button disabled and saying Installing… meanwhile. Each button keeps
    /// one width for all its labels, so nothing moves between states. The text is in the
    /// game's language where Moono has a translation.
    internal static class MoonoDialog
    {
        /// The line the last Copy put on the clipboard (the e2e spec reads it).
        internal static string? LastCopied;

        /// Whether Install & copy launch option succeeded in this start: the dialog shown again
        /// then opens with the button saying Close game.
        static bool installed;

        /// The last install the button started, which a click while it runs joins.
        static InstallRun? running;

        /// How much of the line the box shows beside the Copy button (LoaderStrings.Shorten).
        const int ShownLength = 50;

        /// How long the Copy button says Copied ✓.
        const float CopiedSeconds = 2f;

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
            var step1 = AddText(dialog, "Step1", "1. " + T(installed ? LoaderStrings.Installed : LoaderStrings.StepCopy));
            dialog.AddSpacer(4f);
            var copyLine = AddLine(dialog, LoaderStrings.Shorten(install.LaunchOption, ShownLength), T(LoaderStrings.CopyLine), T(LoaderStrings.Copied));
            void CopyNow()
            {
                SetClipboard(install.LaunchOption);
                LastCopied = install.LaunchOption;
                copyLine?.ShowCopied(CopiedSeconds);
            }
            if (copyLine != null) copyLine.Button.onClick += () =>
            {
                CopyNow();
                global::Debug.Log("[MoonoLoader] launch option copied again: " + install.LaunchOption);
            };
            dialog.AddSpacer(8f);
            AddText(dialog, "Step2", "2. " + T(LoaderStrings.StepPaste));
            dialog.AddSpacer(8f);
            AddText(dialog, "Step3", "3. " + T(LoaderStrings.StepRestart));
            var hint = AddText(dialog, "Hint", T(LoaderStrings.DisableHint));
            hint.color = new Color(hint.color.r, hint.color.g, hint.color.b, 0.55f);
            hint.fontSize *= 0.85f;

            dialog.AddOption(false, out var notNow, out var notNowLabel);
            notNowLabel.text = T(LoaderStrings.NotNow);
            notNow.onClick += dialog.Deactivate;
            dialog.AddOption(true, out var main, out var mainLabel);
            FixWidth(main, mainLabel, T(LoaderStrings.Copy), T(LoaderStrings.Installing), T(LoaderStrings.CloseGame));
            mainLabel.text = T(installed ? LoaderStrings.CloseGame : LoaderStrings.Copy);
            void Finish(InstallRun run)
            {
                if (run.Failure is { } failure)
                {
                    main.isInteractable = true;
                    mainLabel.text = T(LoaderStrings.Copy);
                    step1.text = "1. " + string.Format(T(LoaderStrings.Failed), failure);
                    global::Debug.LogWarning("[MoonoLoader] not installed: " + failure);
                    return;
                }
                CopyNow();
                installed = true;
                main.isInteractable = true;
                mainLabel.text = T(LoaderStrings.CloseGame);
                step1.text = "1. " + T(LoaderStrings.Installed);
                global::Debug.Log($"[MoonoLoader] {run.Done}; launch option copied: {install.LaunchOption}");
            }
            // Until the copy ends: no second click, the button says so, and the dialog's own
            // Update shows the outcome on the main thread.
            void Wait(InstallRun run)
            {
                main.isInteractable = false;
                mainLabel.text = T(LoaderStrings.Installing);
                var watch = dialog.gameObject.GetComponent<InstallWatch>();
                if (watch == null) watch = dialog.gameObject.AddComponent<InstallWatch>();
                watch.Watch(run, () => Finish(run));
            }
            main.onClick += () =>
            {
                if (!main.isInteractable) return;
                if (installed)
                {
                    global::Debug.Log("[MoonoLoader] closing the game to play with Moono");
                    App.Quit();
                    return;
                }
                int pid = Process.GetCurrentProcess().Id;
                var run = running = InstallRun.Join(running, install.NeedsCopy,
                    () => install.Install(pid, Loader.OnWindows ? null : (Action<string>)Loader.MakeExecutable));
                if (run.Finished)
                {
                    Finish(run);
                    return;
                }
                global::Debug.Log("[MoonoLoader] installing Moono off the main thread");
                Wait(run);
            };
            // Shown again while an install it started still runs: it waits for that one.
            if (!installed && running != null && !running.Finished) Wait(running);
            // As little room above the buttons as below the title: the button row's own top
            // padding goes.
            if (Traverse.Create(dialog).Field("rightButtonPanel").GetValue() is GameObject panel
                && panel.transform.parent.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>() is { } row)
                row.padding = new RectOffset(row.padding.left, row.padding.right, 0, row.padding.bottom);
            // Escape closes it too.
            Traverse.Create(dialog).Field("escapeCloses").SetValue(true);
            global::Debug.Log("[MoonoLoader] asked whether to play with Moono");
        }

        /// One width for a button whatever its label says: its widest label's, plus the
        /// button's own padding, and at least the width it has.
        static void FixWidth(KButton button, LocText label, params string[] labels)
        {
            float widest = 0f;
            foreach (var text in labels) widest = Mathf.Max(widest, label.GetPreferredValues(text).x);
            var go = button.gameObject;
            float padding = go.GetComponent<UnityEngine.UI.HorizontalOrVerticalLayoutGroup>()?.padding.horizontal ?? 0;
            var layout = go.GetComponent<UnityEngine.UI.LayoutElement>() ?? go.AddComponent<UnityEngine.UI.LayoutElement>();
            float width = Mathf.Ceil(Mathf.Max(layout.minWidth, widest + padding));
            layout.minWidth = layout.preferredWidth = width;
            layout.flexibleWidth = 0f;
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
        /// dialog's), its editing taken out: one line in the game's UI font (the game ships no
        /// monospaced one, and TextMeshPro's forced spacing gaps hyphens and percent signs). A
        /// small Copy button in the dialog's secondary style sits beside it, on one row; null
        /// when the game has no text field (plain text then, without the button).
        static CopyLine? AddLine(InfoDialogScreen dialog, string shown, string copy, string copied)
        {
            var prefab = Traverse.Create(ScreenPrefabs.Instance.FileNameDialog).Field("inputField").GetValue() as TMP_InputField;
            if (prefab == null)
            {
                global::Debug.LogWarning("[MoonoLoader] no text field to show the line in: plain text instead");
                AddText(dialog, "LaunchOption", shown);
                return null;
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
            // The row takes the box's place in the dialog's layout: the box as wide as the
            // button leaves it, both one line high.
            var row = new GameObject("LaunchOptionRow", typeof(RectTransform));
            row.transform.SetParent(box.transform.parent, false);
            row.transform.SetSiblingIndex(box.transform.GetSiblingIndex());
            var rowLayout = row.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            rowLayout.spacing = 6f;
            rowLayout.childControlWidth = rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = rowLayout.childForceExpandHeight = false;
            var rowElement = row.AddComponent<UnityEngine.UI.LayoutElement>();
            rowElement.minHeight = rowElement.preferredHeight = 32f;
            rowElement.flexibleWidth = 1f;
            box.transform.SetParent(row.transform, false);
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
            text.text = "<noparse>" + shown + "</noparse>";

            var buttonPrefab = Traverse.Create(dialog).Field("leftButtonPrefab").GetValue() as GameObject;
            if (buttonPrefab == null)
            {
                global::Debug.LogWarning("[MoonoLoader] no button to copy the line with beside it");
                return null;
            }
            var go = Util.KInstantiateUI(buttonPrefab, row, force_active: true);
            go.name = "CopyLine";
            var button = go.GetComponent<KButton>();
            var label = go.GetComponentInChildren<LocText>();
            var element = go.GetComponent<UnityEngine.UI.LayoutElement>() ?? go.AddComponent<UnityEngine.UI.LayoutElement>();
            element.minHeight = element.preferredHeight = 32f;
            element.minWidth = -1f;
            if (go.GetComponent<UnityEngine.UI.HorizontalOrVerticalLayoutGroup>() is { } inner)
                inner.padding = new RectOffset(12, 12, inner.padding.top, inner.padding.bottom);
            FixWidth(button, label, copy, copied);
            var line = go.AddComponent<CopyLine>();
            line.Init(button, label, copy, copied);
            return line;
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

    /// The small Copy button beside the launch-option line: Copied ✓ on it for a moment after
    /// each copy, then Copy again.
    internal sealed class CopyLine : MonoBehaviour
    {
        public KButton Button = null!;
        LocText label = null!;
        string copy = "", copied = "";
        float until;

        public void Init(KButton button, LocText label, string copy, string copied)
        {
            Button = button;
            this.label = label;
            this.copy = copy;
            this.copied = copied;
            label.text = copy;
            enabled = false;
        }

        public void ShowCopied(float seconds)
        {
            label.text = copied;
            until = Time.unscaledTime + seconds;
            enabled = true;
        }

        void Update()
        {
            if (Time.unscaledTime < until) return;
            label.text = copy;
            enabled = false;
        }
    }

    /// Shows an InstallRun's outcome on the main thread once it ends, from the dialog's Update;
    /// it goes with the dialog when the dialog closes first.
    internal sealed class InstallWatch : MonoBehaviour
    {
        InstallRun? run;
        System.Action? finish;

        public void Watch(InstallRun run, System.Action finish)
        {
            this.run = run;
            this.finish = finish;
            enabled = true;
        }

        void Update()
        {
            if (run == null || !run.Finished) return;
            var then = finish;
            run = null;
            finish = null;
            enabled = false;
            then?.Invoke();
        }
    }
}
