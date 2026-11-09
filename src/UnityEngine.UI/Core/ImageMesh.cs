using System;
using UnityEngine.Sprites;

namespace UnityEngine.UI
{
    /// Geometry of Image's draw types. Quads are emitted with their corners bottom left, top left,
    /// top right, bottom right, as two triangles.
    internal static class ImageMesh
    {
        const int MaxVertices = 65000;

        static void Quad(VertexHelper vh, float x0, float y0, float x1, float y1, Color32 c, float u0, float v0, float u1, float v1)
        {
            int start = vh.currentVertCount;
            vh.AddVert(new Vector3(x0, y0), c, new Vector2(u0, v0));
            vh.AddVert(new Vector3(x0, y1), c, new Vector2(u0, v1));
            vh.AddVert(new Vector3(x1, y1), c, new Vector2(u1, v1));
            vh.AddVert(new Vector3(x1, y0), c, new Vector2(u1, v0));
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start + 2, start + 3, start);
        }

        /// The largest rect of the sprite's aspect inside r, placed by the pivot.
        public static Rect FitAspect(Rect r, Vector2 spriteSize, Vector2 pivot)
        {
            if (spriteSize.x <= 0f || spriteSize.y <= 0f) return r;
            float aspect = spriteSize.x / spriteSize.y;
            if (aspect > r.width / r.height)
            {
                float h = r.width / aspect;
                r.y += (r.height - h) * pivot.y;
                r.height = h;
            }
            else
            {
                float w = r.height * aspect;
                r.x += (r.width - w) * pivot.x;
                r.width = w;
            }
            return r;
        }

        /// The area the sprite's pixels cover in the rect: trimmed (padding) pixels are left out,
        /// in proportion to the sprite's size. (xMin, yMin, xMax, yMax).
        static Vector4 Covered(Image image, Sprite s, bool preserveAspect)
        {
            var r = image.GetPixelAdjustedRect();
            var size = s.rect.size;
            if (preserveAspect && size.sqrMagnitude > 0f) r = FitAspect(r, size, image.rectTransform.pivot);
            var pad = DataUtility.GetPadding(s);
            float w = Mathf.Round(size.x), h = Mathf.Round(size.y);
            return new Vector4(
                r.x + r.width * (pad.x / w),
                r.y + r.height * (pad.y / h),
                r.x + r.width * ((w - pad.z) / w),
                r.y + r.height * ((h - pad.w) / h));
        }

        public static void Simple(Image image, VertexHelper vh, Sprite s, bool preserveAspect)
        {
            var v = Covered(image, s, preserveAspect);
            var uv = DataUtility.GetOuterUV(s);
            vh.Clear();
            Quad(vh, v.x, v.y, v.z, v.w, image.color, uv.x, uv.y, uv.z, uv.w);
        }

        /// The sprite's own mesh, scaled from its bounds to the rect around the pivots.
        public static void SpriteMesh(Image image, VertexHelper vh, Sprite s, bool preserveAspect)
        {
            var size = s.rect.size;
            var spritePivot = s.pivot / size;
            var r = image.GetPixelAdjustedRect();
            var pivot = image.rectTransform.pivot;
            if (preserveAspect && size.sqrMagnitude > 0f) r = FitAspect(r, size, pivot);
            var drawSize = r.size;
            var bounds = s.bounds.size;
            var offset = (pivot - spritePivot) * drawSize;
            Color32 c = image.color;
            vh.Clear();
            var vertices = s.vertices;
            var uvs = s.uv;
            for (int i = 0; i < vertices.Length; i++)
                vh.AddVert(new Vector3(vertices[i].x / bounds.x * drawSize.x - offset.x, vertices[i].y / bounds.y * drawSize.y - offset.y), c, uvs[i]);
            var triangles = s.triangles;
            for (int i = 0; i + 2 < triangles.Length; i += 3) vh.AddTriangle(triangles[i], triangles[i + 1], triangles[i + 2]);
        }

        [ThreadStatic] static float[] xs, ys;

        /// The borders in the snapped rect: scaled per axis by the snapped rect's size over the
        /// rect's (they follow the pixel snapping), then scaled down together where they do not
        /// fit. (Measured: the arithmetic decides whether a quad between borders that meet comes
        /// out empty, and empty quads are left out.)
        static Vector4 FittedBorder(Image image, Vector4 border, Rect snapped)
        {
            var original = image.rectTransform.rect;
            for (int axis = 0; axis < 2; axis++)
            {
                if (original.size[axis] != 0f)
                {
                    float k = snapped.size[axis] / original.size[axis];
                    border[axis] *= k;
                    border[axis + 2] *= k;
                }
                float both = border[axis] + border[axis + 2];
                if (snapped.size[axis] < both && both != 0f)
                {
                    float k = snapped.size[axis] / both;
                    border[axis] *= k;
                    border[axis + 2] *= k;
                }
            }
            return border;
        }

        /// The four lines across one axis, from the rect's start: the outer edges inset by the
        /// trimmed pixels, the inner ones by the borders.
        static void Lines(float start, float length, float near, float far, float padNear, float padFar, float[] at)
        {
            at[0] = padNear + start;
            at[1] = near + start;
            at[2] = length - far + start;
            at[3] = length - padFar + start;
        }

        /// Nine quads (the centre only with fillCenter), column by column, each column bottom to
        /// top; quads without a positive width and height are left out. Trimmed pixels are left
        /// out of the outer edge.
        public static void Sliced(Image image, VertexHelper vh, Sprite s, float pixelsPerUnit, bool fillCenter)
        {
            if (!image.hasBorder)
            {
                Simple(image, vh, s, false);
                return;
            }
            var outer = DataUtility.GetOuterUV(s);
            var inner = DataUtility.GetInnerUV(s);
            var pad = DataUtility.GetPadding(s) / pixelsPerUnit;
            var r = image.GetPixelAdjustedRect();
            var border = FittedBorder(image, s.border / pixelsPerUnit, r);
            xs ??= new float[4];
            ys ??= new float[4];
            Lines(r.x, r.width, border.x, border.z, pad.x, pad.z, xs);
            Lines(r.y, r.height, border.y, border.w, pad.y, pad.w, ys);
            float[] us = [outer.x, inner.x, inner.z, outer.z];
            float[] vs = [outer.y, inner.y, inner.w, outer.w];
            Color32 c = image.color;
            vh.Clear();
            for (int x = 0; x < 3; x++)
                for (int y = 0; y < 3; y++)
                {
                    if (!fillCenter && x == 1 && y == 1) continue;
                    if (!(xs[x + 1] > xs[x]) || !(ys[y + 1] > ys[y])) continue;
                    Quad(vh, xs[x], ys[y], xs[x + 1], ys[y + 1], c, us[x], vs[y], us[x + 1], vs[y + 1]);
                }
        }

        /// The centre repeats at the sprite's centre size; borders as in Sliced. A wrapping,
        /// unpacked sprite without borders is one quad with repeating texture coordinates.
        public static void Tiled(Image image, VertexHelper vh, Sprite s, float pixelsPerUnit, bool fillCenter)
        {
            var outer = DataUtility.GetOuterUV(s);
            var inner = DataUtility.GetInnerUV(s);
            var b = s.border;
            var r = image.GetPixelAdjustedRect();
            float tileW = (s.rect.width - b.x - b.z) / pixelsPerUnit;
            float tileH = (s.rect.height - b.y - b.w) / pixelsPerUnit;
            xs ??= new float[4];
            ys ??= new float[4];
            var border = FittedBorder(image, b / pixelsPerUnit, r);
            Lines(r.x, r.width, border.x, border.z, 0f, 0f, xs);
            Lines(r.y, r.height, border.y, border.w, 0f, 0f, ys);
            float x1 = xs[1], x2 = xs[2], y1 = ys[1], y2 = ys[2];
            if (tileW <= 0f) tileW = x2 - x1;
            if (tileH <= 0f) tileH = y2 - y1;
            Color32 c = image.color;
            vh.Clear();

            bool bordered = image.hasBorder;
            if (!bordered && !s.packed && s.texture != null && s.texture.wrapMode == TextureWrapMode.Repeat)
            {
                if (!fillCenter) return;
                float repeatX = (x2 - x1) / tileW, repeatY = (y2 - y1) / tileH;
                Quad(vh, x1, y1, x2, y2, c, inner.x, inner.y, inner.x + (inner.z - inner.x) * repeatX, inner.y + (inner.w - inner.y) * repeatY);
                return;
            }

            long across = tileW > 0f ? (long)Math.Ceiling((x2 - x1) / tileW) : 0;
            long up = tileH > 0f ? (long)Math.Ceiling((y2 - y1) / tileH) : 0;
            double quads = (fillCenter ? (double)across * up : 0) + (bordered ? 2.0 * (across + up) + 4 : 0);
            if (quads * 4 > MaxVertices)
            {
                Debug.LogError("Too many sprite tiles on Image \"" + image.name + "\". The tile size will be increased. To remove the limit on the number of tiles, set the Wrap mode to Repeat in the Image Import Settings", image);
                // Fewer, larger tiles of the same proportions.
                double budget = MaxVertices / 4.0 - (bordered ? 4 : 0);
                double ratio = up > 0 ? (double)across / up : 1;
                double fitUp = fillCenter ? Math.Sqrt(budget / Math.Max(ratio, 1e-9)) : budget / (2 * (1 + ratio));
                up = Math.Max(1, (long)Math.Floor(fitUp - (fillCenter && bordered ? 2 : 0)));
                across = Math.Max(1, (long)Math.Floor(up * ratio));
                tileW = (x2 - x1) / across;
                tileH = (y2 - y1) / up;
            }

            if (fillCenter)
                for (long j = 0; j < up; j++)
                {
                    float ty0 = y1 + j * tileH, ty1 = Math.Min(y1 + (j + 1) * tileH, y2);
                    float tv1 = inner.y + (inner.w - inner.y) * ((ty1 - ty0) / tileH);
                    for (long i = 0; i < across; i++)
                    {
                        float tx0 = x1 + i * tileW, tx1 = Math.Min(x1 + (i + 1) * tileW, x2);
                        float tu1 = inner.x + (inner.z - inner.x) * ((tx1 - tx0) / tileW);
                        Quad(vh, tx0, ty0, tx1, ty1, c, inner.x, inner.y, tu1, tv1);
                    }
                }

            if (!bordered) return;
            for (long j = 0; j < up; j++)
            {
                float ty0 = y1 + j * tileH, ty1 = Math.Min(y1 + (j + 1) * tileH, y2);
                float tv1 = inner.y + (inner.w - inner.y) * ((ty1 - ty0) / tileH);
                Quad(vh, r.x, ty0, x1, ty1, c, outer.x, inner.y, inner.x, tv1);
                Quad(vh, x2, ty0, r.xMax, ty1, c, inner.z, inner.y, outer.z, tv1);
            }
            for (long i = 0; i < across; i++)
            {
                float tx0 = x1 + i * tileW, tx1 = Math.Min(x1 + (i + 1) * tileW, x2);
                float tu1 = inner.x + (inner.z - inner.x) * ((tx1 - tx0) / tileW);
                Quad(vh, tx0, r.y, tx1, y1, c, inner.x, outer.y, tu1, inner.y);
                Quad(vh, tx0, y2, tx1, r.yMax, c, inner.x, inner.w, tu1, outer.w);
            }
            Quad(vh, r.x, r.y, x1, y1, c, outer.x, outer.y, inner.x, inner.y);
            Quad(vh, x2, r.y, r.xMax, y1, c, inner.z, outer.y, outer.z, inner.y);
            Quad(vh, r.x, y2, x1, r.yMax, c, outer.x, inner.w, inner.x, outer.w);
            Quad(vh, x2, y2, r.xMax, r.yMax, c, inner.z, inner.w, outer.z, outer.w);
        }

        // ----- Filled -----

        /// Positions and texture coordinates of a quad's corners (bottom left, top left, top right,
        /// bottom right), cut by the fill.
        struct Corners
        {
            public Vector2 P0, P1, P2, P3;
            public Vector2 T0, T1, T2, T3;

            public Vector2 P(int i) => i switch { 0 => P0, 1 => P1, 2 => P2, _ => P3 };
            public Vector2 T(int i) => i switch { 0 => T0, 1 => T1, 2 => T2, _ => T3 };

            public void Set(int i, Vector2 p, Vector2 t)
            {
                switch (i)
                {
                    case 0: P0 = p; T0 = t; break;
                    case 1: P1 = p; T1 = t; break;
                    case 2: P2 = p; T2 = t; break;
                    default: P3 = p; T3 = t; break;
                }
            }

            public static Corners Of(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1) => new()
            {
                P0 = new(x0, y0), P1 = new(x0, y1), P2 = new(x1, y1), P3 = new(x1, y0),
                T0 = new(u0, v0), T1 = new(u0, v1), T2 = new(u1, v1), T3 = new(u1, v0),
            };

            public void Emit(VertexHelper vh, Color32 c)
            {
                int start = vh.currentVertCount;
                for (int i = 0; i < 4; i++) vh.AddVert(P(i), c, T(i));
                vh.AddTriangle(start, start + 1, start + 2);
                vh.AddTriangle(start + 2, start + 3, start);
            }
        }

        /// Linear fills cut the quad; radial fills sweep it around a corner (90), the middle of an
        /// edge (180) or the centre (360), splitting it into the quarters each sweep crosses.
        public static void Filled(Image image, VertexHelper vh, Sprite s, bool preserveAspect, Image.FillMethod method, float amount, int origin, bool clockwise)
        {
            vh.Clear();
            if (amount < 0.001f) return;
            var v = Covered(image, s, preserveAspect);
            var uv = DataUtility.GetOuterUV(s);
            Color32 c = image.color;
            float x0 = v.x, y0 = v.y, x1 = v.z, y1 = v.w;
            float u0 = uv.x, v0 = uv.y, u1 = uv.z, v1 = uv.w;

            if (method == Image.FillMethod.Horizontal)
            {
                float du = (u1 - u0) * amount, dx = (x1 - x0) * amount;
                if (origin == 1) { x0 = x1 - dx; u0 = u1 - du; }
                else { x1 = x0 + dx; u1 = u0 + du; }
                Corners.Of(x0, y0, x1, y1, u0, v0, u1, v1).Emit(vh, c);
                return;
            }
            if (method == Image.FillMethod.Vertical)
            {
                float dv = (v1 - v0) * amount, dy = (y1 - y0) * amount;
                if (origin == 1) { y0 = y1 - dy; v0 = v1 - dv; }
                else { y1 = y0 + dy; v1 = v0 + dv; }
                Corners.Of(x0, y0, x1, y1, u0, v0, u1, v1).Emit(vh, c);
                return;
            }

            var full = Corners.Of(x0, y0, x1, y1, u0, v0, u1, v1);
            switch (method)
            {
                case Image.FillMethod.Radial90:
                    if (Sweep(ref full, amount, clockwise, origin)) full.Emit(vh, c);
                    return;
                case Image.FillMethod.Radial180:
                    for (int half = 0; half < 2; half++)
                    {
                        var part = Half(full, origin, half);
                        // The halves fill one after the other, the first from the origin's side.
                        float share = Mathf.Clamp01(amount * 2f - (clockwise ? half : 1 - half));
                        int corner = HalfCorner(origin, half, clockwise);
                        if (Sweep(ref part, share, clockwise, corner)) part.Emit(vh, c);
                    }
                    return;
                default:
                    for (int q = 0; q < 4; q++)
                    {
                        var part = Quarter(full, q);
                        int order = (q - Quarter360Start(origin) + 4) % 4;
                        if (!clockwise) order = 3 - order;
                        float share = Mathf.Clamp01(amount * 4f - order);
                        if (Sweep(ref part, share, clockwise, (q + 2) % 4)) part.Emit(vh, c);
                    }
                    return;
            }
        }

        static Vector2 Mid(Vector2 a, Vector2 b) => (a + b) * 0.5f;

        /// One quarter of the quad: 0 bottom left, 1 top left, 2 top right, 3 bottom right.
        static Corners Quarter(Corners f, int q)
        {
            var cp = Mid(f.P0, f.P2);
            var ct = Mid(f.T0, f.T2);
            var r = f;
            int opposite = (q + 2) % 4;
            r.Set(opposite, cp, ct);
            int a = (q + 1) % 4, b = (q + 3) % 4;
            r.Set(a, Mid(f.P(q), f.P(a)), Mid(f.T(q), f.T(a)));
            r.Set(b, Mid(f.P(q), f.P(b)), Mid(f.T(q), f.T(b)));
            return r;
        }

        /// The quarter (as numbered in Quarter) the 360 sweep starts in, clockwise, for each origin
        /// (bottom, right, top, left): the one clockwise after the origin's edge middle.
        static int Quarter360Start(int origin) => origin switch { 0 => 0, 1 => 3, 2 => 2, _ => 1 };

        /// A half of the quad for a 180 fill around the middle of the origin's edge (bottom, left,
        /// top, right): half 0 is the one the clockwise sweep starts in.
        static Corners Half(Corners f, int origin, int half)
        {
            var r = f;
            switch (origin)
            {
                case 0: // bottom edge: left half, then right half
                case 2: // top edge: right half, then left half
                {
                    bool left = (origin == 0) == (half == 0);
                    var mb = Mid(f.P0, f.P3); var mt = Mid(f.P1, f.P2);
                    var tb = Mid(f.T0, f.T3); var tt = Mid(f.T1, f.T2);
                    if (left) { r.Set(3, mb, tb); r.Set(2, mt, tt); }
                    else { r.Set(0, mb, tb); r.Set(1, mt, tt); }
                    break;
                }
                default: // left edge: top half, then bottom; right edge: bottom half, then top
                {
                    bool top = (origin == 1) == (half == 0);
                    var ml = Mid(f.P0, f.P1); var mr = Mid(f.P3, f.P2);
                    var tl = Mid(f.T0, f.T1); var tr = Mid(f.T3, f.T2);
                    if (top) { r.Set(0, ml, tl); r.Set(3, mr, tr); }
                    else { r.Set(1, ml, tl); r.Set(2, mr, tr); }
                    break;
                }
            }
            return r;
        }

        /// The corner of a half that the sweep pivots on: the end of the half's side that lies at
        /// the middle of the origin's edge.
        static int HalfCorner(int origin, int half, bool clockwise) => origin switch
        {
            0 => (half == 0) ? 3 : 0,
            1 => (half == 0) ? 0 : 1,
            2 => (half == 0) ? 1 : 2,
            _ => (half == 0) ? 2 : 3,
        };

        /// Cuts a quad to the part swept by `amount` of a quarter turn around corner `pivot`, in
        /// the quad's own (normalized) frame: a clockwise sweep starts along the edge to the next
        /// corner clockwise and ends along the edge to the previous one. The two corners past the
        /// cut collapse onto the point where the sweep leaves the quad. False when nothing is left.
        static bool Sweep(ref Corners q, float amount, bool clockwise, int pivot)
        {
            if (amount < 0.001f) return false;
            if (amount >= 1f) return true;
            int far = (pivot + 2) % 4;
            // Corners run clockwise: bottom left, top left, top right, bottom right.
            int first = clockwise ? (pivot + 1) % 4 : (pivot + 3) % 4;
            int last = clockwise ? (pivot + 3) % 4 : (pivot + 1) % 4;
            float angle = amount * 90f * Mathf.Deg2Rad;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            if (sin <= cos)
            {
                // It leaves through the edge from `first` to `far`.
                float t = sin / cos;
                var p = Vector2.Lerp(q.P(first), q.P(far), t);
                var uv = Vector2.Lerp(q.T(first), q.T(far), t);
                q.Set(far, p, uv);
                q.Set(last, p, uv);
            }
            else
            {
                // It leaves through the edge from `last` to `far`.
                float t = cos / sin;
                var p = Vector2.Lerp(q.P(last), q.P(far), t);
                var uv = Vector2.Lerp(q.T(last), q.T(far), t);
                q.Set(last, p, uv);
            }
            return true;
        }

        /// A point in the rect (from its corner) as a position in the sprite's pixels.
        public static Vector2 ToSpritePixels(Vector2 local, Rect r, Sprite s, Image.Type type, float pixelsPerUnit)
        {
            var size = s.rect.size;
            if (type != Image.Type.Sliced && type != Image.Type.Tiled || s.border.sqrMagnitude <= 0f)
                return new Vector2(local.x / r.width * size.x, local.y / r.height * size.y);
            var b = s.border;
            return new Vector2(Axis(local.x, r.width, b.x, b.z, size.x, type, pixelsPerUnit), Axis(local.y, r.height, b.y, b.w, size.y, type, pixelsPerUnit));
        }

        static float Axis(float at, float length, float near, float far, float pixels, Image.Type type, float ppu)
        {
            float n = near / ppu, f = far / ppu;
            if (at < n) return at * ppu;
            if (at > length - f) return pixels - (length - at) * ppu;
            float centre = pixels - near - far;
            float inside = at - n;
            if (type == Image.Type.Tiled) return near + Mathf.Repeat(inside * ppu, centre);
            float span = length - n - f;
            return near + (span > 0f ? inside / span * centre : 0f);
        }
    }
}
