using System;
using System.IO;
using System.Linq;
using OniMoono.Engine.Assets;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    // A load that fails part way leaves nothing cached: the next load of the same object reads it
    // again, rather than returning the half-built object.
    public class ObjectLoaderTests
    {
        /// The TextAsset binder, failing once after creating its object.
        sealed class FailingOnce(Binder inner) : Binder
        {
            public bool Fail = true;
            public Object HalfBuilt;
            public override Object Create(LoadRequest request) => inner.Create(request);
            public override void Fill(Object obj, LoadRequest request)
            {
                if (Fail) { Fail = false; HalfBuilt = obj; throw new InvalidDataException("test: damaged object"); }
                inner.Fill(obj, request);
            }
        }

        [Fact]
        public void AFailedLoadIsNotCached()
        {
            TmpTestSupport.LoadGameData();
            var file = AssetSystem.Get("resources.assets");
            var info = file.ObjectsById.Values.First(i => file.TypeOf(i).ClassId == 49);
            var real = Binders.For(49);
            var failing = new FailingOnce(real);
            Binders.Register(49, failing);
            try
            {
                Assert.Throws<InvalidDataException>(() => ObjectLoader.Load(file, info.PathId));
                var loaded = ObjectLoader.Load(file, info.PathId);
                Assert.NotNull(loaded);
                Assert.NotSame(failing.HalfBuilt, loaded);
            }
            finally
            {
                Binders.Register(49, real);
            }
        }
    }
}

namespace OniMoono.Tests
{
    public class ResourceStreamTests
    {
        // Texture and video threads read resource files concurrently; a file is mapped once.
        [Fact]
        public void ConcurrentFirstReadsMapAFileOnce()
        {
            TmpTestSupport.LoadGameData();
            var name = Directory.GetFiles(GameInstall.DataDir, "*.resS")
                .Select(Path.GetFileName).OrderBy(n => n).Last();
            int before = AssetSystem.MappingsMade;
            using var start = new System.Threading.Barrier(32);
            var threads = Enumerable.Range(0, 32).Select(_ => new System.Threading.Thread(() =>
            {
                start.SignalAndWait();
                AssetSystem.ReadStream(name, 0, 16);
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            Assert.Equal(before + 1, AssetSystem.MappingsMade);
        }

        // A range past the end of a resource file throws instead of reading unmapped memory
        // (the test host aborted with an access violation when a test switched game builds and
        // U59's resources.assets.resS was read at the U60 vista texture's offset).
        [Fact]
        public void ARangePastTheEndThrows()
        {
            TmpTestSupport.LoadGameData();
            var path = Path.Combine(GameInstall.DataDir, "resources.assets.resS");
            long length = new FileInfo(path).Length;
            Assert.Equal(16, AssetSystem.ReadStream("resources.assets.resS", length - 16, 16).Length);
            Assert.Throws<InvalidDataException>(() => AssetSystem.ReadStream("resources.assets.resS", length - 8, 16));
        }

        // Once its files have been read, the data directory stays: files are kept by name, and
        // one build's .assets read with another's .resS would read texels from the wrong places.
        [Fact]
        public void TheDataDirectoryCannotChangeOnceRead()
        {
            TmpTestSupport.LoadGameData();
            AssetSystem.Get("resources.assets");
            var dir = GameInstall.DataDir;
            Assert.Throws<InvalidOperationException>(() => AssetSystem.Initialize(Path.Combine(GameInstall.Dir, "elsewhere")));
            Assert.Equal(dir, AssetSystem.DataDir);
            AssetSystem.Initialize(dir + Path.DirectorySeparatorChar);
            AssetSystem.Initialize(dir);
        }
    }
}

namespace OniMoono.Tests
{
    public class SceneReloadTests
    {
        // A scene's objects are made afresh each time the scene loads: an object that outlived the
        // previous load (DontDestroyOnLoad, scene settings) is not taken into the new one.
        [Fact]
        public void ReloadingASceneFileMakesNewObjects()
        {
            TmpTestSupport.LoadGameData();
            var file = AssetSystem.Get("level0");
            var info = file.ObjectsById.Values.First(i => file.TypeOf(i).ClassId == 104); // RenderSettings
            var scene = OniMoono.Engine.Scenes.Create("Reload", "Assets/Reload.unity", 0);
            ObjectLoader.BeginSceneFile(file, scene);
            var first = ObjectLoader.Load(file, info.PathId);
            ObjectLoader.EndSceneFile(file);
            ObjectLoader.BeginSceneFile(file, scene);
            var second = ObjectLoader.Load(file, info.PathId);
            ObjectLoader.EndSceneFile(file);
            Assert.NotNull(first);
            Assert.NotSame(first, second);
        }
    }
}
