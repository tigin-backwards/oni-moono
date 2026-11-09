using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OniMoono.Engine.Assets;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    public class AssetUnloadTests : EngineTest
    {
        static string DataDir => GameInstall.DataDir;

        // sharedassets0.assets: the Droid Sans font, which nothing else in the tests' data keeps
        // loaded (Liberation Sans is held from TextMeshPro's settings: their default font asset's
        // dynamic fallback is generated from it).
        static Font LoadFont()
        {
            AssetSystem.Initialize(DataDir);
            return (Font)ObjectLoader.Load(AssetSystem.Get("sharedassets0.assets"), TmpTestSupport.FindId("sharedassets0.assets", "DroidSans", typeof(Font)));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (WeakReference Weak, int Id) LoadAndDrop()
        {
            var font = LoadFont();
            return (new WeakReference(font), font.GetInstanceID());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (WeakReference Font, WeakReference Material, WeakReference Texture) AttachMaterial(GameObject go)
        {
            var font = LoadFont();
            var material = font.material;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return (new WeakReference(font), new WeakReference(material), new WeakReference(material.mainTexture));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (WeakReference Plain, int PlainId, WeakReference Kept) CreateTextures()
        {
            var plain = new Texture2D(4, 4);
            var kept = new Texture2D(4, 4) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            return (new WeakReference(plain), plain.GetInstanceID(), new WeakReference(kept));
        }

        [Fact]
        public void UnreferencedAssetUnloadsAndReloadsWithItsInstanceId()
        {
            var (weak, id) = LoadAndDrop();
            Assert.True(id > 0);
            Resources.UnloadUnusedAssets();
            Assert.False(weak.IsAlive);
            var again = LoadFont();
            Assert.Equal(id, again.GetInstanceID());
            Assert.True(again.HasCharacter('A'));
        }

        [Fact]
        public void AssetsReachableFromSceneObjectsOrCodeStay()
        {
            var held = LoadFont();
            var go = new GameObject("holder");
            var (font, material, texture) = AttachMaterial(go);
            Resources.UnloadUnusedAssets();
            // The scene's renderer keeps its material, and the material its texture.
            var m = (Material)material.Target;
            Assert.True(m != null);
            Assert.True(m.mainTexture != null);
            Assert.Same(texture.Target, m.mainTexture);
            // The font is held by this method.
            Assert.Same(held, font.Target);
            Assert.True(held != null);
            GC.KeepAlive(held);
        }

        [Fact]
        public void RuntimeObjectsUnloadUnlessMarked()
        {
            var (plain, plainId, kept) = CreateTextures();
            Resources.UnloadUnusedAssets();
            Assert.False(plain.IsAlive);
            Assert.DoesNotContain(Resources.FindObjectsOfTypeAll<Texture2D>(), t => t.GetInstanceID() == plainId);
            var k = (Texture2D)kept.Target;
            Assert.True(k != null);
            Object.DestroyImmediate(k);
        }
    }
}
