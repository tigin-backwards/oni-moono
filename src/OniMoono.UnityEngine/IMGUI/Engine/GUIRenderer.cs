using System;
using System.Collections.Generic;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Engine.IMGUI
{
    /// Draws IMGUI geometry on the back buffer during Repaint events: textured quads (style
    /// backgrounds sliced by their border, images, GUI.DrawTexture) with Unity's GUI shaders,
    /// and text from the legacy text generator. Coordinates are the current GUIClip's local
    /// coordinates; quads are clipped to the topmost clip's visible rect in absolute GUI space
    /// and then transformed by GUI.matrix into screen pixels (origin top left).
    internal static class GUIRenderer
    {
        static readonly int MainTex = Shader.PropertyToID("_MainTex");
        static readonly int ClipTex = Shader.PropertyToID("_GUIClipTexture");
        static readonly int ClipMatrix = Shader.PropertyToID("unity_GUIClipTextureMatrix");
        static Material textureClip, textClip, blit;
        static bool active;
        static readonly List<ImmediateVertex> vertices = new();
        static readonly TextGenerator generator = new() { GuiMetrics = true };

        /// Hidden/Internal-GUITextureClip: texture × vertex colour, alpha × the clip texture.
        /// Clipping is done on the geometry here, so the clip texture is plain white.
        static Material TextureMaterial
        {
            get
            {
                if (textureClip != null) return textureClip;
                textureClip = new Material(Shader.Find("Hidden/Internal-GUITextureClip")) { hideFlags = HideFlags.HideAndDontSave };
                textureClip.SetTexture(ClipTex, Texture2D.whiteTexture);
                textureClip.SetMatrix(ClipMatrix, Matrix4x4.identity);
                return textureClip;
            }
        }

        /// Hidden/Internal-GUITextureBlit: GUI.DrawTexture without alpha blending.
        public static Material BlitMaterial
        {
            get
            {
                if (!active) return null;
                if (blit != null) return blit;
                blit = new Material(Shader.Find("Hidden/Internal-GUITextureBlit")) { hideFlags = HideFlags.HideAndDontSave };
                blit.SetTexture(ClipTex, Texture2D.whiteTexture);
                blit.SetMatrix(ClipMatrix, Matrix4x4.identity);
                return blit;
            }
        }

        /// Hidden/Internal-GUITextureClipText: vertex colour with the font texture's alpha.
        static Material TextMaterial
        {
            get
            {
                if (textClip != null) return textClip;
                textClip = new Material(Shader.Find("Hidden/Internal-GUITextureClipText")) { hideFlags = HideFlags.HideAndDontSave };
                textClip.SetTexture(ClipTex, Texture2D.whiteTexture);
                textClip.SetMatrix(ClipMatrix, Matrix4x4.identity);
                textClip.SetColor("_Color", Color.white);
                return textClip;
            }
        }

        /// GUI drawing goes to the screen (the back buffer, or a capture's super-sized target or
        /// window of it) in screen pixel coordinates with the origin at the top left; GL
        /// immediate drawing (Graphics.DrawTexture) inside OnGUI uses the same space.
        public static void BeginFrame()
        {
            var device = GraphicsDevice.Current;
            var size = new Vector2(Screen.width, Screen.height);
            var pixels = FrameRenderer.ScreenPixelSize;
            var projection = FrameRenderer.ScreenProjection(Projection);
            FrameRenderer.Bind(device, FrameRenderer.ScreenBinding);
            device.SetViewport(new Rect(0f, 0f, pixels.x, pixels.y));
            device.SetCamera(new CameraConstants(Matrix4x4.identity, projection, Vector3.zero, -1f, 100f, true, size));
            GLState.Push();
            GLState.Projection = projection;
            GLState.ModelView = Matrix4x4.identity;
            active = true;
        }

        public static void EndFrame()
        {
            active = false;
            GLState.Pop();
        }

        static Matrix4x4 Projection => Matrix4x4.Ortho(0f, Screen.width, Screen.height, 0f, -1f, 100f);

        /// Drawing happens only while a frame is being drawn, during Repaint.
        static bool CanDraw => active && Event.current != null && Event.current.type == EventType.Repaint;

        // ----- Textures -----

        /// Draws `texture` over `rect` (local GUI coordinates) using the normalized source rect
        /// `uv`, sliced by `border` (left, right, top, bottom in texture pixels: corners keep
        /// their size, edges stretch along one axis, the centre along both).
        public static void DrawTexture(Rect rect, Texture texture, Rect uv, Color color, RectOffset border, Material material = null)
        {
            if (!CanDraw || texture == null || rect.width <= 0f || rect.height <= 0f) return;
            vertices.Clear();
            var abs = ClipStack.Unclip(rect);
            var clip = ClipStack.AbsoluteVisible;
            if (border == null || (border.left | border.right | border.top | border.bottom) == 0)
            {
                AddQuad(abs, uv, color, clip);
            }
            else
            {
                float tw = texture.width, th = texture.height;
                float l = border.left, r = border.right, t = border.top, b = border.bottom;
                // Borders larger than the rect shrink proportionally.
                if (l + r > abs.width && l + r > 0f) { float k = abs.width / (l + r); l *= k; r *= k; }
                if (t + b > abs.height && t + b > 0f) { float k = abs.height / (t + b); t *= k; b *= k; }
                float[] xs = { abs.xMin, abs.xMin + l, abs.xMax - r, abs.xMax };
                float[] ys = { abs.yMin, abs.yMin + t, abs.yMax - b, abs.yMax };
                float[] us = { uv.xMin, uv.xMin + border.left / tw, uv.xMax - border.right / tw, uv.xMax };
                // GUI y grows downwards, texture v upwards.
                float[] vs = { uv.yMax, uv.yMax - border.top / th, uv.yMin + border.bottom / th, uv.yMin };
                for (int iy = 0; iy < 3; iy++)
                    for (int ix = 0; ix < 3; ix++)
                    {
                        var q = Rect.MinMaxRect(xs[ix], ys[iy], xs[ix + 1], ys[iy + 1]);
                        if (q.width <= 0f || q.height <= 0f) continue;
                        AddQuad(q, Rect.MinMaxRect(us[ix], vs[iy + 1], us[ix + 1], vs[iy]), color, clip);
                    }
            }
            Flush(material ?? TextureMaterial, texture);
        }

        /// One quad: `abs` in absolute GUI coordinates, `uv` with yMax at the quad's top.
        static void AddQuad(Rect abs, Rect uv, Color color, Rect clip)
        {
            float x0 = Mathf.Max(abs.xMin, clip.xMin), x1 = Mathf.Min(abs.xMax, clip.xMax);
            float y0 = Mathf.Max(abs.yMin, clip.yMin), y1 = Mathf.Min(abs.yMax, clip.yMax);
            if (x1 <= x0 || y1 <= y0) return;
            float U(float x) => Mathf.Lerp(uv.xMin, uv.xMax, (x - abs.xMin) / abs.width);
            float V(float y) => Mathf.Lerp(uv.yMax, uv.yMin, (y - abs.yMin) / abs.height);
            Add(x0, y0, U(x0), V(y0), color);
            Add(x1, y0, U(x1), V(y0), color);
            Add(x1, y1, U(x1), V(y1), color);
            Add(x0, y1, U(x0), V(y1), color);
        }

        static void Add(float x, float y, float u, float v, Color color)
            => vertices.Add(new ImmediateVertex { Position = ClipStack.ToScreen(new Vector2(x, y)), Uv0 = new Vector4(u, v, 0f, 0f), Color = color });

        static void Flush(Material material, Texture texture)
        {
            if (vertices.Count == 0) return;
            material.SetTexture(MainTex, texture);
            if (material.SetPass(0))
                GraphicsDevice.Current.DrawImmediate(ImmediatePrimitive.Quads, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices), Matrix4x4.identity, Projection);
            vertices.Clear();
        }

        // ----- Text -----

        /// Draws text laid out in `rect` (local GUI coordinates) and clipped to `textClip`
        /// (local, null: only the GUIClip).
        public static void DrawText(Rect rect, string text, Font font, int fontSize, FontStyle fontStyle, TextAnchor alignment, bool wordWrap, bool richText, Color color, Rect? textClip)
        {
            if (!CanDraw || font == null || string.IsNullOrEmpty(text)) return;
            var settings = GUIText.Settings(font, fontSize, fontStyle, alignment, wordWrap, richText, rect.size, aligned: true);
            settings.color = Color.white;
            generator.Populate(text, settings);
            var texture = font.material != null ? font.material.mainTexture : null;
            if (texture == null) return;
            var abs = ClipStack.Unclip(rect);
            var clip = ClipStack.AbsoluteVisible;
            if (textClip is { } local)
            {
                var c = ClipStack.Unclip(local);
                clip = Rect.MinMaxRect(Mathf.Max(clip.xMin, c.xMin), Mathf.Max(clip.yMin, c.yMin), Mathf.Min(clip.xMax, c.xMax), Mathf.Min(clip.yMax, c.yMax));
            }
            vertices.Clear();
            var verts = generator.verts;
            for (int i = 0; i + 3 < verts.Count; i += 4)
            {
                // Generator quads: top-left, top-right, bottom-right, bottom-left; y up from the
                // rect's top edge.
                var tl = verts[i];
                var br = verts[i + 2];
                var q = Rect.MinMaxRect(abs.x + tl.position.x, abs.y - tl.position.y, abs.x + br.position.x, abs.y - br.position.y);
                var uv = Rect.MinMaxRect(Mathf.Min(tl.uv0.x, br.uv0.x), Mathf.Min(tl.uv0.y, br.uv0.y), Mathf.Max(tl.uv0.x, br.uv0.x), Mathf.Max(tl.uv0.y, br.uv0.y));
                Color vc = tl.color;
                var c = new Color(vc.r * color.r, vc.g * color.g, vc.b * color.b, vc.a * color.a);
                if (tl.uv0.x <= br.uv0.x && tl.uv0.y >= br.uv0.y) AddQuad(q, uv, c, clip);
                else AddRotatedGlyph(q, verts[i].uv0, verts[i + 1].uv0, verts[i + 2].uv0, verts[i + 3].uv0, c, clip);
            }
            Flush(TextMaterial, texture);
        }

        /// A glyph stored rotated in a bitmap font's texture: drawn whole when it is inside the
        /// clip rect, dropped otherwise.
        static void AddRotatedGlyph(Rect q, Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl, Color color, Rect clip)
        {
            if (q.xMin < clip.xMin || q.xMax > clip.xMax || q.yMin < clip.yMin || q.yMax > clip.yMax) return;
            vertices.Add(new ImmediateVertex { Position = ClipStack.ToScreen(new Vector2(q.xMin, q.yMin)), Uv0 = tl, Color = color });
            vertices.Add(new ImmediateVertex { Position = ClipStack.ToScreen(new Vector2(q.xMax, q.yMin)), Uv0 = tr, Color = color });
            vertices.Add(new ImmediateVertex { Position = ClipStack.ToScreen(new Vector2(q.xMax, q.yMax)), Uv0 = br, Color = color });
            vertices.Add(new ImmediateVertex { Position = ClipStack.ToScreen(new Vector2(q.xMin, q.yMax)), Uv0 = bl, Color = color });
        }
    }

    /// Text measurement for IMGUI with the legacy text generator. Sizes are in GUI pixels; a
    /// style's font size 0 means the font's own size.
    internal static class GUIText
    {
        static readonly TextGenerator measure = new() { GuiMetrics = true };
        static Font builtinFont;
        static bool builtinLoaded;

        /// Unity's built-in GUI font (LegacyRuntime.ttf), used when neither the style nor the
        /// skin names one.
        public static Font BuiltinFont
        {
            get
            {
                if (builtinLoaded) return builtinFont;
                try
                {
                    builtinFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    builtinLoaded = true;
                }
                catch (System.IO.IOException)
                {
                    // No player data (no built-in resources file): no default font for now.
                    builtinFont = null;
                }
                return builtinFont;
            }
        }

        /// `aligned`: lines placed by the alignment in the rect (drawing, carets); otherwise the
        /// generator's extents are the text's own size (measuring).
        public static TextGenerationSettings Settings(Font font, int fontSize, FontStyle fontStyle, TextAnchor alignment, bool wordWrap, bool richText, Vector2 extents, bool aligned = false)
        {
            return new TextGenerationSettings
            {
                font = font,
                color = Color.white,
                fontSize = font != null && font.dynamic ? fontSize : 0,
                fontStyle = font != null && font.dynamic ? fontStyle : FontStyle.Normal,
                lineSpacing = LineSpacing(font, fontSize),
                richText = richText,
                scaleFactor = 1f,
                textAnchor = alignment,
                alignByGeometry = false,
                updateBounds = !aligned,
                horizontalOverflow = wordWrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow,
                verticalOverflow = VerticalWrapMode.Overflow,
                generationExtents = new Vector2(Mathf.Max(0f, extents.x), Mathf.Max(0f, extents.y)),
                pivot = new Vector2(0f, 1f),
                generateOutOfBounds = true,
            };
        }

        /// The built-in font, whose IMGUI text heights were measured in the original player.
        static bool IsLegacyRuntime(Font font) => font.name == "LegacyRuntime";

        static int PixelSize(Text.FontNative native, int fontSize) => fontSize != 0 && native.Dynamic ? fontSize : (int)native.FontSize;

        /// The generator's line spacing: the built-in font's lines are drawn 1.1 × size apart,
        /// as its text is measured (Size); other fonts keep their own.
        static float LineSpacing(Font font, int fontSize)
        {
            if (font == null || !IsLegacyRuntime(font)) return 1f;
            var native = (Text.FontNative)font.NativeOrThrow;
            int size = PixelSize(native, fontSize);
            native.PixelLineMetrics(size, out _, out _, out int pitch, out _);
            return pitch > 0 ? 1.1f * size / pitch : 1f;
        }

        /// Height of one line of the font at the style's size (GUIStyle.lineHeight). For the
        /// built-in font it is a whole number: Font.lineHeight (14) at the font's own size.
        public static float LineHeight(Font font, int fontSize)
        {
            if (font == null) return 0f;
            var native = (Text.FontNative)font.NativeOrThrow;
            int size = PixelSize(native, fontSize);
            native.LineMetrics(size, out _, out _, out float lineHeight);
            return IsLegacyRuntime(font) ? Mathf.Floor(lineHeight) : lineHeight;
        }

        /// Size of the text, unwrapped (width ≤ 0) or wrapped at `width`. Empty text measures
        /// one empty line. Widths come from the font's unrounded advances and step up to the
        /// next hundredth ("GUILayout, measured", part 5: "Clear" 30.31, "Count: 59" 54.72).
        /// With the built-in font at size s (part 2), one line of text is s + 0.01 high and
        /// each further line adds 1.1 × s, bold and rich text alike: the lines' s + 1.1 × s
        /// stepped up the same way (27.31 for two at 13).
        public static Vector2 Size(string text, Font font, int fontSize, FontStyle fontStyle, bool richText, float width)
        {
            if (font == null) return Vector2.zero;
            float line = LineHeight(font, fontSize);
            if (string.IsNullOrEmpty(text)) return new Vector2(0f, line);
            bool wrap = width > 0f;
            var settings = Settings(font, fontSize, fontStyle, TextAnchor.UpperLeft, wrap, richText, new Vector2(wrap ? width : 0f, 0f));
            measure.Populate(text, settings);
            var extents = measure.rectExtents;
            float w = NextHundredth(measure.WidestLine);
            if (IsLegacyRuntime(font))
            {
                double s = PixelSize((Text.FontNative)font.NativeOrThrow, fontSize);
                return new Vector2(w, NextHundredth(s + 1.1 * s * Math.Max(0, measure.lineCount - 1)));
            }
            return new Vector2(w, Mathf.Max(extents.height, line));
        }

        /// The next hundredth above `v` (13 → 13.01, 30.303 → 30.31), as the float nearest to it.
        static float NextHundredth(double v) => (float)((Math.Floor(v * 100.0 + 1e-4) + 1.0) / 100.0);

        /// Cursor geometry of laid-out text: for each string index, the top-left position of the
        /// caret relative to the text rect, and the line it is on.
        public static List<(Vector2 Position, int Line)> Carets(string text, Font font, int fontSize, FontStyle fontStyle, TextAnchor alignment, bool wordWrap, Vector2 extents, out float lineHeight, out List<int> lineStarts)
        {
            lineHeight = LineHeight(font, fontSize);
            lineStarts = new List<int> { 0 };
            var result = new List<(Vector2, int)>();
            if (font == null)
            {
                for (int i = 0; i <= (text?.Length ?? 0); i++) result.Add((Vector2.zero, 0));
                return result;
            }
            text ??= "";
            // Rich text is not interpreted while editing (text fields show the markup).
            var settings = Settings(font, fontSize, fontStyle, alignment, wordWrap, false, extents, aligned: true);
            measure.Populate(text, settings);
            var chars = measure.characters;
            var lines = measure.lines;
            lineStarts.Clear();
            foreach (var l in lines) lineStarts.Add(l.startCharIdx);
            if (lineStarts.Count == 0) lineStarts.Add(0);
            int line = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                while (line + 1 < lineStarts.Count && i >= lineStarts[line + 1]) line++;
                if (i < chars.Count)
                {
                    var p = chars[i].cursorPos;
                    result.Add((new Vector2(p.x, -p.y), line));
                }
                else
                {
                    var prev = result.Count > 0 ? result[^1] : (Vector2.zero, 0);
                    result.Add(prev);
                }
            }
            return result;
        }
    }
}
