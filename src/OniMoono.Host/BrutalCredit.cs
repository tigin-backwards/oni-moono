using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace OniMoono.Host
{
    /// The BRUTAL License (§2.2) asks for BRUTAL's logo, unaltered except for scaling, to be shown
    /// before gameplay and no less prominently than other parties' logos; its maintainers note a
    /// main menu placement meets that. The logo (ahwootech/brutal-branding) is drawn on the game's
    /// main menu as one more of its news tiles, their size, with a caption. Only while the main
    /// menu is up and nothing covers it; nothing in the game is changed.
    internal sealed class BrutalCredit : MonoBehaviour
    {
        const string Caption = "This logo is only here because the people who made BRUTAL require it to be here. "
                               + "(BRUTAL License, section 2.2. Hi, Ahwoo.)";

        Texture2D logo = null!;
        GUIStyle? headerStyle, captionStyle;
        Func<object?> mainMenu = () => null;
        Func<object?> videoScreen = () => null;
        Func<bool> modalOpen = () => false;

        public static void Install()
        {
            var go = new GameObject("BrutalCredit");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<BrutalCredit>();
        }

        void Awake()
        {
            // It lays nothing out: no Layout events.
            useGUILayout = false;
            using var stream = typeof(BrutalCredit).Assembly.GetManifestResourceStream("LOGO_BRUTAL_WHT.png")!;
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            logo = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            logo.LoadImage(data);
            // The game's MainMenu.Instance, read without referencing the game's assembly.
            var instance = Type.GetType("MainMenu, Assembly-CSharp")?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            mainMenu = instance == null ? () => null : () => instance.GetValue(null);
            // VideoScreen.Instance: the intros and cutscenes play over the menu.
            var video = Type.GetType("VideoScreen, Assembly-CSharp")?.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            videoScreen = video == null ? () => null : () => video.GetValue(null);
            // A modal screen open over the menu (the new game's steps, Load Game, Options, Mods)
            // dims and covers the news tiles: KScreenManager.Instance's screen stack.
            var manager = Type.GetType("KScreenManager, Assembly-CSharp-firstpass");
            var managerInstance = manager?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var stack = manager?.GetField("screenStack", BindingFlags.NonPublic | BindingFlags.Instance);
            var modals = new[] { Type.GetType("KModalScreen, Assembly-CSharp"), Type.GetType("KModalButtonMenu, Assembly-CSharp") };
            if (managerInstance != null && stack != null)
                modalOpen = () =>
                {
                    if (stack.GetValue(managerInstance.GetValue(null)) is not System.Collections.IEnumerable screens) return false;
                    foreach (var screen in screens)
                        if (screen is MonoBehaviour { isActiveAndEnabled: true } open && Array.Exists(modals, m => m != null && m.IsInstanceOfType(open)))
                            return true;
                    return false;
                };
        }

        Transform? motd;

        /// The screen rects (GUI space, from the top left) of the main menu's news tiles, the
        /// children of its "MOTD" row or column.
        List<Rect> NewsTiles(MonoBehaviour menu)
        {
            var tiles = new List<Rect>();
            if (motd == null) motd = Find(menu.transform, "MOTD");
            if (motd == null) return tiles;
            var corners = new Vector3[4];
            foreach (Transform child in motd)
            {
                if (!child.gameObject.activeInHierarchy || child is not RectTransform rect) continue;
                rect.GetWorldCorners(corners);
                // Overlay canvases: world space is screen pixels from the bottom left.
                tiles.Add(new Rect(corners[0].x, Screen.height - corners[1].y, corners[2].x - corners[0].x, corners[1].y - corners[0].y));
            }
            return tiles;
        }

        /// The news tiles' frame: their FrameBG image's sprite (a sliced panel with a one-unit
        /// dark edge) and colour, and how many screen pixels its border takes, read from the
        /// first tile; null until there is one.
        (Sprite Sprite, Color Color, float Scale)? frame;

        /// Draws `tile` as the news tiles draw their frame: the same sprite, sliced with its
        /// border at the same screen size, in the same translucent colour. False when the
        /// tiles' frame can't be read.
        bool DrawFrame(Rect tile)
        {
            if (frame == null && motd != null)
            {
                foreach (Transform box in motd)
                {
                    if (!box.gameObject.activeInHierarchy || box.Find("FrameBG") is not RectTransform bg) continue;
                    foreach (var c in bg.GetComponents<Component>())
                    {
                        var type = c.GetType();
                        if (type.Name != "Image" || type.GetProperty("sprite")?.GetValue(c) is not Sprite sprite) continue;
                        var color = (Color)type.GetProperty("color")!.GetValue(c)!;
                        // Sprite pixels per canvas unit, times the image's multiplier.
                        float perUnit = (float)type.GetProperty("pixelsPerUnit")!.GetValue(c)!;
                        if (type.GetProperty("pixelsPerUnitMultiplier")?.GetValue(c) is float multiplier) perUnit *= multiplier;
                        var corners = new Vector3[4];
                        bg.GetWorldCorners(corners);
                        float screenPerUnit = (corners[2].x - corners[0].x) / Mathf.Max(1f, bg.rect.width);
                        frame = (sprite, color, screenPerUnit / Mathf.Max(0.0001f, perUnit));
                        break;
                    }
                    break;
                }
            }
            if (frame is not var (s, tint, scale) || s == null || s.texture == null) return false;
            var tex = s.texture;
            var r = s.textureRect;
            var b = s.border; // left, bottom, right, top, in sprite pixels
            // Whole pixels, as the tiles come out: the pixels whose centres the rect covers, and
            // borders of at least a pixel (the tiles' one-unit edge is one dark pixel at 720p to
            // 1440p, measured in the original).
            static float Snap(float v) => Mathf.Ceil(v - 0.5f);
            static float Edge(float v) => v <= 0f ? 0f : Mathf.Max(1f, Mathf.Round(v));
            tile = Rect.MinMaxRect(Snap(tile.xMin), Snap(tile.yMin), Snap(tile.xMax), Snap(tile.yMax));
            float l = Edge(b.x * scale), bo = Edge(b.y * scale), ri = Edge(b.z * scale), t = Edge(b.w * scale);
            GUI.color = tint;
            // Nine parts: rows top to bottom in GUI space, sprite rows bottom to top.
            float[] xs = { tile.x, tile.x + l, tile.xMax - ri, tile.xMax };
            float[] ys = { tile.y, tile.y + t, tile.yMax - bo, tile.yMax };
            float[] us = { r.x, r.x + b.x, r.xMax - b.z, r.xMax };
            float[] vs = { r.yMax, r.yMax - b.w, r.y + b.y, r.y };
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                {
                    var dest = Rect.MinMaxRect(xs[col], ys[row], xs[col + 1], ys[row + 1]);
                    if (dest.width <= 0f || dest.height <= 0f) continue;
                    var uv = Rect.MinMaxRect(us[col] / tex.width, vs[row + 1] / tex.height, us[col + 1] / tex.width, vs[row] / tex.height);
                    GUI.DrawTextureWithTexCoords(dest, tex, uv);
                }
            return true;
        }

        static Transform? Find(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform child in t)
                if (Find(child, name) is { } found) return found;
            return null;
        }

        void OnGUI()
        {
            // Not a behaviour of the game's: it draws only, behind every other behaviour, so
            // that input reaches the game's and mods' OnGUI as in the original player (the
            // front behaviour is the one that gets an event a window used).
            GUI.depth = int.MaxValue;
            if (Event.current.type != EventType.Repaint || mainMenu() is not MonoBehaviour menu || menu == null || !menu.isActiveAndEnabled) return;
            if (videoScreen() is MonoBehaviour video && video != null && video.isActiveAndEnabled) return;
            if (modalOpen()) return;
            // One more tile beside the menu's news tiles ("It's Klei Fest!", "Out Now!"), their size:
            // left of the row they form (above its last tile when the row fills the screen's
            // width), right of the first of the column the other menu layout stacks them in.
            var tiles = NewsTiles(menu);
            if (tiles.Count == 0 || tiles[0].width < 1f) return;
            bool row = tiles.Count > 1 && Mathf.Abs(tiles[1].x - tiles[0].x) > Mathf.Abs(tiles[1].y - tiles[0].y);
            float gap = 8f * Screen.height / 1440f;
            Rect tile;
            if (row)
            {
                var first = tiles[0];
                foreach (var t in tiles) if (t.x < first.x) first = t;
                tile = new Rect(first.x - first.width - gap, first.y, first.width, first.height);
                // On a narrow screen the row fills the bottom, its left end reaching under the menu's
                // buttons: above its last tile instead, over the background.
                if (tile.x < gap)
                {
                    var last = tiles[0];
                    foreach (var t in tiles) if (t.x > last.x) last = t;
                    tile = new Rect(last.x, last.y - last.height - gap, last.width, last.height);
                }
            }
            else
            {
                var first = tiles[0];
                foreach (var t in tiles) if (t.y < first.y) first = t;
                tile = new Rect(first.xMax + gap, first.y, first.width, first.height);
            }
            float unit = tile.width / 300f;
            float pad = 8f * unit;
            headerStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            captionStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.UpperCenter };
            headerStyle.fontSize = Mathf.Max(9, Mathf.RoundToInt(15f * unit));
            headerStyle.normal.textColor = new Color(0.94f, 0.47f, 0.47f);
            captionStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(12f * unit));
            captionStyle.normal.textColor = new Color(0.9f, 0.9f, 0.92f);
            float inner = tile.width - 2f * pad;
            float headerHeight = 24f * unit;
            float logoHeight = inner * logo.height / logo.width;
            var tint = GUI.color;
            if (!DrawFrame(tile))
            {
                GUI.color = new Color(0.17f, 0.19f, 0.26f, 0.92f);
                GUI.DrawTexture(tile, Texture2D.whiteTexture);
            }
            GUI.color = tint;
            GUI.Label(new Rect(tile.x, tile.y + pad * 0.5f, tile.width, headerHeight), "Legally Required!", headerStyle);
            float y = tile.y + headerHeight + pad;
            GUI.DrawTexture(new Rect(tile.x + pad, y, inner, logoHeight), logo, ScaleMode.ScaleToFit);
            y += logoHeight + pad;
            GUI.Label(new Rect(tile.x + pad, y, inner, tile.yMax - y - pad * 0.5f), Caption, captionStyle);
        }
    }
}
