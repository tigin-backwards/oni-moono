using System;
using UnityEngine;
using UnityEngine.UI;

namespace OniMoono.GameTweaks
{
    /// The main menu's update badge (OniMoono.Host.UpdateCheck finds the release): a Gassy Moo
    /// with "Moo! An update is available" and the new release's tag, beside the Moono Tweaks
    /// button, or above the news tiles when those take that place. Clicking it opens the release's
    /// page; hovering it shows the notes of every newer release in the game's tooltip. Our own
    /// element on the menu, made as MainMenu makes its buttons (a copy of its button prefab in
    /// the normal button style), so it hovers, presses and sounds as they do: nothing of Klei's
    /// is changed.
    public sealed class UpdateBadge : MonoBehaviour
    {
        /// The host's result: null while the check runs, empty when there is nothing newer (or it
        /// failed), else [tag, release page, notes (TMP rich text), "1" when the notes were cut].
        static Func<string[]> poll;
        const float Gap = 8f, IconSize = 40f;

        public static void Install(Func<string[]> result) => poll = result;

        RectTransform anchor, rect;
        /// The overlap badge (OverlapBadge), between the button and this one while it shows.
        internal RectTransform after;
        Transform motd;
        ToolTip tip;
        string url;
        bool built;

        /// From MoonoTweaksMenu.AddButton: the badge for this menu, hidden until there is an
        /// update; `prefab` and `style` are the menu's button prefab and normal button style.
        internal static void Attach(MainMenu menu, KButton tweaksButton, KButton prefab, ColorStyleSetting style, RectTransform overlaps)
        {
            if (poll == null) return;
            var button = Util.KInstantiateUI<KButton>(prefab.gameObject, menu.gameObject, force_active: true);
            var go = button.gameObject;
            go.name = "MoonoUpdateBadge";
            var image = go.GetComponent<KImage>();
            image.colorStyleSetting = style;
            image.ApplyColorStyleSetting();
            var badge = go.AddComponent<UpdateBadge>();
            button.onClick += badge.Open;
            badge.anchor = tweaksButton.GetComponent<RectTransform>();
            badge.after = overlaps;
            badge.motd = Find(menu.transform, "MOTD");
            badge.Build(button);
            go.SetActive(false);
            badge.enabled = true;
            // A watcher on the menu shows the badge when the check has an answer.
            menu.gameObject.AddComponent<UpdateBadgeWatcher>().badge = badge;
        }

        internal static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform child in t)
            {
                var found = Find(child, name);
                if (found != null) return found;
            }
            return null;
        }

        void Build(KButton button)
        {
            rect = (RectTransform)transform;
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            // The prefab's own label: a source for ours, then out of the way.
            var source = button.GetComponentInChildren<LocText>(true);
            var buttonHeight = rect.rect.height;
            foreach (var old in GetComponents<LayoutGroup>()) DestroyImmediate(old);
            var row = gameObject.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset(8, 14, 6, 6);
            row.spacing = 8f;
            row.childAlignment = TextAnchor.MiddleLeft;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            var fit = gameObject.GetComponent<ContentSizeFitter>() ?? gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            // As tall as the menu's buttons, so it lines up with their row.
            var height = gameObject.GetComponent<LayoutElement>() ?? gameObject.AddComponent<LayoutElement>();
            height.ignoreLayout = false;
            height.minWidth = height.preferredWidth = height.flexibleWidth = -1f;
            height.minHeight = -1f;
            height.preferredHeight = anchor != null ? anchor.rect.height : buttonHeight;

            // The Gassy Moo, as the game draws it in its codex and menus.
            var icon = new GameObject("Moo", typeof(RectTransform)).AddComponent<Image>();
            icon.transform.SetParent(transform, worldPositionStays: false);
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            try
            {
                // The Moo's "ui" symbol is in its build file (the codex's portrait).
                icon.sprite = Def.GetUISpriteFromMultiObjectAnim(Assets.GetAnim("gassy_moo_build_kanim"));
            }
            catch (Exception)
            {
                icon.gameObject.SetActive(false);
            }
            var iconSize = icon.gameObject.AddComponent<LayoutElement>();
            iconSize.minWidth = iconSize.preferredWidth = IconSize;
            iconSize.minHeight = iconSize.preferredHeight = IconSize;

            var column = new GameObject("Text", typeof(RectTransform));
            column.transform.SetParent(transform, worldPositionStays: false);
            var stack = column.AddComponent<VerticalLayoutGroup>();
            stack.spacing = 1f;
            stack.childAlignment = TextAnchor.MiddleLeft;
            stack.childControlWidth = stack.childControlHeight = true;
            stack.childForceExpandWidth = stack.childForceExpandHeight = false;

            Line(source, column.transform, "Headline", MoonoTweaksMenu.Ui("UPDATE_HEADLINE"), 14, Color.white, heading: true);
            Line(source, column.transform, "Version", "", 12, Util.ColorFromHex("9DE07A"), heading: false);
            // The prefab's label goes once our lines are made from it.
            source.gameObject.SetActive(false);
            source.transform.SetParent(null, false);
            Destroy(source.gameObject);

            tip = gameObject.AddComponent<ToolTip>();
            tip.SizingSetting = ToolTip.ToolTipSizeSetting.MaxWidthWrapContent;
            tip.WrapWidth = 440f;
            tip.toolTipPosition = ToolTip.TooltipPosition.Custom;
            tip.tooltipPivot = new Vector2(0f, 0f);
            tip.parentPositionAnchor = new Vector2(0f, 1f);
            tip.tooltipPositionOffset = new Vector2(0f, 8f);
            built = true;
        }

        /// A line of text: a copy of the menu button's label (its heading font) or, for body
        /// text, restyled as the game's small body text.
        internal static LocText Line(LocText source, Transform parent, string name, string text, float size, Color color, bool heading)
        {
            var go = Instantiate(source.gameObject, parent, worldPositionStays: false);
            go.name = name;
            var label = go.GetComponent<LocText>();
            if (!heading)
            {
                foreach (var style in Resources.FindObjectsOfTypeAll<TextStyleSetting>())
                    if (style.name == "style_bodyTextWhiteSmall")
                    {
                        label.textStyleSetting = style;
                        label.ApplySettings();
                        go.GetComponent<SetTextStyleSetting>()?.SetStyle(style);
                        break;
                    }
            }
            label.raycastTarget = false;
            label.fontSize = size;
            label.color = color;
            label.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            label.text = text;
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
            return label;
        }

        /// The check's answer: shows the badge when there is an update. False while the check
        /// runs.
        internal bool Show()
        {
            var found = poll?.Invoke();
            if (found == null) return false;
            if (found.Length < 4 || !built) return true;
            url = found[1];
            transform.Find("Text/Headline").GetComponent<LocText>().text = MoonoTweaksMenu.Ui("UPDATE_HEADLINE");
            transform.Find("Text/Version").GetComponent<LocText>().text = found[0];
            var notes = found[2];
            if (found[3] == "1") notes += "\n\n<i>" + MoonoTweaksMenu.Ui("UPDATE_MORE") + "</i>";
            tip.ClearMultiStringTooltip();
            tip.AddMultiStringTooltip(notes, PluginAssets.Instance.defaultTextStyleSetting);
            gameObject.SetActive(true);
            Place();
            return true;
        }

        void LateUpdate() => Place();

        /// Right of the Moono Tweaks button, or of the overlap badge while that shows.
        void Place() => PlaceRight(rect, after != null && after.gameObject.activeInHierarchy ? after : anchor, motd);

        /// A badge right of `anchor`; moved up to clear the news tiles (and the BRUTAL credit
        /// drawn left of them) when it would reach into their row.
        internal static void PlaceRight(RectTransform rect, RectTransform anchor, Transform motd)
        {
            if (anchor == null || rect == null) return;
            var parent = (RectTransform)rect.parent;
            var corners = new Vector3[4];
            anchor.GetWorldCorners(corners);
            Vector3 right = parent.InverseTransformPoint(new Vector3(corners[2].x, (corners[0].y + corners[1].y) / 2f, 0f));
            var size = rect.rect.size;
            float x = right.x + Gap, y = right.y;
            if (motd != null && motd.gameObject.activeInHierarchy && motd is RectTransform tiles)
            {
                // The top of the news tiles' row (its visible children), in the menu's space.
                float top = float.NegativeInfinity;
                foreach (Transform child in tiles)
                {
                    if (!child.gameObject.activeInHierarchy || child is not RectTransform tile) continue;
                    tile.GetWorldCorners(corners);
                    top = Mathf.Max(top, parent.InverseTransformPoint(corners[1]).y);
                }
                if (top > float.NegativeInfinity && y - size.y / 2f < top + Gap) y = top + Gap + size.y / 2f;
            }
            var center = parent.rect.center;
            rect.anchoredPosition = new Vector2(x - center.x, y - center.y);
        }

        /// The button's click (its KButton plays the menu buttons' sounds and shows their hover
        /// and pressed colours): the release's page in the browser.
        void Open()
        {
            if (!string.IsNullOrEmpty(url)) Application.OpenURL(url);
        }
    }

    /// Waits on the main menu for the update check's answer, then shows the badge (and stops).
    sealed class UpdateBadgeWatcher : MonoBehaviour
    {
        internal UpdateBadge badge;

        void Update()
        {
            if (badge == null || badge.Show()) Destroy(this);
        }
    }
}
