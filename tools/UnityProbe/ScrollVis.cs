// Scrollbar visibility of a ScrollRect (lines "PROBE ... SVIS ..."), at the main menu, 2 s in.
// Found with Better Build Menu (CN) (Workshop 3825795220): PlanScreen hides the building list's
// vertical scrollbar itself (SetActive(false) while it judges the list short enough), and the
// original kept it hidden while the content overflowed the viewport (122.4 against 107.4); ours
// showed it again. That scroll rect (a KScrollRect, vertical, Elastic, both visibilities
// Permanent) sits on its own content object; its viewport is the content's parent, and the
// scrollbar is the viewport's child, not the scroll rect's.
//
// Each case builds, on one overlay canvas, a 200 x 100 scroll rect (vertical, Clamped) with a
// Viewport (stretched, a RectMask2D), a Content under it (along the viewport's top, pivot
// (0.5, 1)) and a vertical Scrollbar (BottomToTop, 10 wide along the right side) with a Handle.
// Layouts:
// - child: the scroll rect on the root, the viewport and the scrollbar its children;
// - outside: as child, but the scrollbar a child of the canvas, beside the root;
// - game: as the game's, the scroll rect on the content (Elastic), the scrollbar under the
//   viewport.
// Per child case: the vertical scrollbar visibility (Permanent, AutoHide,
// AutoHideAndExpandViewport), the content height (50: fits; 150: overflows), the vertical axis
// on or off, and the scrollbar active or inactive when the scroll rect activates. Every case is
// made in one frame (frame 0). Child cases, from an Update: frame 3 sets each scrollbar to the
// other activity (as the game's code would); frame 6 gives each content the other height;
// frame 9 sets each scrollbar inactive and frame 12 active. Outside and game cases (Permanent
// and AutoHide, heights 50 and 150, axis on, the scrollbar active at first): frame 3 sets the
// scrollbar inactive; frame 5 moves the content 5 up; frame 7 sets it inactive, frame 8 marks
// the scroll rect's layout for rebuild; frame 10 inactive, frame 11 the other content height;
// frame 13 inactive, frame 14 sets verticalNormalizedPosition to 0.5. At the end of every
// frame, 0 to 16, each case logs its scrollbar's activeSelf, the viewport's sizeDelta.x, the
// content's height and the scrollbar's size. With PROBE_SCENARIO=scrollvis only this probe
// runs; it ends with a ScrollVisProbeDone canvas.
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace SemanticsProbe
{
    public class ScrollVis : MonoBehaviour
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static void L(string s) => Rec.Log("SVIS " + s);

        class Case
        {
            public string Name, Layout;
            public ScrollRect Rect;
            public Scrollbar Bar;
            public RectTransform Viewport, Content;
            public float Height;
        }

        readonly List<Case> cases = new List<Case>();
        int frame = -1;

        static RectTransform Child(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static Scrollbar MakeBar(Transform parent, bool active)
        {
            var barRect = Child("Scrollbar", parent);
            barRect.anchorMin = new Vector2(1f, 0f);
            barRect.anchorMax = Vector2.one;
            barRect.pivot = Vector2.one;
            barRect.sizeDelta = new Vector2(10f, 0f);
            barRect.gameObject.AddComponent<Image>();
            var area = Child("Sliding Area", barRect);
            area.anchorMin = Vector2.zero;
            area.anchorMax = Vector2.one;
            area.sizeDelta = Vector2.zero;
            var handle = Child("Handle", area);
            handle.anchorMin = Vector2.zero;
            handle.anchorMax = Vector2.one;
            handle.sizeDelta = Vector2.zero;
            var handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = Color.gray;
            var bar = barRect.gameObject.AddComponent<Scrollbar>();
            bar.handleRect = handle;
            bar.targetGraphic = handleImage;
            bar.direction = Scrollbar.Direction.BottomToTop;
            barRect.gameObject.SetActive(active);
            return bar;
        }

        static Case Build(Transform canvas, int index, string layout, ScrollRect.ScrollbarVisibility vis, float height, bool axis, bool barActive)
        {
            var c = new Case
            {
                Name = (layout == "child" ? "" : layout + "-") + vis + "-h" + height.ToString(Inv) + (axis ? "-axis" : "-noaxis") + (barActive ? "-on" : "-off"),
                Layout = layout,
                Height = height,
            };
            var root = Child("SV-" + c.Name, canvas);
            root.gameObject.SetActive(false);
            root.anchorMin = root.anchorMax = root.pivot = Vector2.zero;
            root.sizeDelta = new Vector2(200f, 100f);
            root.anchoredPosition = new Vector2(10f + (index % 6) * 210f, 10f + (index / 6) * 110f);
            var viewport = Child("Viewport", root);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.sizeDelta = Vector2.zero;
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = Child("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = new Vector2(0f, height);
            content.gameObject.AddComponent<Image>().color = new Color(0.3f, 0.5f, 0.8f, 0.5f);
            Scrollbar bar;
            if (layout == "outside")
            {
                // Beside the root on the canvas, along the root's right edge.
                var holder = Child("SVBar-" + c.Name, canvas);
                holder.anchorMin = holder.anchorMax = holder.pivot = Vector2.zero;
                holder.sizeDelta = new Vector2(200f, 100f);
                holder.anchoredPosition = root.anchoredPosition;
                bar = MakeBar(holder, barActive);
            }
            else bar = MakeBar(layout == "game" ? viewport : root, barActive);
            var sr = (layout == "game" ? content.gameObject : root.gameObject).AddComponent<ScrollRect>();
            sr.content = content;
            sr.viewport = viewport;
            sr.horizontal = false;
            sr.vertical = axis;
            sr.movementType = layout == "game" ? ScrollRect.MovementType.Elastic : ScrollRect.MovementType.Clamped;
            sr.verticalScrollbar = bar;
            sr.verticalScrollbarVisibility = vis;
            sr.verticalScrollbarSpacing = 0f;
            c.Rect = sr;
            c.Bar = bar;
            c.Viewport = viewport;
            c.Content = content;
            root.gameObject.SetActive(true);
            return c;
        }

        void Update()
        {
            if (frame < 0) return;
            frame++;
            foreach (var c in cases)
            {
                if (c.Layout == "child")
                {
                    if (frame == 3) c.Bar.gameObject.SetActive(!c.Bar.gameObject.activeSelf);
                    if (frame == 6) c.Content.sizeDelta = new Vector2(0f, c.Height == 50f ? 150f : 50f);
                    if (frame == 9) c.Bar.gameObject.SetActive(false);
                    if (frame == 12) c.Bar.gameObject.SetActive(true);
                    continue;
                }
                if (frame == 3 || frame == 7 || frame == 10 || frame == 13) c.Bar.gameObject.SetActive(false);
                if (frame == 5) c.Content.anchoredPosition += new Vector2(0f, 5f);
                if (frame == 8) LayoutRebuilder.MarkLayoutForRebuild((RectTransform)c.Rect.transform);
                if (frame == 11) c.Content.sizeDelta = new Vector2(0f, c.Height == 50f ? 150f : 50f);
                if (frame == 14) c.Rect.verticalNormalizedPosition = 0.5f;
            }
            if (frame >= 3 && frame <= 14) L("f" + frame + " acted in Update");
        }

        IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(2f);
            L("begin");
            var canvasGo = new GameObject("ScrollVisCanvas", typeof(RectTransform));
            DontDestroyOnLoad(canvasGo);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            int i = 0;
            foreach (var vis in new[] { ScrollRect.ScrollbarVisibility.Permanent, ScrollRect.ScrollbarVisibility.AutoHide, ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport })
                foreach (var height in new[] { 50f, 150f })
                    foreach (var axis in new[] { true, false })
                        foreach (var on in new[] { true, false })
                            cases.Add(Build(canvasGo.transform, i++, "child", vis, height, axis, on));
            foreach (var layout in new[] { "outside", "game" })
                foreach (var vis in new[] { ScrollRect.ScrollbarVisibility.Permanent, ScrollRect.ScrollbarVisibility.AutoHide })
                    foreach (var height in new[] { 50f, 150f })
                        cases.Add(Build(canvasGo.transform, i++, layout, vis, height, true, true));
            L("made " + cases.Count + " cases");
            frame = 0;
            for (int k = 0; k <= 16; k++)
            {
                yield return new WaitForEndOfFrame();
                foreach (var c in cases)
                    L("f" + k + " " + c.Name + " bar=" + (c.Bar.gameObject.activeSelf ? 1 : 0) + " vsd=" + c.Viewport.sizeDelta.x.ToString("R", Inv)
                      + " h=" + c.Content.rect.height.ToString("R", Inv) + " size=" + c.Bar.size.ToString("R", Inv));
                if (k < 16) yield return null;
            }
            frame = -1;
            var done = new GameObject("ScrollVisProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("done");
        }
    }
}
