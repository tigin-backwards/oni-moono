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
        internal static string GameDir =>
            Environment.GetEnvironmentVariable("ONI_GAME_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762230");

        static bool s_Loaded;

        /// The game's data (TMP Settings, fonts) through the asset system.
        internal static void LoadGameData()
        {
            if (s_Loaded) return;
            AssetSystem.Initialize(Path.Combine(GameDir, "OxygenNotIncluded_Data"));
            ProjectSettings.Load();
            s_Loaded = true;
        }

        internal static T Load<T>(string file, long pathId) where T : Object
        {
            LoadGameData();
            return (T)ObjectLoader.Load(AssetSystem.Get(file), pathId);
        }

        /// Where the game's TextMeshPro assets are, by name: the file to look in. The path id is
        /// found by name (FindId), since it changes between game builds.
        static readonly System.Collections.Generic.Dictionary<string, string> s_Assets = new()
        {
            ["LiberationSans SDF"] = "sharedassets0.assets",
            ["LiberationSans SDF - Fallback"] = "sharedassets0.assets",
            ["NotoSans-Regular"] = "sharedassets0.assets",
            ["RobotoCondensed-Regular"] = "sharedassets0.assets",
            ["Economica-Bold-OTF SDF"] = "sharedassets0.assets",
            ["GRAYSTROKE REGULAR SDF"] = "sharedassets0.assets",
            ["GRAYSTROKE OUTLINE SDF"] = "sharedassets0.assets",
            ["NotoSansCJKjp-Regular"] = "sharedassets0.assets",
            ["NotoSansCJKkr-Regular"] = "sharedassets0.assets",
            ["NotoSansCJKsc-Regular"] = "sharedassets0.assets",
            ["NotoSansThai-Regular"] = "resources.assets",
            ["oni_sprite_assets"] = "resources.assets",
            ["EmojiOne"] = "resources.assets",
        };

        internal static T FindAsset<T>(string name) where T : Object => Load<T>(s_Assets[name], name);

        /// The asset of this name in `file`, of class `T`.
        internal static T Load<T>(string file, string name) where T : Object => Load<T>(file, FindId(file, name, typeof(T)));

        static readonly System.Collections.Generic.Dictionary<(string, string, System.Type), long> s_Ids = new();

        /// The path id of the object named `name` in `file` that loads as a `type`. Names are read
        /// from the data without loading: a native object's m_Name comes first; a MonoBehaviour's
        /// follows m_GameObject, m_Enabled and m_Script (28 bytes). Candidates whose name matches
        /// are loaded to check their type.
        internal static long FindId(string file, string name, System.Type type)
        {
            if (s_Ids.TryGetValue((file, name, type), out var known)) return known;
            LoadGameData();
            var sf = AssetSystem.Get(file);
            foreach (var info in sf.Objects)
            {
                int classId = sf.TypeOf(info).ClassId;
                var r = sf.OpenObject(info);
                try
                {
                    if (classId == 114) r.Skip(28);
                    int length = r.ReadInt32();
                    if (length != System.Text.Encoding.UTF8.GetByteCount(name)) continue;
                    if (System.Text.Encoding.UTF8.GetString(r.ReadSpan(length)) != name) continue;
                }
                catch (System.Exception e) when (e is System.IO.EndOfStreamException or System.ArgumentOutOfRangeException or System.IO.InvalidDataException) { continue; }
                if (!type.IsInstanceOfType(ObjectLoader.Load(sf, info.PathId))) continue;
                s_Ids[(file, name, type)] = info.PathId;
                return info.PathId;
            }
            throw new System.Collections.Generic.KeyNotFoundException($"{file} has no {type.Name} named {name}");
        }

        /// The game's main UI font (GRAYSTROKE REGULAR SDF, a static SDF asset at 124 pt).
        internal static TMP_FontAsset Graystroke => FindAsset<TMP_FontAsset>("GRAYSTROKE REGULAR SDF");

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
