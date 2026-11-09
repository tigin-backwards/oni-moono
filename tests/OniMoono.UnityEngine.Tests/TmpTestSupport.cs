using System;
using System.IO;
using OniMoono.Engine.Assets;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// The game data the TextMeshPro tests use (fonts, sprite assets, settings).
    static class TmpTestSupport
    {
        static bool s_Loaded;

        /// The game's data (TMP Settings, fonts) through the asset system.
        internal static void LoadGameData()
        {
            if (s_Loaded) return;
            AssetSystem.Initialize(GameInstall.DataDir);
            ProjectSettings.Load();
            s_Loaded = true;
        }

        internal static T Load<T>(string file, long pathId) where T : Object
        {
            LoadGameData();
            return (T)ObjectLoader.Load(AssetSystem.Get(file), pathId);
        }

        /// Where the game's TextMeshPro assets are (file and path id), by name.
        static readonly System.Collections.Generic.Dictionary<string, (string File, long PathId)> s_Assets = new()
        {
            ["LiberationSans SDF"] = ("sharedassets0.assets", 17851),
            ["LiberationSans SDF - Fallback"] = ("sharedassets0.assets", 17850),
            ["NotoSans-Regular"] = ("sharedassets0.assets", 20151),
            ["RobotoCondensed-Regular"] = ("sharedassets0.assets", 20156),
            ["Economica-Bold-OTF SDF"] = ("sharedassets0.assets", 20028),
            ["GRAYSTROKE REGULAR SDF"] = ("sharedassets0.assets", 20032),
            ["GRAYSTROKE OUTLINE SDF"] = ("sharedassets0.assets", 20031),
            ["NotoSansCJKjp-Regular"] = ("sharedassets0.assets", 20153),
            ["NotoSansCJKkr-Regular"] = ("sharedassets0.assets", 20154),
            ["NotoSansCJKsc-Regular"] = ("sharedassets0.assets", 20155),
            ["NotoSansThai-Regular"] = ("resources.assets", 194),
            ["oni_sprite_assets"] = ("resources.assets", 177),
            ["EmojiOne"] = ("resources.assets", 178),
        };

        internal static T FindAsset<T>(string name) where T : Object
        {
            var (file, id) = s_Assets[name];
            return Load<T>(file, id);
        }

        /// The game's main UI font (GRAYSTROKE REGULAR SDF, a static SDF asset at 124 pt).
        internal static TMP_FontAsset Graystroke => Load<TMP_FontAsset>("sharedassets0.assets", 20032);

        /// A root overlay canvas with a text of the given rect size under it.
        internal static TextMeshProUGUI NewText(string text, TMP_FontAsset font, float fontSize, Vector2 size)
        {
            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = size;
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = font;
            t.fontSize = fontSize;
            t.text = text;
            t.ForceMeshUpdate(true);
            return t;
        }
    }
}
