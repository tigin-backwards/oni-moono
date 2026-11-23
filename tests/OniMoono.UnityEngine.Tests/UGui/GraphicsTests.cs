using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Xunit;

namespace OniMoono.Tests.UGui
{
    public class CanvasUpdateTests : UGuiTest
    {
        sealed class Recorder : ICanvasElement
        {
            public readonly List<string> Log;
            public readonly string Name;
            public ICanvasElement AlsoGraphic;
            readonly Transform t;

            public Recorder(string name, Transform transform, List<string> log)
            {
                Name = name;
                t = transform;
                Log = log;
            }

            public Transform transform => t;
            public void Rebuild(CanvasUpdate executing)
            {
                Log.Add(Name + ":" + executing);
                if (executing == CanvasUpdate.Layout && AlsoGraphic != null)
                {
                    // A graphic rebuild requested during the layout runs in the same pass; a
                    // layout rebuild requested now is refused.
                    CanvasUpdateRegistry.RegisterCanvasElementForGraphicRebuild(AlsoGraphic);
                    Log.Add("layout again: " + CanvasUpdateRegistry.TryRegisterCanvasElementForLayoutRebuild(this));
                    Log.Add("rebuilding layout: " + CanvasUpdateRegistry.IsRebuildingLayout());
                }
            }
            public void LayoutComplete() => Log.Add(Name + ":LayoutComplete");
            public void GraphicUpdateComplete() => Log.Add(Name + ":GraphicUpdateComplete");
            public bool IsDestroyed() => false;
        }

        [Fact]
        public void LayoutRunsParentsFirstPhaseByPhaseThenGraphics()
        {
            var canvas = NewCanvas();
            var parent = NewRect("Parent", canvas.transform, 10, 10);
            var child = NewRect("Child", parent, 10, 10);
            var log = new List<string>();
            var deep = new Recorder("child", child, log);
            var shallow = new Recorder("parent", parent, log) { AlsoGraphic = new Recorder("graphic", child, log) };
            // Queued deepest first: the pass sorts by depth.
            CanvasUpdateRegistry.RegisterCanvasElementForLayoutRebuild(deep);
            CanvasUpdateRegistry.RegisterCanvasElementForLayoutRebuild(shallow);
            CanvasUpdateRegistry.RegisterCanvasElementForLayoutRebuild(deep);
            Canvas.ForceUpdateCanvases();
            Assert.Equal(new[]
            {
                "parent:Prelayout", "child:Prelayout",
                "parent:Layout", "layout again: False", "rebuilding layout: True", "child:Layout",
                "parent:PostLayout", "child:PostLayout",
                "parent:LayoutComplete", "child:LayoutComplete",
                "graphic:PreRender", "graphic:LatePreRender", "graphic:GraphicUpdateComplete",
            }, log);
            Assert.False(CanvasUpdateRegistry.IsRebuildingLayout());

            // The queues are empty after the pass.
            log.Clear();
            Canvas.ForceUpdateCanvases();
            Assert.Empty(log);
        }

        [Fact]
        public void UnregisteringCompletesAtOnce()
        {
            var canvas = NewCanvas();
            var log = new List<string>();
            var r = new Recorder("r", canvas.transform, log);
            CanvasUpdateRegistry.RegisterCanvasElementForGraphicRebuild(r);
            CanvasUpdateRegistry.UnRegisterCanvasElementForRebuild(r);
            // Both completions at once, even for an element queued for graphics only.
            Assert.Equal(new[] { "r:LayoutComplete", "r:GraphicUpdateComplete" }, log);
            log.Clear();
            Canvas.ForceUpdateCanvases();
            Assert.Empty(log);
        }

        [Fact]
        public void DisablingRemovesAndCompletesAtOnce()
        {
            var canvas = NewCanvas();
            var log = new List<string>();
            var d = new Recorder("d", canvas.transform, log);
            var other = new Recorder("other", canvas.transform, log);
            CanvasUpdateRegistry.RegisterCanvasElementForLayoutRebuild(d);
            CanvasUpdateRegistry.RegisterCanvasElementForGraphicRebuild(d);
            CanvasUpdateRegistry.RegisterCanvasElementForLayoutRebuild(other);
            CanvasUpdateRegistry.DisableCanvasElementForRebuild(d);
            Assert.Equal(new[] { "d:LayoutComplete", "d:GraphicUpdateComplete" }, log);
            log.Clear();
            Canvas.ForceUpdateCanvases();
            Assert.Equal(new[] { "other:Prelayout", "other:Layout", "other:PostLayout", "other:LayoutComplete" }, log);
        }

        sealed class Remover : ICanvasElement
        {
            public ICanvasElement Target;
            public CanvasUpdate When;
            public Transform transform { get; set; }
            public void Rebuild(CanvasUpdate executing)
            {
                if (executing == When) CanvasUpdateRegistry.DisableCanvasElementForRebuild(Target);
            }
            public void LayoutComplete() { }
            public void GraphicUpdateComplete() { }
            public bool IsDestroyed() => false;
        }

        [Fact]
        public void TheQueueBeingRebuiltCannotLoseElements()
        {
            var canvas = NewCanvas();
            var log = new List<string>();
            var target = new Recorder("t", canvas.transform, log);
            var remover = new Remover { Target = target, When = CanvasUpdate.Layout, transform = canvas.transform };
            CanvasUpdateRegistry.RegisterCanvasElementForLayoutRebuild(remover);
            CanvasUpdateRegistry.RegisterCanvasElementForLayoutRebuild(target);
            int errors = 0;
            Application.LogCallback count = (_, _, type) => { if (type == LogType.Error) errors++; };
            Application.logMessageReceived += count;
            try { Canvas.ForceUpdateCanvases(); }
            finally { Application.logMessageReceived -= count; }
            // The layout half is refused (logged) and the target still rebuilt; the graphic half
            // (it was not queued there) completes at once.
            Assert.Equal(1, errors);
            Assert.Equal(new[] { "t:Prelayout", "t:GraphicUpdateComplete", "t:Layout", "t:PostLayout", "t:LayoutComplete" }, log);
        }
    }

    public class ImageTests : UGuiTest
    {
        Image NewImage(float width, float height, Sprite sprite, Image.Type type)
        {
            var canvas = NewCanvas();
            var image = NewRect("Image", canvas.transform, width, height, typeof(Image)).GetComponent<Image>();
            image.sprite = sprite;
            image.type = type;
            Canvas.ForceUpdateCanvases();
            return image;
        }

        [Fact]
        public void WithoutASpriteAnImageIsOneWhiteQuadOverItsRect()
        {
            var image = NewImage(100, 50, null, Image.Type.Simple);
            Assert.Equal(new[] { new Vector2(-50, -25), new Vector2(-50, 25), new Vector2(50, 25), new Vector2(50, -25) }, Positions(image));
            Assert.Equal(new[] { 0, 1, 2, 2, 3, 0 }, GeometryOf(image).Indices);
            Assert.Equal(Texture2D.whiteTexture, image.mainTexture);
        }

        [Fact]
        public void SimpleKeepsTheSpriteAspectWhenAsked()
        {
            var image = NewImage(100, 50, NewSprite(20, 20), Image.Type.Simple);
            image.preserveAspect = true;
            Canvas.ForceUpdateCanvases();
            // Fitted at 1:1 inside 100x50, centred by the pivot.
            Assert.Equal(new[] { new Vector2(-25, -25), new Vector2(-25, 25), new Vector2(25, 25), new Vector2(25, -25) }, Positions(image));
        }

        [Fact]
        public void SlicedIsNineQuadsColumnByColumn()
        {
            var image = NewImage(100, 50, NewSprite(30, 30, new Vector4(10, 10, 10, 10)), Image.Type.Sliced);
            var p = Positions(image);
            Assert.Equal(36, p.Count);
            // First column, bottom quad.
            Assert.Equal(new[] { new Vector2(-50, -25), new Vector2(-50, -15), new Vector2(-40, -15), new Vector2(-40, -25) }, p.GetRange(0, 4));
            // First column, middle quad, then the top one.
            Assert.Equal(new Vector2(-50, 15), p[5]);
            Assert.Equal(new Vector2(-50, 25), p[9]);
            // Then the middle column.
            Assert.Equal(new Vector2(-40, -25), p[12]);
            Assert.Equal(new Vector2(40, -15), p[14]);

            image.fillCenter = false;
            Canvas.ForceUpdateCanvases();
            Assert.Equal(32, Positions(image).Count);
        }

        [Fact]
        public void SlicedBordersShrinkTogetherAndEmptyQuadsAreLeftOut()
        {
            // Borders of 10 + 30 in a rect 20 wide: scaled to 5 + 15, no centre column.
            var image = NewImage(20, 100, NewSprite(50, 50, new Vector4(10, 10, 30, 10)), Image.Type.Sliced);
            var p = Positions(image);
            Assert.Equal(24, p.Count);
            Assert.Equal(new Vector2(-5, -50), p[3]);
            Assert.Equal(new Vector2(-5, -50), p[12]);
            Assert.Equal(new Vector2(10, -40), p[14]);
        }

        [Fact]
        public void SlicedWithoutBordersIsSimple()
        {
            var image = NewImage(100, 50, NewSprite(30, 30), Image.Type.Sliced);
            Assert.Equal(4, Positions(image).Count);
        }

        [Fact]
        public void TiledRepeatsTheSpriteClippedAtTheEdges()
        {
            // 32 units per tile (100 pixels per unit on a canvas of 100 reference pixels per unit).
            var image = NewImage(100, 50, NewSprite(32, 32), Image.Type.Tiled);
            var p = Positions(image);
            Assert.Equal(4 * 2 * 4, p.Count);
            // The last tile of the first row ends at the rect's edge, its texture cut to match.
            Assert.Equal(new Vector2(50, -25 + 32), p[3 * 4 + 2]);
            var uv = GeometryOf(image).Uvs[0];
            Assert.Equal(4f / 32f, uv[3 * 4 + 2].x, 4);
        }

        [Fact]
        public void TiledWrappingSpriteIsOneQuadWithRepeatingCoordinates()
        {
            var image = NewImage(64, 32, NewSprite(32, 32, default, TextureWrapMode.Repeat), Image.Type.Tiled);
            Assert.Equal(4, Positions(image).Count);
            var uv = GeometryOf(image).Uvs[0];
            Assert.Equal(new Vector4(2, 1, 0, 0), uv[2]);
        }

        [Fact]
        public void FilledCutsTheQuadAndDrawsNothingAtZero()
        {
            var image = NewImage(100, 50, NewSprite(20, 20), Image.Type.Filled);
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillAmount = 0.25f;
            Canvas.ForceUpdateCanvases();
            Assert.Equal(new[] { new Vector2(-50, -25), new Vector2(-50, 25), new Vector2(-25, 25), new Vector2(-25, -25) }, Positions(image));

            image.fillOrigin = 1;
            Canvas.ForceUpdateCanvases();
            Assert.Equal(new Vector2(25, -25), Positions(image)[0]);

            image.fillAmount = 0f;
            Canvas.ForceUpdateCanvases();
            Assert.Empty(Positions(image));
        }

        [Fact]
        public void RadialFillCoversTheSweptPart()
        {
            var image = NewImage(100, 100, NewSprite(20, 20), Image.Type.Filled);
            image.fillMethod = Image.FillMethod.Radial360;
            image.fillAmount = 0.5f;
            Canvas.ForceUpdateCanvases();
            // Half a turn from the bottom: two of the four quarters, half the area.
            var p = Positions(image);
            var indices = GeometryOf(image).Indices;
            float area = 0f;
            for (int i = 0; i < indices.Length; i += 3)
            {
                var a = p[indices[i]];
                var b = p[indices[i + 1]];
                var c = p[indices[i + 2]];
                area += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) / 2f;
            }
            Assert.Equal(5000f, area, 1);
        }

        [Fact]
        public void RadialSweepCollapsesTheCornersPastTheCut()
        {
            // An eighth of a turn from the top, clockwise: the top right quarter up to its corner,
            // as the shipped package draws the game's asteroid clock.
            var image = NewImage(80, 80, NewSprite(20, 20), Image.Type.Filled);
            image.fillMethod = Image.FillMethod.Radial360;
            image.fillOrigin = (int)Image.Origin360.Top;
            image.fillAmount = 0.125f;
            Canvas.ForceUpdateCanvases();
            var p = Positions(image);
            Assert.Equal(4, p.Count);
            Assert.Equal(new Vector2(0, 0), p[0]);
            Assert.Equal(new Vector2(0, 40), p[1]);
            Assert.Equal(40f, p[2].x, 3);
            Assert.Equal(40f, p[2].y, 3);
            Assert.Equal(p[2], p[3]);
        }

        [Fact]
        public void SetNativeSizeUsesTheSpritePixels()
        {
            var image = NewImage(10, 10, NewSprite(40, 20), Image.Type.Simple);
            image.SetNativeSize();
            Assert.Equal(new Vector2(40, 20), image.rectTransform.rect.size);
            Assert.Equal(40f, image.preferredWidth);
        }

        [Fact]
        public void ColorChangesRebuildTheVertices()
        {
            var image = NewImage(10, 10, null, Image.Type.Simple);
            image.color = Color.red;
            Canvas.ForceUpdateCanvases();
            Assert.Equal(Color.red, GeometryOf(image).Colors[0]);
        }

        [Fact]
        public void CrossFadeColorTweensTheRendererNotTheColor()
        {
            var image = NewImage(10, 10, null, Image.Type.Simple);
            image.CrossFadeColor(Color.black, 0f, true, true);
            Assert.Equal(Color.black, image.canvasRenderer.GetColor());
            Assert.Equal(Color.white, image.color);
            image.CrossFadeAlpha(0.5f, 1f, true);
            for (int i = 0; i < 70; i++) Frame(1f / 60f);
            Assert.Equal(0.5f, image.canvasRenderer.GetColor().a, 3);
            Assert.Equal(0f, image.canvasRenderer.GetColor().r);
        }
    }

    public class MaskingTests : UGuiTest
    {
        [Fact]
        public void RectMask2DClipsAndCullsItsGraphics()
        {
            var canvas = NewCanvas();
            var mask = NewRect("Mask", canvas.transform, 100, 100, typeof(RectMask2D));
            var inside = NewRect("Inside", mask, 20, 20, typeof(Image)).GetComponent<Image>();
            var outside = NewRect("Outside", mask, 20, 20, typeof(Image)).GetComponent<Image>();
            outside.rectTransform.anchoredPosition = new Vector2(300, 0);
            Canvas.ForceUpdateCanvases();
            Assert.True(inside.canvasRenderer.hasRectClipping);
            Assert.False(inside.canvasRenderer.cull);
            Assert.True(outside.canvasRenderer.cull);

            mask.GetComponent<RectMask2D>().enabled = false;
            Canvas.ForceUpdateCanvases();
            Assert.False(inside.canvasRenderer.hasRectClipping);
            Assert.False(outside.canvasRenderer.cull);
        }

        [Fact]
        public void StencilMaskWrapsTheMaterialsOfTheGraphicsBelow()
        {
            UseGameAssets();
            var canvas = NewCanvas();
            var maskRect = NewRect("Mask", canvas.transform, 100, 100, typeof(Image), typeof(Mask));
            var child = NewRect("Child", maskRect, 20, 20, typeof(Image)).GetComponent<Image>();
            var nested = NewRect("Nested", child.transform, 10, 10, typeof(Image), typeof(Mask));
            var deep = NewRect("Deep", nested, 5, 5, typeof(Image)).GetComponent<Image>();
            Canvas.ForceUpdateCanvases();
            Assert.Equal(1, child.canvasRenderer.materialCount);
            var below = child.canvasRenderer.GetMaterial();
            Assert.Equal("Stencil Id:1, Op:Keep, Comp:Equal, WriteMask:0, ReadMask:1, ColorMask:All AlphaClip:False (" + Graphic.defaultGraphicMaterial.name + ")", below.name);
            var own = maskRect.GetComponent<Image>().canvasRenderer;
            Assert.StartsWith("Stencil Id:1, Op:Replace, Comp:Always, WriteMask:255, ReadMask:255, ColorMask:All AlphaClip:True", own.GetMaterial().name);
            Assert.Equal(1, own.popMaterialCount);
            Assert.StartsWith("Stencil Id:1, Op:Zero, Comp:Always", own.GetPopMaterial(0).name);
            // Two masks above: the low two bits.
            Assert.StartsWith("Stencil Id:3, Op:Keep, Comp:Equal, WriteMask:0, ReadMask:3", deep.canvasRenderer.GetMaterial().name);

            maskRect.GetComponent<Mask>().showMaskGraphic = false;
            Canvas.ForceUpdateCanvases();
            Assert.Contains("ColorMask:0", own.GetMaterial().name);
        }

        /// The Colony Summary's flourish (AchievementWidget.Flourish): a widget below the list's
        /// mask turns on its canvas's overrideSorting to draw over the veils, and off again. Its
        /// graphics count the masks only up to that canvas while it overrides, so they draw
        /// unmasked then; with the list's stencil test kept, they vanished for the flourish.
        [Fact]
        public void OverridingSortingRecountsTheMasksAboveTheGraphics()
        {
            UseGameAssets();
            var canvas = NewCanvas();
            var list = NewRect("List", canvas.transform, 100, 100, typeof(Image), typeof(Mask));
            var widget = NewRect("Widget", list, 50, 50, typeof(Canvas));
            var image = NewRect("BG", widget, 50, 50, typeof(Image)).GetComponent<Image>();
            Canvas.ForceUpdateCanvases();
            Assert.StartsWith("Stencil Id:1, Op:Keep, Comp:Equal", image.canvasRenderer.GetMaterial().name);

            var sorting = widget.GetComponent<Canvas>();
            sorting.overrideSorting = true;
            sorting.sortingOrder = 30;
            Canvas.ForceUpdateCanvases();
            Assert.Equal(Graphic.defaultGraphicMaterial, image.canvasRenderer.GetMaterial());

            sorting.overrideSorting = false;
            Canvas.ForceUpdateCanvases();
            Assert.StartsWith("Stencil Id:1, Op:Keep, Comp:Equal", image.canvasRenderer.GetMaterial().name);
        }
    }
}
