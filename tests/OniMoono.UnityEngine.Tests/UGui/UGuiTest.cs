using System.Collections.Generic;
using OniMoono.Engine;
using OniMoono.Engine.UI;
using UnityEngine;
using UnityEngine.UI;

namespace OniMoono.Tests.UGui
{
    /// Tests of this project's uGUI (src/UnityEngine.UI, written from docs/cleanroom/ugui.md): an
    /// overlay canvas on the headless 1024x768 screen, updated by the engine's canvas system.
    public abstract class UGuiTest : EngineTest
    {
        protected UGuiTest() => CanvasSystem.Install();

        /// The game's data, for the built-in UI shaders (the stencil properties of UI/Default).
        protected static void UseGameAssets()
        {
            OniMoono.Engine.Assets.AssetSystem.Initialize(GameInstall.DataDir);
            OniMoono.Engine.Assets.ProjectSettings.Load();
        }

        protected static Canvas NewCanvas(string name = "Canvas")
        {
            var canvas = new GameObject(name, typeof(RectTransform)).AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            return canvas;
        }

        /// A child rect of the given size, centred in its parent.
        protected static RectTransform NewRect(string name, Transform parent, float width, float height, params System.Type[] components)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(width, height);
            foreach (var c in components) go.AddComponent(c);
            return rt;
        }

        protected static Sprite NewSprite(int width, int height, Vector4 border = default, TextureWrapMode wrap = TextureWrapMode.Clamp)
        {
            var texture = new Texture2D(width, height) { wrapMode = wrap };
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        }

        /// What the graphic last gave its canvas renderer.
        internal static CanvasGeometry GeometryOf(Graphic g) => ((CanvasRendererNative)g.canvasRenderer.Native).Geometry;

        protected static List<Vector2> Positions(Graphic g)
        {
            var list = new List<Vector2>();
            var geometry = GeometryOf(g);
            if (geometry == null) return list;
            foreach (var p in geometry.Positions) list.Add(new Vector2(p.x, p.y));
            return list;
        }
    }
}
