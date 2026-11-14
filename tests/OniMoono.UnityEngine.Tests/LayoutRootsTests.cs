using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using Xunit;

namespace OniMoono.Tests
{
    /// A layout root queued with a root above it in the same layout pass (a scroll view's content
    /// under a screen's layout group): the upper root's rebuild leaves the lower one's subtree to
    /// the lower root's own rebuild (LayoutRebuilder, "Nested layout roots in one layout pass"),
    /// and every rect ends where a pass that calculates it all places it.
    public class LayoutRootsTests : EngineTest
    {
        /// A layout element with a layout member of its own: a layout holding one is rebuilt whole.
        sealed class OwnLayoutElement : LayoutElement
        {
            public static int Asked;

            public override float minWidth
            {
                get
                {
                    Asked++;
                    return base.minWidth;
                }
            }
        }

        static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static VerticalLayoutGroup Group(RectTransform rect, int spacing)
        {
            var group = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            group.spacing = spacing;
            group.padding = new RectOffset(3, 5, 7, 2);
            group.childControlWidth = group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            return group;
        }

        static LayoutElement Element<T>(RectTransform rect, float width, float height) where T : LayoutElement
        {
            var element = rect.gameObject.AddComponent<T>();
            element.minWidth = element.preferredWidth = width;
            element.minHeight = element.preferredHeight = height;
            return element;
        }

        /// A screen (a layout group) with a header and a view; the view (an Image, no group and
        /// no controller) holds the content, a layout group of its own with three rows of two
        /// cells each. `own`: the header's layout element has a member of its own.
        static (RectTransform Screen, RectTransform Content, List<RectTransform> All) Build(bool own)
        {
            var all = new List<RectTransform>();
            RectTransform Add(string name, Transform parent)
            {
                var r = Rect(name, parent);
                all.Add(r);
                return r;
            }
            var screen = Add("Screen", null);
            screen.sizeDelta = new Vector2(300, 240);
            Group(screen, 4);
            var header = Add("Header", screen);
            if (own) Element<OwnLayoutElement>(header, 120, 30);
            else Element<LayoutElement>(header, 120, 30);
            var view = Add("View", screen);
            view.gameObject.AddComponent<Image>();
            Element<LayoutElement>(view, 200, 150);
            var content = Add("Content", view);
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0, 1);
            Group(content, 2);
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            for (int i = 0; i < 3; i++)
            {
                var row = Add("Row" + i, content);
                var cells = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                cells.spacing = 6;
                cells.childControlWidth = cells.childControlHeight = true;
                cells.childForceExpandWidth = cells.childForceExpandHeight = false;
                for (int k = 0; k < 2; k++) Element<LayoutElement>(Add($"Cell{i}{k}", row), 40 + 10 * i + 5 * k, 12 + 3 * i);
            }
            return (screen, content, all);
        }

        static long LeftOut() => (long)typeof(LayoutRebuilder).GetField("leftOut", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        static void Change(List<RectTransform> all)
        {
            // A cell and the header change size: the content's root and the screen's are both queued.
            foreach (var r in all)
            {
                if (r.name == "Cell11") r.GetComponent<LayoutElement>().preferredHeight = 44;
                if (r.name == "Cell20") r.GetComponent<LayoutElement>().preferredWidth = 90;
                if (r.name == "Header") r.GetComponent<LayoutElement>().preferredHeight = 36;
            }
        }

        [Fact]
        public void ANestedRootLeftToItsOwnRebuildEndsWhereAWholePassPutsIt()
        {
            var plain = Build(own: false);
            var whole = Build(own: true);
            Canvas.ForceUpdateCanvases();
            Change(plain.All);
            Change(whole.All);

            long before = LeftOut();
            OwnLayoutElement.Asked = 0;
            Canvas.ForceUpdateCanvases();
            // The plain screen's rebuild left its content to the content's own; the one with a
            // layout member of its own was calculated whole, twice over the content.
            Assert.Equal(before + 2, LeftOut());
            Assert.True(OwnLayoutElement.Asked > 0);

            for (int i = 0; i < plain.All.Count; i++)
            {
                var a = plain.All[i];
                var b = whole.All[i];
                Assert.True(a.rect == b.rect && a.anchoredPosition == b.anchoredPosition && a.sizeDelta == b.sizeDelta,
                    $"{a.name}: left out {a.rect} at {a.anchoredPosition}, whole {b.rect} at {b.anchoredPosition}");
            }
            // And the values are the layout's: the changed cell made its row 44 high.
            Assert.Equal(44f, plain.All.Find(r => r.name == "Row1").rect.height);
            Assert.Equal(36f, plain.All.Find(r => r.name == "Header").rect.height);
        }

        /// A rebuild asked for outside the pass (ForceRebuildLayoutImmediate) calculates all of it.
        [Fact]
        public void AnImmediateRebuildIsWhole()
        {
            var plain = Build(own: false);
            Canvas.ForceUpdateCanvases();
            Change(plain.All);
            long before = LeftOut();
            LayoutRebuilder.ForceRebuildLayoutImmediate(plain.Screen);
            Assert.Equal(before, LeftOut());
        }
    }
}
