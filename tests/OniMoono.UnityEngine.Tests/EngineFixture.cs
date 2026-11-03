using OniMoono.Engine;
using UnityEngine;
using Xunit;


namespace OniMoono.Tests
{
    /// Gives each test a fresh active scene and a running player loop.
    public abstract class EngineTest : System.IDisposable
    {
        static bool initialized;
        internal readonly SceneRecord Scene;

        protected EngineTest()
        {
            if (!initialized)
            {
                PlayerLoop.Initialize();
                Layers.Install(["Default", "TransparentFX", "Ignore Raycast", "", "Water", "UI"]);
                initialized = true;
            }
            // The player loop's thread is the test's: xunit runs tests on pool threads and can
            // move to another one partway through a run, and a main thread kept from the first
            // test would fail every later one on the new thread.
            Threads.SetMainThread();
            Scene = Scenes.Create("Test", "Assets/Test.unity", 0);
            Scenes.SetActive(Scene);
        }

        protected static void Frame(float dt = 1f / 60f) => PlayerLoop.RunFrame(dt);

        public void Dispose()
        {
            foreach (var root in Scene.Roots.ToArray()) Object.DestroyImmediate((GameObject)root.Managed);
            Lifecycle.ProcessDestroyQueue();
            Scenes.Remove(Scene);
        }
    }
}
