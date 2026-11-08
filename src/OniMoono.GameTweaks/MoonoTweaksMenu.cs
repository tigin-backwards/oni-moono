using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using STRINGS;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniMoono.GameTweaks
{
    /// The main menu's "Moono Tweaks" button, directly below Mods (above Options when there is no
    /// Mods button), and the screen it opens: the player's options (OniMoono.Host.MoonoTweaksMenu
    /// gives the rows, from the one catalog GameTweakNames.Rows), in sections, each with an on/off
    /// toggle (or, for a row with choices, a button that steps through them), saved to the player's settings file and applied by the game's own restart
    /// (App.Restart, through the player's Restarter).
    ///
    /// Everything on it is the game's: the button is made as MainMenu makes its own, and the
    /// screen is an instance of the Mods screen's prefab (its window, list, rows, toggles and
    /// buttons) whose ModsScreen behaviour is swapped for MoonoTweaksScreen before it wakes.
    ///
    /// Its text is in the game's language: each string has a key (STRINGS.MOONO.TWEAKS....,
    /// OniMoono.Host.GameTweaks.TweakStrings), and Localize reads the player's
    /// translations/CODE.po for the game's locale with the game's own reader, as a mod reads its
    /// translations, falling back to English string by string, and puts the result in the game's
    /// string table (Strings).
    public static class MoonoTweaksMenu
    {
        /// The rows' text in the game's language (Localize), and the launch options (`Notes`).
        internal static string[] Ids, Groups, Titles, Effects, Details, Gains, Notes;
        internal static bool[] ChangesGameplay, BugFixes, Locked;
        /// The rows whose tweak this game version can't run (also Locked: not changeable, not saved).
        internal static bool[] Unavailable;
        /// Each row's value: 0 or 1 for a toggle, else the index in its Choices.
        internal static int[] Running, Defaults, Saved;
        internal static string[][] Choices;
        internal static string SettingsPath;
        internal static Func<string[], int[], string> Save;
        /// The rows' text as keys ("" for none); every key, its English and its text in the
        /// game's language (Localize).
        static string[] groupKeys, titleKeys, effectKeys, detailKeys, gainKeys;
        static string[][] choiceKeys;
        static string[] stringKeys, english, text;
        static Dictionary<string, int> keyIndex;
        static Func<Dictionary<string, string>, string[]> resolve;
        static string translationsDir, loaded;
        const string UiPrefix = "STRINGS.MOONO.TWEAKS.UI.";

        /// One entry per row, in order: its section (`groups`), title and one-line effect, and
        /// its tooltip's parts: how it works (`details`), its measured gain ("" for none) and the
        /// lines after it (where it was measured, the launch option). `choices`: a row's values
        /// when it has more than on and off (null for a toggle). `running`: each option as this
        /// run has it (0 or 1 for a toggle, else the index in its choices); `defaults`: as Reset
        /// to defaults sets it; `locked`: set by the command line (shown, not changeable, labelled
        /// LAUNCH OPTION); `unavailable`: locked too, as the tweak can't run on this game version
        /// (greyed out, labelled NOT AVAILABLE). `save(ids, values)` writes the changeable rows to
        /// the settings file, returning why it could not or null. The text (`groups` to `gains`,
        /// `choices`) is given as keys ("" for none) into `keys`, whose English is `englishTexts`;
        /// `resolveTexts` gives every key's text from a .po's strings (key to text, English where
        /// it has none), read from the folder `translations`.
        public static void Install(Harmony harmony, string[] ids, string[] groups, string[] titles, string[] effects,
            string[] details, string[] gains, string[] notes, bool[] changesGameplay, bool[] bugFixes, string[][] choices, int[] running, int[] defaults, bool[] locked,
            bool[] unavailable, string settingsPath, Func<string[], int[], string> save,
            string[] keys, string[] englishTexts, Func<Dictionary<string, string>, string[]> resolveTexts, string translations)
        {
            Ids = ids;
            groupKeys = groups;
            titleKeys = titles;
            effectKeys = effects;
            detailKeys = details;
            gainKeys = gains;
            Notes = notes;
            ChangesGameplay = changesGameplay;
            BugFixes = bugFixes;
            choiceKeys = choices;
            stringKeys = keys;
            english = englishTexts;
            keyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < keys.Length; i++) keyIndex[keys[i]] = i;
            resolve = resolveTexts;
            translationsDir = translations;
            Localize();
            Running = running;
            Defaults = defaults;
            Saved = (int[])running.Clone();
            Locked = locked;
            Unavailable = unavailable;
            SettingsPath = settingsPath;
            Save = save;
            harmony.Patch(AccessTools.Method(typeof(MainMenu), "OnPrefabInit"), postfix: new HarmonyMethod(typeof(MoonoTweaksMenu), nameof(AddButton)));
        }

        /// The translation for the game's language: translations/CODE.po for its locale's code
        /// (Localization.GetLocale().Code: "zh" for the game's Simplified Chinese, zh_klei, and
        /// for any translation mod whose language contains "zh"; a mod's own code for a language
        /// the game does not know), else for the code's language part ("pt" for "pt_BR"); null
        /// in English (no locale) or when there is none.
        static string TranslationFile()
        {
            string code;
            try
            {
                code = Localization.GetLocale()?.Code;
            }
            catch (Exception)
            {
                return null;
            }
            if (string.IsNullOrEmpty(code)) return null;
            int cut = code.IndexOfAny(new[] { '_', '-' });
            foreach (var name in cut > 0 ? new[] { code, code.Substring(0, cut) } : new[] { code })
            {
                var path = System.IO.Path.Combine(translationsDir, name + ".po");
                if (System.IO.File.Exists(path)) return path;
            }
            return null;
        }

        /// Every string in the game's language, into the game's string table and the rows' text:
        /// at install, and again when the menu and the screen open (the language is loaded after
        /// the player installs its tweaks). A string the .po lacks, leaves empty or whose
        /// placeholders differ stays English.
        internal static void Localize()
        {
            var path = TranslationFile();
            var next = english;
            if (path != null)
            {
                try
                {
                    next = resolve(Localization.LoadStringsFile(path, false));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Moono] Moono Tweaks: {path} could not be read, English instead: {e.Message}");
                }
            }
            if (path != loaded)
            {
                loaded = path;
                if (path != null) Debug.Log($"[Moono] Moono Tweaks: strings from {path}");
            }
            if (next != text)
            {
                text = next;
                for (int i = 0; i < stringKeys.Length; i++) Strings.Add(stringKeys[i], text[i]);
            }
            string[] Texts(string[] keys) => Array.ConvertAll(keys, Text);
            Groups = Texts(groupKeys);
            Titles = Texts(titleKeys);
            Effects = Texts(effectKeys);
            Details = Texts(detailKeys);
            Gains = Texts(gainKeys);
            Choices = Array.ConvertAll(choiceKeys, c => c == null ? null : Texts(c));
        }

        /// A key's text in the game's language ("" for no key).
        internal static string Text(string key) =>
            string.IsNullOrEmpty(key) ? "" : keyIndex.TryGetValue(key, out int i) ? text[i] : key;

        /// One of the screen's own labels (STRINGS.MOONO.TWEAKS.UI.NAME) in the game's language.
        internal static string Ui(string name) => Text(UiPrefix + name);

        /// After MainMenu.OnPrefabInit has made its buttons: one more, as its MakeButton makes them
        /// (the menu's button prefab, normal style, 14 pt).
        static void AddButton(MainMenu __instance)
        {
            Localize();
            // Every mod has made its patches by now (KMod loads them before the first menu).
            ModOverlaps.Refresh();
            try
            {
                var menu = Traverse.Create(__instance);
                var prefab = menu.Field("buttonPrefab").GetValue<KButton>();
                var parent = menu.Field("buttonParent").GetValue<GameObject>();
                var style = menu.Field("normalButtonStyle").GetValue<ColorStyleSetting>();
                var button = Util.KInstantiateUI<KButton>(prefab.gameObject, parent, force_active: true);
                button.name = "MoonoTweaksButton";
                button.onClick += () => Open(__instance);
                var image = button.GetComponent<KImage>();
                image.colorStyleSetting = style;
                image.ApplyColorStyleSetting();
                var label = button.GetComponentInChildren<LocText>();
                label.text = Ui("TITLE");
                label.fontSize = 14;
                int index = -1;
                foreach (Transform child in parent.transform)
                {
                    var text = child == button.transform ? null : child.GetComponentInChildren<LocText>()?.text;
                    if (text == (string)UI.FRONTEND.MODS.TITLE) index = child.GetSiblingIndex() + 1;
                    else if (index < 0 && text == (string)UI.FRONTEND.MAINMENU.OPTIONS) index = child.GetSiblingIndex();
                }
                if (index >= 0) button.transform.SetSiblingIndex(index);
                RectTransform overlaps = null;
                try
                {
                    overlaps = OverlapBadge.Attach(__instance, button, prefab, style, () => Open(__instance));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Moono] the overlap badge could not be added: " + e);
                }
                UpdateBadge.Attach(__instance, button, prefab, style, overlaps);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Moono] the Moono Tweaks button could not be added: " + e);
            }
        }

        static void Open(MainMenu menu)
        {
            // The Mods screen's prefab, instantiated inactive so that ModsScreen never wakes.
            var prefab = ScreenPrefabs.Instance.modsMenu.gameObject;
            bool active = prefab.activeSelf;
            prefab.SetActive(false);
            GameObject go;
            try
            {
                go = UnityEngine.Object.Instantiate(prefab, menu.transform.parent, worldPositionStays: false);
            }
            finally
            {
                prefab.SetActive(active);
            }
            go.name = "MoonoTweaksScreen";
            var mods = go.GetComponent<ModsScreen>();
            var screen = go.AddComponent<MoonoTweaksScreen>();
            screen.TakeOver(mods);
            UnityEngine.Object.DestroyImmediate(mods);
            go.SetActive(true);
        }
    }

    /// The Moono Tweaks screen, on the Mods screen's prefab (MoonoTweaksMenu.Open): a section
    /// heading per group, then its rows (title, one-line effect, toggle; how it works and its
    /// measured gain in the tooltip), under a search box that filters them; the footer says
    /// when changes apply beside Reset to defaults, Restart to apply and Close.
    public class MoonoTweaksScreen : KModalScreen
    {
        static string Title => MoonoTweaksMenu.Ui("TITLE");
        /// The warning colour of the gameplay-changing section's heading (amber), and of a
        /// high-grade mod overlap's icon and lines.
        internal const string Warning = "FFB040";
        /// The colour of a low-grade overlap's icon, and of a tweak's that steps aside: the body
        /// text's light grey.
        const string Neutral = "C8CBD3";
        const float RowIconSize = 20f;
        /// Row padding (above and below, and the left inset) and the spacing between rows: a
        /// little tighter than the Mods screen's 10 px, so that about ten rows fit.
        const int RowPadding = 7, RowInset = 16, RowSpacing = 6;

        KButton closeTitle, close, apply, reset;
        GameObject entryPrefab;
        Transform entryParent;
        LocText windowTitle;
        int[] shown;
        MultiToggle[] toggles;
        /// The choice rows' buttons (null for a toggle row).
        KButton[] choiceButtons;
        /// The search box (null when the game has none to copy), each row and the text a search
        /// looks in (Searchable), each section's heading with its rows (first, end), the line
        /// shown when nothing matches and the list's scroll view.
        KInputTextField search;
        HorizontalLayoutGroup searchRow;
        GameObject[] rows;
        string[] searchable;
        readonly List<(GameObject Heading, int First, int End)> sections = new List<(GameObject, int, int)>();
        GameObject noMatches;
        KScrollRect scroll;

        /// The Mods screen's serialized state: its KScreen settings and its parts.
        internal void TakeOver(ModsScreen mods)
        {
            for (var type = typeof(KModalScreen); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    field.SetValue(this, field.GetValue(mods));
            var parts = Traverse.Create(mods);
            closeTitle = parts.Field("closeButtonTitle").GetValue<KButton>();
            close = parts.Field("closeButton").GetValue<KButton>();
            apply = parts.Field("toggleAllButton").GetValue<KButton>();
            reset = parts.Field("workshopButton").GetValue<KButton>();
            entryPrefab = parts.Field("entryPrefab").GetValue<GameObject>();
            entryParent = parts.Field("entryParent").GetValue<Transform>();
            windowTitle = parts.Field("windowTitle").GetValue<LocText>();
        }

        protected override void OnActivate()
        {
            base.OnActivate();
            MoonoTweaksMenu.Localize();
            windowTitle.text = Title;
            windowTitle.ApplySettings();
            closeTitle.onClick += Close;
            close.onClick += Close;
            apply.GetComponentInChildren<LocText>().text = MoonoTweaksMenu.Ui("RESTART_TO_APPLY");
            apply.onClick += Apply;
            // The Mods screen's Workshop button, as Reset to defaults (wider for its label).
            reset.GetComponentInChildren<LocText>().text = MoonoTweaksMenu.Ui("RESET_TO_DEFAULTS");
            var resetRect = reset.GetComponent<RectTransform>();
            resetRect.sizeDelta = new Vector2(166f, resetRect.sizeDelta.y);
            reset.onClick += ResetToDefaults;
            reset.gameObject.SetActive(true);
            FooterNote(MoonoTweaksMenu.Ui("CHANGES_APPLY_ON_RESTART"));
            var list = entryParent.GetComponent<VerticalLayoutGroup>();
            if (list != null)
            {
                list.spacing = RowSpacing;
                list.padding = new RectOffset(list.padding.left, list.padding.right, RowSpacing, RowSpacing);
            }
            // Choices saved earlier in this run (closed with "Later") show as chosen.
            shown = (int[])MoonoTweaksMenu.Saved.Clone();
            toggles = new MultiToggle[shown.Length];
            choiceButtons = new KButton[shown.Length];
            rows = new GameObject[shown.Length];
            searchable = new string[shown.Length];
            string group = null;
            for (int i = 0; i < MoonoTweaksMenu.Ids.Length; i++)
            {
                if (MoonoTweaksMenu.Groups[i] != group)
                {
                    group = MoonoTweaksMenu.Groups[i];
                    sections.Add((Heading(group, MoonoTweaksMenu.ChangesGameplay[i], MoonoTweaksMenu.BugFixes[i]), i, i));
                }
                rows[i] = Row(i);
                searchable[i] = Searchable(i);
                var last = sections[sections.Count - 1];
                sections[sections.Count - 1] = (last.Heading, last.First, i + 1);
            }
            noMatches = NoMatches();
            scroll = entryParent.GetComponentInParent<KScrollRect>();
            SearchBox();
            Refresh();
            if (search != null) StartCoroutine(AlignSearch());
        }

        /// Once the screen is laid out: the search box's insets set so that the field starts at
        /// the rows' left edge and ends at their right edge.
        System.Collections.IEnumerator AlignSearch()
        {
            yield return null;
            RectTransform row = null;
            foreach (var r in rows)
                if (r.activeSelf)
                {
                    row = r.GetComponent<RectTransform>();
                    break;
                }
            if (row == null) yield break;
            Canvas.ForceUpdateCanvases();
            var rowCorners = new Vector3[4];
            var leftCorners = new Vector3[4];
            var rightCorners = new Vector3[4];
            row.GetWorldCorners(rowCorners);
            search.GetComponent<RectTransform>().GetWorldCorners(leftCorners);
            search.GetComponent<RectTransform>().GetWorldCorners(rightCorners);
            float scale = searchRow.transform.lossyScale.x;
            if (scale <= 0f) yield break;
            var padding = searchRow.padding;
            searchRow.padding = new RectOffset(
                padding.left + Mathf.RoundToInt((rowCorners[0].x - leftCorners[0].x) / scale),
                padding.right + Mathf.RoundToInt((rightCorners[2].x - rowCorners[2].x) / scale),
                padding.top, padding.bottom);
            LayoutRebuilder.MarkLayoutForRebuild(searchRow.GetComponent<RectTransform>());
        }

        /// What a search looks in: the row's title, its one-liner, its tooltip (without its
        /// colour tags) and choices, in the game's language, and its name (its `--game-tweak` name
        /// is in the tooltip's launch option line), lowercased as the game's searches lowercase
        /// (StringSearchableListUtil).
        static string Searchable(int i)
        {
            var parts = new List<string> { MoonoTweaksMenu.Ids[i], MoonoTweaksMenu.Titles[i], MoonoTweaksMenu.Effects[i],
                System.Text.RegularExpressions.Regex.Replace(Tooltip(i), "<[^<>]*>", "") };
            if (MoonoTweaksMenu.Choices[i] != null) parts.AddRange(MoonoTweaksMenu.Choices[i]);
            return string.Join("\n", parts).ToLowerInvariant();
        }

        /// Whether a row's text matches a search as the game's own searches match a list
        /// (StringSearchableListUtil.DoAnyTagsMatchFilter): it contains the whole query, or every
        /// word of it; an empty query matches every row.
        static bool Matches(string text, string query)
        {
            query = query.Trim().ToLowerInvariant();
            if (query.Length == 0 || text.Contains(query)) return true;
            foreach (var word in query.Split(' '))
                if (word.Length > 0 && !text.Contains(word)) return false;
            return true;
        }

        /// Shows the rows the search matches, and the headings of the sections that keep one;
        /// with none, a line saying so. The list goes back to its top.
        void Filter(string query)
        {
            query = query ?? "";
            bool any = false;
            for (int i = 0; i < rows.Length; i++)
            {
                bool show = Matches(searchable[i], query);
                if (rows[i].activeSelf != show) rows[i].SetActive(show);
                any |= show;
            }
            foreach (var (heading, first, end) in sections)
            {
                bool show = false;
                for (int i = first; i < end && !show; i++) show = rows[i].activeSelf;
                if (heading.activeSelf != show) heading.SetActive(show);
            }
            if (noMatches.activeSelf == any) noMatches.SetActive(!any);
            if (scroll != null) scroll.verticalNormalizedPosition = 1f;
        }

        /// The line in the list when the search matches nothing (hidden until then).
        GameObject NoMatches()
        {
            var entry = Entry("NoMatches");
            entry.GetReference<KImage>("BG").gameObject.SetActive(false);
            var band = entry.GetComponent<Image>();
            if (band != null) band.enabled = false;
            var highlight = entry.GetComponent<MultiToggle>();
            if (highlight != null) highlight.enabled = false;
            var title = entry.GetReference<LocText>("Title");
            var style = BodyStyle();
            if (style != null)
            {
                title.textStyleSetting = style;
                title.ApplySettings();
                title.GetComponent<SetTextStyleSetting>()?.SetStyle(style);
            }
            else title.fontSize = 12;
            title.text = MoonoTweaksMenu.Ui("NO_MATCHES");
            entry.GetReference<MultiToggle>("EnabledToggle").gameObject.SetActive(false);
            entry.GetReference<LocText>("Version").gameObject.SetActive(false);
            entry.gameObject.SetActive(false);
            return entry.gameObject;
        }

        /// The search box above the list: a copy of the Supply Closet's (KleiInventoryScreen's
        /// search field, in the locker prefab the main menu opens, without its clear button),
        /// with the game's "Search" placeholder. While it has the keyboard the screen is editing, as
        /// the game's own search fields make their screens (KScreen.isEditing): its keys type
        /// and reach no hotkey. Left out, with a log line, when the game has no such field.
        void SearchBox()
        {
            KInputTextField field = null;
            KButton clear = null;
            try
            {
                var inventory = ScreenPrefabs.Instance.LockerNavigator.GetComponent<LockerNavigator>().kleiInventoryScreen.GetComponent<KleiInventoryScreen>();
                var parts = Traverse.Create(inventory);
                field = parts.Field("searchField").GetValue<KInputTextField>();
                clear = parts.Field("clearSearchButton").GetValue<KButton>();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Moono] Moono Tweaks: no search box, the Supply Closet's could not be found: " + e.Message);
                return;
            }
            if (field == null)
            {
                Debug.LogWarning("[Moono] Moono Tweaks: no search box, the Supply Closet has none");
                return;
            }
            var panel = windowTitle.transform.parent.parent;
            var list = entryParent;
            while (list.parent != null && list.parent != panel) list = list.parent;
            if (list.parent != panel)
            {
                Debug.LogWarning("[Moono] Moono Tweaks: no search box, the list is not in the screen's panel");
                return;
            }
            // The field with its clear button when they share a parent (the closet's search bar).
            var source = clear != null && clear.transform.parent == field.transform.parent ? field.transform.parent.gameObject : field.gameObject;
            var bar = new GameObject("SearchBar", typeof(RectTransform));
            bar.transform.SetParent(panel, worldPositionStays: false);
            bar.transform.SetSiblingIndex(list.GetSiblingIndex());
            var row = searchRow = bar.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset(RowInset, RowInset, 8, 4);
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = true;
            row.childForceExpandHeight = false;
            var size = bar.AddComponent<LayoutElement>();
            size.flexibleHeight = 0f;
            var copy = Instantiate(source, bar.transform, worldPositionStays: false);
            copy.name = "Search";
            copy.SetActive(true);
            var copyLayout = copy.GetComponent<LayoutElement>();
            if (copyLayout == null) copyLayout = copy.AddComponent<LayoutElement>();
            copyLayout.flexibleWidth = 1f;
            copyLayout.minHeight = Mathf.Max(copyLayout.minHeight, 28f);
            copyLayout.preferredHeight = Mathf.Max(copyLayout.preferredHeight, 28f);
            search = copy.GetComponentInChildren<KInputTextField>(true);
            // The closet's events stay with the closet.
            search.onValueChanged = new TMP_InputField.OnChangeEvent();
            search.onEndEdit = new TMP_InputField.SubmitEvent();
            search.onSubmit = new TMP_InputField.SubmitEvent();
            search.OnValueChangesPaused = null;
            search.text = "";
            if (search.placeholder != null && search.placeholder.GetComponent<TextMeshProUGUI>() is { } placeholder)
                placeholder.text = UI.UISIDESCREENS.TREEFILTERABLESIDESCREEN.SEARCH_PLACEHOLDER;
            search.onValueChanged.AddListener(Filter);
            search.onFocus = (System.Action)Delegate.Combine(search.onFocus, (System.Action)(() => isEditing = true));
            search.onEndEdit.AddListener(_ => isEditing = false);
            // No clear button: the screen's close button sits right above it, in the same style.
            // The field spans the rows alone, as the wardrobe's search does in the Supply Closet.
            if (clear != null && source != field.gameObject)
                foreach (var button in copy.GetComponentsInChildren<KButton>(true))
                    if (button.name == clear.name) button.gameObject.SetActive(false);
        }

        HierarchyReferences Entry(string name)
        {
            var entry = Util.KInstantiateUI<HierarchyReferences>(entryPrefab, entryParent.gameObject);
            entry.name = name;
            // The rows keep their order: no dragging.
            var drag = entry.GetComponent<DragMe>();
            if (drag != null) Destroy(drag);
            entry.transform.Find("DragReorderIndicator")?.gameObject.SetActive(false);
            entry.GetReference<KButton>("ManageButton").gameObject.SetActive(false);
            var bg = entry.GetReference<KImage>("BG");
            bg.defaultState = KImage.ColorSelector.Inactive;
            bg.ColorState = KImage.ColorSelector.Inactive;
            var layout = entry.GetComponent<HorizontalLayoutGroup>();
            if (layout != null) layout.padding = new RectOffset(RowInset, layout.padding.right, RowPadding, RowPadding);
            return entry;
        }

        /// A section's heading, as Klei heads the sections of its own screens (Game options, the
        /// destination screen): its name in capitals, GRAYSTROKE at 18 pt (style_titleTextLarge's
        /// size), regular and white, on the list's background with no row panel or band. The
        /// gameplay-changing sections' (Changes gameplay, Bug fixes) are amber, each with a line
        /// of body text under it.
        GameObject Heading(string group, bool changesGameplay, bool bugFixes)
        {
            var entry = Entry("Heading " + group);
            entry.GetReference<KImage>("BG").gameObject.SetActive(false);
            var band = entry.GetComponent<Image>();
            if (band != null) band.enabled = false;
            // Not a row: no hover highlight (the row's own MultiToggle).
            var highlight = entry.GetComponent<MultiToggle>();
            if (highlight != null) highlight.enabled = false;
            var layout = entry.GetComponent<HorizontalLayoutGroup>();
            if (layout != null) layout.padding = new RectOffset(RowInset, layout.padding.right, 12, 2);
            var title = entry.GetReference<LocText>("Title");
            title.text = group.ToUpperInvariant();
            title.fontSize = 18;
            title.color = changesGameplay ? Util.ColorFromHex(Warning) : Color.white;
            if (changesGameplay) BodyLine(title, MoonoTweaksMenu.Ui(bugFixes ? "BUG_FIXES_NOTE" : "CHANGES_GAMEPLAY_NOTE"));
            entry.GetReference<MultiToggle>("EnabledToggle").gameObject.SetActive(false);
            entry.GetReference<LocText>("Version").gameObject.SetActive(false);
            entry.gameObject.SetActive(true);
            return entry.gameObject;
        }

        /// A row's tooltip, styled as the game's effect tooltips: how it works, then its measured
        /// gain in the game's positive colour, then where that was measured and the launch option
        /// (if it has one).
        static string Tooltip(int i)
        {
            var text = MoonoTweaksMenu.Details[i];
            // A tweak this game version can't run says so first.
            if (MoonoTweaksMenu.Unavailable[i])
                text = $"<color=#{Warning}>{MoonoTweaksMenu.Ui("NOT_AVAILABLE_TIP")}</color>" + (text.Length > 0 ? "\n\n" + text : "");
            if (MoonoTweaksMenu.Gains[i].Length > 0)
                text += (text.Length > 0 ? "\n\n" : "") + $"<color={Constants.POSITIVE_COLOR_STR}>{MoonoTweaksMenu.Gains[i]}</color>";
            if (MoonoTweaksMenu.Notes[i].Length > 0)
                text += (text.Length > 0 ? "\n" : "") + string.Format(MoonoTweaksMenu.Ui("LAUNCH_OPTION_LINE"), MoonoTweaksMenu.Notes[i]);
            // The mods that patch what the tweak changes (ModOverlaps), last.
            var overlaps = ModOverlapsCore.Tooltip(ModOverlaps.For(MoonoTweaksMenu.Ids[i]), MoonoTweaksMenu.Ui, Warning);
            if (overlaps.Length > 0) text += (text.Length > 0 ? "\n\n" : "") + overlaps;
            return text;
        }

        /// The icon of a row with mod overlaps (and of the main menu's overlap badge): the game's
        /// warning sign, amber for a high-grade overlap and light grey for a low one; for a tweak
        /// that only steps aside, the game's disabled sign in light grey.
        internal static Image OverlapIcon(Transform parent, OverlapGrade grade, float size)
        {
            var icon = new GameObject("OverlapIcon", typeof(RectTransform)).AddComponent<Image>();
            icon.transform.SetParent(parent, worldPositionStays: false);
            icon.preserveAspect = true;
            var sprite = Assets.GetSprite(grade == OverlapGrade.StepsAside ? "icon_priority_disabled" : "iconWarning");
            if (sprite != null) icon.sprite = sprite;
            icon.color = Util.ColorFromHex(grade == OverlapGrade.High ? Warning : Neutral);
            var layout = icon.gameObject.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = size;
            layout.minHeight = layout.preferredHeight = size;
            layout.flexibleWidth = layout.flexibleHeight = 0f;
            return icon;
        }

        GameObject Row(int i)
        {
            var entry = Entry(MoonoTweaksMenu.Ids[i]);
            bool locked = MoonoTweaksMenu.Locked[i];
            var title = entry.GetReference<LocText>("Title");
            // The Mods screen's title font, GRAYSTROKE REGULAR SDF: its own glyph for "f" is a
            // narrow, cap-height F (the original shows "conFiguration" on that screen too), and
            // "%" overhangs its advance by about a third of a space, so "% " looks unspaced.
            title.text = MoonoTweaksMenu.Titles[i];
            // White, as Klei's headings over body text (the Mods screen's are grey-blue).
            title.color = Color.white;
            var tooltip = Tooltip(i);
            entry.GetReference<ToolTip>("Description").toolTip = tooltip;
            var effect = BodyLine(title, MoonoTweaksMenu.Effects[i]);
            if (effect.GetComponent<ToolTip>() is { } effectTip)
            {
                effectTip.toolTip = tooltip;
                // The rest of the row shows it too: its background, under the gaps between and
                // around the lines and the label (the toggle or choice button keeps its own).
                RowTip(entry.GetReference<KImage>("BG").gameObject, effectTip, tooltip);
            }
            // The label beside the toggle: who set a row the launch options set (the section
            // heading says which tweaks change gameplay).
            var label = entry.GetReference<LocText>("Version");
            bool unavailable = MoonoTweaksMenu.Unavailable[i];
            if (locked)
            {
                label.text = MoonoTweaksMenu.Ui(unavailable ? "NOT_AVAILABLE" : "LAUNCH_OPTION");
                label.fontSize = 12;
                // One line: the effect beside it wraps instead.
                label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
                var layout = label.GetComponent<LayoutElement>();
                if (layout != null) layout.flexibleWidth = 0f;
            }
            else label.gameObject.SetActive(false);
            var toggle = entry.GetReference<MultiToggle>("EnabledToggle");
            // A tweak with mod overlaps: an icon left of its toggle, showing the row's tooltip.
            if (ModOverlapsCore.Worst(ModOverlaps.For(MoonoTweaksMenu.Ids[i])) is { } grade)
            {
                var icon = OverlapIcon(toggle.transform.parent, grade, RowIconSize);
                icon.transform.SetSiblingIndex(toggle.transform.GetSiblingIndex());
                if (effect.GetComponent<ToolTip>() is { } like) RowTip(icon.gameObject, like, tooltip);
            }
            if (MoonoTweaksMenu.Choices[i] != null)
            {
                ChoiceRow(entry, i, toggle, title, effect, locked);
                entry.gameObject.SetActive(true);
                return entry.gameObject;
            }
            toggles[i] = toggle;
            toggle.ChangeState(shown[i]);
            var tip = toggle.GetComponent<ToolTip>();
            if (locked)
            {
                // Dimmed, and the toggle does nothing.
                var bg = entry.GetReference<KImage>("BG");
                bg.defaultState = KImage.ColorSelector.Disabled;
                bg.ColorState = KImage.ColorSelector.Disabled;
                title.color = new Color(title.color.r, title.color.g, title.color.b, 0.55f);
                // (A group, not its colour: the text style sets the colour again when it wakes.)
                effect.gameObject.AddComponent<CanvasGroup>().alpha = 0.55f;
                var dim = toggle.gameObject.AddComponent<CanvasGroup>();
                dim.alpha = 0.45f;
                if (tip != null) tip.OnToolTip = () => MoonoTweaksMenu.Ui(unavailable ? "NOT_AVAILABLE_TIP" : shown[i] != 0 ? "LOCKED_ON" : "LOCKED_OFF");
            }
            else
            {
                toggle.onClick += () =>
                {
                    shown[i] = 1 - shown[i];
                    toggle.ChangeState(shown[i]);
                    Refresh();
                };
                if (tip != null) tip.OnToolTip = () => shown[i] != 0 ? UI.FRONTEND.MODS.TOOLTIPS.ENABLED : UI.FRONTEND.MODS.TOOLTIPS.DISABLED;
            }
            entry.gameObject.SetActive(true);
            return entry.gameObject;
        }

        /// A row with choices (the GPU's): the Mods rows' Manage button in the toggle's place,
        /// labelled with the choice, stepping to the next one when clicked.
        void ChoiceRow(HierarchyReferences entry, int i, MultiToggle toggle, LocText title, LocText effect, bool locked)
        {
            toggle.gameObject.SetActive(false);
            var button = entry.GetReference<KButton>("ManageButton");
            button.gameObject.SetActive(true);
            choiceButtons[i] = button;
            var rect = button.GetComponent<RectTransform>();
            var size = button.GetComponent<LayoutElement>();
            if (size != null) size.minWidth = size.preferredWidth = 120f;
            else rect.sizeDelta = new Vector2(120f, rect.sizeDelta.y);
            var tip = button.GetComponent<ToolTip>();
            if (tip == null) tip = button.gameObject.AddComponent<ToolTip>();
            ShowChoice(i);
            if (locked)
            {
                button.isInteractable = false;
                title.color = new Color(title.color.r, title.color.g, title.color.b, 0.55f);
                effect.gameObject.AddComponent<CanvasGroup>().alpha = 0.55f;
                tip.toolTip = "";
                tip.OnToolTip = () => string.Format(MoonoTweaksMenu.Ui("LOCKED_CHOICE"), MoonoTweaksMenu.Choices[i][shown[i]]);
            }
            else
            {
                tip.toolTip = "";
                tip.OnToolTip = () => string.Format(MoonoTweaksMenu.Ui("NEXT_CHOICE"), string.Join(MoonoTweaksMenu.Ui("LIST_SEPARATOR"), MoonoTweaksMenu.Choices[i]));
                button.onClick += () =>
                {
                    shown[i] = (shown[i] + 1) % MoonoTweaksMenu.Choices[i].Length;
                    ShowChoice(i);
                    Refresh();
                };
            }
        }

        /// A row's tooltip on another of its parts, set up and placed as `like` (at the pointer).
        /// The part is a sibling of the title, the effect and the toggle, not their parent, so a
        /// hover over one of those raises only that one's tooltip.
        static void RowTip(GameObject part, ToolTip like, string text)
        {
            var tip = part.GetComponent<ToolTip>();
            if (tip == null) tip = part.AddComponent<ToolTip>();
            tip.toolTipPosition = like.toolTipPosition;
            tip.tooltipPivot = like.tooltipPivot;
            tip.tooltipPositionOffset = like.tooltipPositionOffset;
            tip.parentPositionAnchor = like.parentPositionAnchor;
            tip.SizingSetting = like.SizingSetting;
            tip.WrapWidth = like.WrapWidth;
            tip.toolTip = text;
        }

        void ShowChoice(int i)
        {
            var label = choiceButtons[i].GetComponentInChildren<LocText>();
            label.text = MoonoTweaksMenu.Choices[i][shown[i]].ToUpperInvariant();
        }

        /// The game's small body text (style_bodyTextWhiteSmall: NotoSans-Regular, 12 pt, white),
        /// as its screens set prose under a GRAYSTROKE heading; null when it isn't loaded.
        static TextStyleSetting bodyStyle;
        static TextStyleSetting BodyStyle()
        {
            if (bodyStyle == null)
                foreach (var style in Resources.FindObjectsOfTypeAll<TextStyleSetting>())
                    if (style.name == "style_bodyTextWhiteSmall") bodyStyle = style;
            return bodyStyle;
        }

        /// A line of body text under a row's title: the title and a copy of it, restyled, stacked
        /// in the title's place.
        static LocText BodyLine(LocText title, string text)
        {
            var row = title.transform.parent;
            var stack = new GameObject("TitleAndEffect", typeof(RectTransform));
            stack.transform.SetParent(row, worldPositionStays: false);
            stack.transform.SetSiblingIndex(title.transform.GetSiblingIndex());
            var column = stack.AddComponent<VerticalLayoutGroup>();
            column.childControlWidth = column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            column.spacing = 2f;
            var layout = stack.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;
            title.transform.SetParent(stack.transform, worldPositionStays: false);
            var line = Instantiate(title.gameObject, stack.transform, worldPositionStays: false).GetComponent<LocText>();
            line.name = "Effect";
            var style = BodyStyle();
            if (style != null)
            {
                line.textStyleSetting = style;
                line.ApplySettings();
                line.GetComponent<SetTextStyleSetting>()?.SetStyle(style);
            }
            else line.fontSize = 12;
            line.text = text;
            return line;
        }

        /// A line of text at the footer's left, beside its buttons (a copy of the rows' label): one
        /// line in the room the three buttons leave.
        void FooterNote(string text)
        {
            var source = entryPrefab.GetComponent<HierarchyReferences>().GetReference<LocText>("Version");
            var note = Instantiate(source.gameObject, close.transform.parent, worldPositionStays: false);
            note.name = "RestartNote";
            note.SetActive(true);
            var layout = note.GetComponent<LayoutElement>();
            if (layout == null) layout = note.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            var rect = note.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(14f, 0f);
            rect.sizeDelta = new Vector2(222f, 0f);
            var label = note.GetComponent<LocText>();
            label.text = text;
            label.fontSize = 12;
            label.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        }

        static bool Differ(int[] a, int[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return true;
            return false;
        }

        /// "Restart to apply" shows once the choices differ from what this run has; Reset to
        /// defaults is available while a changeable row is off its default.
        void Refresh()
        {
            apply.gameObject.SetActive(Differ(shown, MoonoTweaksMenu.Running));
            bool offDefault = false;
            for (int i = 0; i < shown.Length; i++)
                if (!MoonoTweaksMenu.Locked[i] && shown[i] != MoonoTweaksMenu.Defaults[i]) offDefault = true;
            reset.isInteractable = offDefault;
        }

        /// Every row the launch options did not set back to its default, as if toggled.
        void ResetToDefaults()
        {
            for (int i = 0; i < shown.Length; i++)
            {
                if (MoonoTweaksMenu.Locked[i]) continue;
                shown[i] = MoonoTweaksMenu.Defaults[i];
                if (choiceButtons[i] != null) ShowChoice(i);
                else toggles[i].ChangeState(shown[i]);
            }
            Refresh();
        }

        /// The changeable rows to the settings file; false (after saying why) when it failed.
        bool SaveShown()
        {
            var ids = new List<string>();
            var values = new List<int>();
            for (int i = 0; i < shown.Length; i++)
            {
                if (MoonoTweaksMenu.Locked[i]) continue;
                ids.Add(MoonoTweaksMenu.Ids[i]);
                values.Add(shown[i]);
            }
            var error = MoonoTweaksMenu.Save(ids.ToArray(), values.ToArray());
            if (error != null)
            {
                KMod.Manager.Dialog(gameObject, Title, string.Format(MoonoTweaksMenu.Ui("SAVE_FAILED"), MoonoTweaksMenu.SettingsPath, error));
                return false;
            }
            for (int i = 0; i < shown.Length; i++)
                if (!MoonoTweaksMenu.Locked[i]) MoonoTweaksMenu.Saved[i] = shown[i];
            return true;
        }

        void Apply()
        {
            if (SaveShown()) App.instance.Restart();
        }

        /// Close (or Escape): the choices are kept; when they differ from this run's, the game's
        /// confirm dialog offers the restart that applies them.
        void Close()
        {
            if (Differ(shown, MoonoTweaksMenu.Saved) && !SaveShown()) return;
            if (Differ(MoonoTweaksMenu.Saved, MoonoTweaksMenu.Running))
                KMod.Manager.Dialog(gameObject, Title, MoonoTweaksMenu.Ui("SAVED_RESTART"),
                    UI.FRONTEND.MOD_DIALOGS.RESTART.OK, App.instance.Restart, MoonoTweaksMenu.Ui("LATER"), Deactivate);
            else Deactivate();
        }

        public override void OnKeyDown(KButtonEvent e)
        {
            if (!e.Consumed && e.TryConsume(global::Action.Escape)) Close();
            base.OnKeyDown(e);
        }
    }
}
