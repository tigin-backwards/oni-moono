// When textures and render textures hold device memory ("PROBE ... VR ..."), at the main menu
// (10 s after the frontend scene starts). Each phase logs the wall clock (UTC), what the player
// reports (IsCreated, mipmapCount, whether the object is gone) and then holds for HoldSeconds,
// so that the process's video memory, sampled from outside (Windows' "GPU Process Memory"
// counters), can be read for each phase:
//   rt-new: 8 render textures of 4096x4096 ARGB32 with 24-bit depth, constructed;
//   rt-create: Create() on each; rt-release: Release(); rt-blit: a Blit into each (first use);
//   rt-release-used, rt-reuse (a Blit again), rt-destroy (Object.Destroy);
//   rt-active: 4 fresh ones each made RenderTexture.active and back;
//   rt-copy: new RenderTexture(rt) of a used one, never used itself;
//   temp: GetTemporary + Blit + ReleaseTemporary of 4, then the frames until each is destroyed;
//   tex-apply: 8 Texture2D 4096x4096 RGBA32 without mips, Apply(false, true); tex-destroy;
//   tex-unload: 8 more, dropped unreferenced, then Resources.UnloadUnusedAssets;
//   mips: mipmapCount of textures and render textures asked for fewer levels than the full
//   chain (and their memory: 8 render textures with mipCount 2, then with the full chain).
// With PROBE_SCENARIO=vram only this probe runs; a VramProbeDone object appears when it is done.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class VramProbe : MonoBehaviour
    {
        const int Size = 4096, Count = 8;
        const float HoldSeconds = 6f;
        bool started;
        float sceneStart = -1f;

        static void L(string s) => Rec.Log("VR " + System.DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + s);

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        static string Created(IList<RenderTexture> rts)
        {
            int n = 0;
            foreach (var rt in rts) if (rt != null && rt.IsCreated()) n++;
            return n + "/" + rts.Count;
        }

        static IEnumerator Hold(string phase, string detail)
        {
            L("phase " + phase + " " + detail + " " + Counters());
            yield return new WaitForSecondsRealtime(HoldSeconds);
            L("end " + phase + " " + Counters());
        }

        /// The player's own texture memory counters and texture quality settings, where it has
        /// them (a player without them answers "none").
        static string Counters()
        {
            try { return UnityCounters(); }
            catch (Exception e) { return "counters none (" + e.GetType().Name + ")"; }
        }

        static string UnityCounters()
            => "counters textures " + (Texture.currentTextureMemory >> 20) + " MiB current, " + (Texture.totalTextureMemory >> 20) + " total, "
               + (Texture.nonStreamingTextureMemory >> 20) + " non-streaming (" + Texture.nonStreamingTextureCount + "), streaming " + QualitySettings.streamingMipmapsActive
               + ", mipmap limit " + QualitySettings.globalTextureMipmapLimit + ", graphics driver " + (UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() >> 20) + " MiB";

        static List<RenderTexture> MakeTargets(int count, int depth)
        {
            var list = new List<RenderTexture>();
            for (int i = 0; i < count; i++) list.Add(new RenderTexture(Size, Size, depth, RenderTextureFormat.ARGB32) { name = "VramProbe" + i });
            return list;
        }

        IEnumerator Run()
        {
            yield return Hold("baseline", "");

            var rts = MakeTargets(Count, 24);
            yield return Hold("rt-new", "created " + Created(rts));
            foreach (var rt in rts) rt.Create();
            yield return Hold("rt-create", "created " + Created(rts));
            foreach (var rt in rts) rt.Release();
            yield return Hold("rt-release", "created " + Created(rts));
            foreach (var rt in rts) Graphics.Blit(Texture2D.whiteTexture, rt);
            yield return Hold("rt-blit", "created " + Created(rts));
            foreach (var rt in rts) rt.Release();
            yield return Hold("rt-release-used", "created " + Created(rts));
            foreach (var rt in rts) Graphics.Blit(Texture2D.whiteTexture, rt);
            yield return Hold("rt-reuse", "created " + Created(rts));
            var copy = new RenderTexture(rts[0]) { name = "VramProbeCopy" };
            L("copy created " + copy.IsCreated() + " mipmapCount " + copy.mipmapCount);
            foreach (var rt in rts) Destroy(rt);
            yield return null;
            L("destroyed: gone " + rts.FindAll(r => r == null).Count + "/" + rts.Count + " copy created " + copy.IsCreated());
            yield return Hold("rt-destroy", "");

            var actives = MakeTargets(4, 0);
            foreach (var rt in actives)
            {
                var before = RenderTexture.active;
                RenderTexture.active = rt;
                RenderTexture.active = before;
            }
            yield return Hold("rt-active", "created " + Created(actives));
            foreach (var rt in actives) Destroy(rt);
            yield return null;
            var copies = new List<RenderTexture>();
            var model = MakeTargets(1, 24)[0];
            Graphics.Blit(Texture2D.whiteTexture, model);
            for (int i = 0; i < Count; i++) copies.Add(new RenderTexture(model));
            yield return Hold("rt-copy", "created " + Created(copies));
            foreach (var rt in copies) Destroy(rt);
            Destroy(model);
            Destroy(copy);
            yield return null;
            yield return Hold("rt-copy-destroy", "");

            var temps = new List<RenderTexture>();
            for (int i = 0; i < 4; i++)
            {
                var t = RenderTexture.GetTemporary(Size, Size, 0);
                Graphics.Blit(Texture2D.whiteTexture, t);
                temps.Add(t);
            }
            foreach (var t in temps) RenderTexture.ReleaseTemporary(t);
            int releasedAt = Time.frameCount;
            L("temp released, created " + Created(temps));
            float tempStart = Time.realtimeSinceStartup;
            while (temps.Exists(t => t != null) && Time.realtimeSinceStartup - tempStart < 30f)
            {
                yield return null;
            }
            L("temp gone after " + (Time.frameCount - releasedAt) + " frames (" + (Time.realtimeSinceStartup - tempStart).ToString("F2", CultureInfo.InvariantCulture) + " s), gone " + temps.FindAll(t => t == null).Count + "/4");
            yield return Hold("temp-gone", "");

            var textures = new List<Texture2D>();
            for (int i = 0; i < Count; i++)
            {
                var t = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                t.Apply(false, true);
                textures.Add(t);
            }
            yield return Hold("tex-apply", "mipmapCount " + textures[0].mipmapCount + " readable " + textures[0].isReadable);
            foreach (var t in textures) Destroy(t);
            textures.Clear();
            yield return null;
            yield return Hold("tex-destroy", "");

            MakeUnreferenced();
            yield return Hold("tex-unref", "");
            var op = Resources.UnloadUnusedAssets();
            while (!op.isDone) yield return null;
            yield return Hold("tex-unload", "");

            var t2 = new Texture2D(1024, 1024, TextureFormat.RGBA32, true);
            var t3 = new Texture2D(1024, 1024, TextureFormat.RGBA32, 3, false);
            L("mips texture full " + t2.mipmapCount + " asked 3 " + t3.mipmapCount);
            var d = new RenderTextureDescriptor(1024, 1024, RenderTextureFormat.ARGB32, 0) { useMipMap = true, mipCount = 3, autoGenerateMips = false };
            var rtm = new RenderTexture(d);
            L("mips rt descriptor mipCount 3: mipmapCount " + rtm.mipmapCount + " descriptor " + rtm.descriptor.mipCount + " created " + rtm.IsCreated());
            rtm.Create();
            L("mips rt after Create: mipmapCount " + rtm.mipmapCount + " created " + rtm.IsCreated());
            var rtw = new RenderTexture(256, 256, 0) { useMipMap = true };
            rtw.width = 1024;
            rtw.height = 1024;
            L("mips rt useMipMap at 256 then 1024: mipmapCount " + rtw.mipmapCount + " descriptor " + rtw.descriptor.mipCount);
            rtw.Create();
            L("mips rt useMipMap at 256 then 1024 after Create: mipmapCount " + rtw.mipmapCount);
            Destroy(t2);
            Destroy(t3);
            Destroy(rtm);
            Destroy(rtw);

            var few = MipTargets(2);
            yield return Hold("mips-2", "mipmapCount " + few[0].mipmapCount + " created " + Created(few));
            foreach (var rt in few) Destroy(rt);
            yield return null;
            var full = MipTargets(-1);
            yield return Hold("mips-full", "mipmapCount " + full[0].mipmapCount + " created " + Created(full));
            foreach (var rt in full) Destroy(rt);
            yield return null;
            yield return Hold("end", "");
            new GameObject("VramProbeDone");
        }

        static List<RenderTexture> MipTargets(int mipCount)
        {
            var list = new List<RenderTexture>();
            for (int i = 0; i < Count; i++)
            {
                var d = new RenderTextureDescriptor(Size, Size, RenderTextureFormat.ARGB32, 0) { useMipMap = true, mipCount = mipCount, autoGenerateMips = false };
                var rt = new RenderTexture(d);
                rt.Create();
                list.Add(rt);
            }
            return list;
        }

        // Separate frame, so that no local keeps them.
        static void MakeUnreferenced()
        {
            for (int i = 0; i < Count; i++)
            {
                var t = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                t.Apply(false, true);
            }
        }
    }
}
