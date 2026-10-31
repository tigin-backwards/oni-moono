using System.Collections.Generic;
using OniMoono.Engine.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Xunit;

namespace OniMoono.Tests.UGui
{
    /// The canvas walk skips a hidden screen whose group still blocks raycasts while no enabled
    /// raycaster can test its graphics, as the game hides its research screen (ResearchScreen.Show:
    /// the group's alpha 0, its own and its tree's GraphicRaycasters disabled, the object left
    /// active). Opening it again, or anything that could let a raycast or a draw reach it, walks it
    /// again, with the same depths and hits as a walk that never skipped.
    public class HiddenScreenSkipTests : UGuiTest
    {
        sealed class Screen
        {
            public EventSystem System;
            public Canvas Root, Canvas, TreeCanvas;
            public GraphicRaycaster RootRaycaster, Raycaster, TreeRaycaster;
            public CanvasGroup Group;
            public RectTransform Panel;
            public CanvasGroup PanelGroup;
            public Image Button, Node, PanelImage;
            public readonly List<Image> Images = new();

            public void Show(bool show)
            {
                Group.alpha = show ? 1f : 0f;
                Raycaster.enabled = show;
                TreeRaycaster.enabled = show;
            }
        }

        static Image Img(Screen s, string name, Transform parent, Vector2 position)
        {
            var rt = NewRect(name, parent, 40, 40);
            rt.anchoredPosition = position;
            var image = rt.gameObject.AddComponent<Image>();
            s.Images.Add(image);
            return image;
        }

        /// A HUD canvas with a raycaster and a button; the research screen in it (a nested canvas
        /// with its own raycaster and group) holding a tree (a nested canvas with a raycaster) of
        /// 30 nodes; beside the screen a panel with a group of its own and no canvas.
        static Screen Build()
        {
            var s = new Screen { System = new GameObject("EventSystem").AddComponent<EventSystem>() };
            s.Root = NewCanvas("HUD");
            s.RootRaycaster = s.Root.gameObject.AddComponent<GraphicRaycaster>();
            Img(s, "HudButton", s.Root.transform, new Vector2(-400, 300));
            var screen = NewRect("ResearchScreen", s.Root.transform, 1024, 768);
            s.Canvas = screen.gameObject.AddComponent<Canvas>();
            s.Raycaster = screen.gameObject.AddComponent<GraphicRaycaster>();
            s.Group = screen.gameObject.AddComponent<CanvasGroup>();
            s.Button = Img(s, "Close", screen, new Vector2(400, 300));
            var tree = NewRect("Tree", screen, 800, 600);
            s.TreeCanvas = tree.gameObject.AddComponent<Canvas>();
            s.TreeRaycaster = tree.gameObject.AddComponent<GraphicRaycaster>();
            for (int i = 0; i < 30; i++)
            {
                var row = NewRect("Row" + i, tree, 40, 40);
                var node = Img(s, "Node" + i, row, new Vector2(-300 + 50 * i, 0));
                if (i == 7) s.Node = node;
            }
            s.Panel = NewRect("Panel", s.Root.transform, 200, 200);
            s.PanelGroup = s.Panel.gameObject.AddComponent<CanvasGroup>();
            s.PanelImage = Img(s, "PanelImage", s.Panel, new Vector2(0, -300));
            return s;
        }

        static string FirstHit(EventSystem system, Vector2 position)
        {
            var hits = new List<RaycastResult>();
            system.RaycastAll(new PointerEventData(system) { position = position }, hits);
            return hits.Count > 0 ? hits[0].gameObject.name : "none";
        }

        static string Depths(Screen s)
        {
            var depths = new List<string>();
            foreach (var i in s.Images) depths.Add($"{i.name}={i.canvasRenderer.absoluteDepth}");
            return string.Join(" ", depths);
        }

        /// Objects walked in a frame after the one that follows a change (that one walks the
        /// screen and marks it); the top of a skipped subtree is counted.
        static int Walked()
        {
            Frame();
            Frame();
            return CanvasSystem.Walked;
        }

        [Fact]
        public void AHiddenScreenWithItsRaycastersDisabledIsSkippedUntilShownAgain()
        {
            var s = Build();
            int all = Walked();
            string depths = Depths(s);
            Assert.Equal("Node7", FirstHit(s.System, new Vector2(512 - 300 + 350, 384)));
            Assert.Equal("Close", FirstHit(s.System, new Vector2(912, 684)));

            // Closed: below the screen (the close button, the tree, 30 rows and 30 nodes) nothing
            // is walked.
            s.Show(false);
            int closed = Walked();
            Assert.Equal(all - 62, closed);
            Assert.Equal("none", FirstHit(s.System, new Vector2(912, 684)));
            Assert.Equal("HudButton", FirstHit(s.System, new Vector2(112, 684)));

            // Opened again: walked, with the same depths and hits.
            s.Show(true);
            Assert.Equal(all, Walked());
            Assert.Equal(depths, Depths(s));
            Assert.Equal("Node7", FirstHit(s.System, new Vector2(512 - 300 + 350, 384)));
            Assert.Equal("Close", FirstHit(s.System, new Vector2(912, 684)));

            // Closed and reopened again.
            s.Show(false);
            Assert.Equal(closed, Walked());
            s.Show(true);
            Assert.Equal(all, Walked());
            Assert.Equal("Close", FirstHit(s.System, new Vector2(912, 684)));
            s.Show(false);
            Assert.Equal(closed, Walked());

            // Any one raycaster enabled again, or the group shown, walks it.
            s.TreeRaycaster.enabled = true;
            Assert.Equal(all, Walked());
            Assert.Equal("Node7", FirstHit(s.System, new Vector2(512 - 300 + 350, 384)));
            s.TreeRaycaster.enabled = false;
            Assert.Equal(closed, Walked());
            s.Raycaster.enabled = true;
            Assert.Equal(all, Walked());
            Assert.Equal("Close", FirstHit(s.System, new Vector2(912, 684)));
            s.Raycaster.enabled = false;
            Assert.Equal(closed, Walked());
            s.Group.alpha = 0.5f;
            Assert.Equal(all, Walked());
            s.Group.alpha = 0f;
            Assert.Equal(closed, Walked());

            // A raycaster added in it walks it; the HUD's raycaster is not the screen's (it has
            // a canvas of its own).
            var added = s.Button.gameObject.AddComponent<Canvas>();
            var addedRaycaster = s.Button.gameObject.AddComponent<GraphicRaycaster>();
            Assert.Equal(all, Walked());
            Object.DestroyImmediate(addedRaycaster);
            Object.DestroyImmediate(added);
            Assert.Equal(closed, Walked());
            s.RootRaycaster.enabled = false;
            s.RootRaycaster.enabled = true;
            Assert.Equal(closed, Walked());

            Assert.Equal(depths, Depths(s));

            // The HUD renumbered while the screen is closed (an element added before it): the
            // screen is numbered then, and opened, its depths and hits are a renumbered walk's.
            var late = NewRect("Late", s.Root.transform, 10, 10);
            late.gameObject.AddComponent<Image>();
            late.SetAsFirstSibling();
            Frame();
            Frame();
            s.Show(true);
            Frame();
            Assert.Equal(0, late.GetComponent<CanvasRenderer>().absoluteDepth);
            for (int i = 0; i < s.Images.Count; i++) Assert.Equal(i + 1, s.Images[i].canvasRenderer.absoluteDepth);
            Assert.Equal("Node7", FirstHit(s.System, new Vector2(512 - 300 + 350, 384)));
        }

        /// A hidden panel without a canvas of its own belongs to the HUD's canvas: skipped only
        /// while the HUD's raycaster is disabled, though that raycaster is above it.
        [Fact]
        public void AHiddenPanelIsSkippedOnlyWhileItsCanvasesRaycasterIsDisabled()
        {
            var s = Build();
            int all = Walked();
            s.PanelGroup.alpha = 0f;
            Assert.Equal(all, Walked());
            Assert.Equal("PanelImage", FirstHit(s.System, new Vector2(512, 84)));
            s.RootRaycaster.enabled = false;
            Assert.Equal(all - 1, Walked());
            s.RootRaycaster.enabled = true;
            Assert.Equal(all, Walked());
            Assert.Equal("PanelImage", FirstHit(s.System, new Vector2(512, 84)));
            // A group that stops raycasts skips it whatever the raycasters (as before).
            s.PanelGroup.blocksRaycasts = false;
            Assert.Equal(all - 1, Walked());
        }
    }
}
