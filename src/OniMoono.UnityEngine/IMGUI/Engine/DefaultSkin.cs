using System.IO;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Assets;
using UnityEngine;

namespace OniMoono.Engine.IMGUI
{
    /// The skin every OnGUI call starts with: the GUISkin "GameSkin" of the built-in resources
    /// (a MonoBehaviour object), or an empty skin of that name when there is no player data.
    internal static class DefaultSkin
    {
        const int MonoBehaviourClass = 114;
        const string Name = "GameSkin";
        static GUISkin skin;

        public static GUISkin Get()
        {
            if (skin) return skin;
            skin = Load();
            if (!skin)
            {
                skin = ScriptableObject.CreateInstance<GUISkin>();
                skin.name = Name;
            }
            skin.hideFlags = HideFlags.HideAndDontSave;
            return skin;
        }

        static GUISkin Load()
        {
            OniMoono.Assets.Files.SerializedFile file;
            try
            {
                if (AssetSystem.DataDir == null) return null;
                file = AssetSystem.Get("unity default resources");
            }
            catch (IOException)
            {
                return null;
            }
            var header = TypeTreeDatabase.Get(MonoBehaviourClass).Tree;
            foreach (var info in file.Objects)
            {
                if (file.TypeOf(info).ClassId != MonoBehaviourClass) continue;
                var fields = TypeTreeReader.ReadFields(header, file.OpenObject(info));
                if (!fields.Contains("m_Name") || fields.String("m_Name") != Name) continue;
                if (ObjectLoader.Load(file, info.PathId) is GUISkin loaded) return loaded;
            }
            return null;
        }
    }
}
