using System;
using UnityEngine;
using UnityEngine.UI;

namespace OniMoono.GameTweaks
{
    /// The main menu's overlap badge: "N overlaps" with the game's warning icon, right of the
    /// Moono Tweaks button (the update badge moves right of it), shown only while a mod has a
    /// high-grade overlap with a tweak this run has on (ModOverlaps). Made as the update badge is
    /// (UpdateBadge: a copy of the menu's button prefab in the normal button style), so it hovers,
    /// presses and sounds as the menu's buttons do. Clicking it opens the Moono Tweaks screen;
    /// hovering it says what the count is.
    public sealed class OverlapBadge : MonoBehaviour
    {
        const float IconSize = 22f;

        RectTransform anchor, rect;
        Transform motd;

        /// From MoonoTweaksMenu.AddButton, after ModOverlaps.Refresh: the badge for this menu, or
        /// null when there is no high-grade overlap.
        internal static RectTransform Attach(MainMenu menu, KButton tweaksButton, KButton prefab, ColorStyleSetting style, System.Action open)
        {
            int count = ModOverlaps.HighCount;
            if (count == 0) return null;
            var button = Util.KInstantiateUI<KButton>(prefab.gameObject, menu.gameObject, force_active: true);
            var go = button.gameObject;
            go.name = "MoonoOverlapBadge";
            var image = go.GetComponent<KImage>();
            image.colorStyleSetting = style;
            image.ApplyColorStyleSetting();
            button.onClick += open;
            var badge = go.AddComponent<OverlapBadge>();
            badge.anchor = tweaksButton.GetComponent<RectTransform>();
            badge.motd = UpdateBadge.Find(menu.transform, "MOTD");
            badge.Build(button, count);
            return badge.rect;
        }

        void Build(KButton button, int count)
        {
            rect = (RectTransform)transform;
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            var source = button.GetComponentInChildren<LocText>(true);
            var buttonHeight = rect.rect.height;
            foreach (var old in GetComponents<LayoutGroup>()) DestroyImmediate(old);
            var row = gameObject.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset(10, 14, 6, 6);
            row.spacing = 6f;
            row.childAlignment = TextAnchor.MiddleLeft;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            var fit = gameObject.GetComponent<ContentSizeFitter>() ?? gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var height = gameObject.GetComponent<LayoutElement>() ?? gameObject.AddComponent<LayoutElement>();
            height.ignoreLayout = false;
            height.minWidth = height.preferredWidth = height.flexibleWidth = -1f;
            height.minHeight = -1f;
            height.preferredHeight = anchor != null ? anchor.rect.height : buttonHeight;

            var icon = MoonoTweaksScreen.OverlapIcon(transform, OverlapGrade.High, IconSize);
            icon.transform.SetAsFirstSibling();

            var text = count == 1 ? MoonoTweaksMenu.Ui("OVERLAPS_ONE") : string.Format(MoonoTweaksMenu.Ui("OVERLAPS_MANY"), count);
            UpdateBadge.Line(source, transform, "Count", text, 14, Util.ColorFromHex(MoonoTweaksScreen.Warning), heading: true);
            source.gameObject.SetActive(false);
            source.transform.SetParent(null, false);
            Destroy(source.gameObject);

            var tip = gameObject.AddComponent<ToolTip>();
            tip.SizingSetting = ToolTip.ToolTipSizeSetting.MaxWidthWrapContent;
            tip.WrapWidth = 360f;
            tip.toolTipPosition = ToolTip.TooltipPosition.Custom;
            tip.tooltipPivot = new Vector2(0f, 0f);
            tip.parentPositionAnchor = new Vector2(0f, 1f);
            tip.tooltipPositionOffset = new Vector2(0f, 8f);
            tip.toolTip = MoonoTweaksMenu.Ui("OVERLAPS_TIP");
            Place();
        }

        void LateUpdate() => Place();

        void Place() => UpdateBadge.PlaceRight(rect, anchor, motd);
    }
}
