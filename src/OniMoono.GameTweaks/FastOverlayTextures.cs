using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-overlay-textures`: the render textures the coloured overlays
    /// drop are freed, instead of staying allocated until the next load.
    ///
    /// The main camera renders into screen-sized targets (MultipleRenderTargetProxy.Textures),
    /// and the liquid pass reads copies of two of them (TexturesCopies, blitted into by the
    /// command buffer MRTLiquidInterception records on the base camera, and set as the shader
    /// globals MRT0_Copy and MRT1_Copy). Every CreateRenderTarget, which runs each time a
    /// coloured overlay (temperature, germs, ...) is opened or closed, its legend filter is
    /// changed, and on a resize, keeps the targets when their size is right but makes new copies
    /// (two, and a third for the overlay buffer while one is shown) and drops the old ones
    /// without destroying them. As in Unity, a dropped texture stays allocated until
    /// Resources.UnloadUnusedAssets, which the game calls only when a colony is loaded or quit:
    /// opening and closing the temperature overlay once (two to four CreateRenderTargets)
    /// leaves eight textures behind, four of them allocated on the device, about
    /// 18 MB of video memory at 1024×768 (U57: 100 openings, 45 → 846 live render textures and
    /// about 1.8 GB; the germ overlay the same); at 1920×1080 that is about 50 MB an opening.
    ///
    /// The tweak notes the copies before the game's CreateRenderTarget and, after it, takes those
    /// the method replaced and that are neither among the proxy's textures nor its new copies.
    /// They are released and destroyed two frames later, as the game's RenderTextureDestroyer
    /// does for the targets it replaces itself: by then the command buffer, recorded again by
    /// CreateRenderTarget with the new copies, has run in a frame's rendering and set the shader
    /// globals to them. A texture still found then among the proxy's textures, as a shader global
    /// under its name or the copies' names, as a camera's target or as the active render texture
    /// is kept (the game's own behaviour). While another Harmony owner patches CreateRenderTarget
    /// (a mod freeing these itself), the tweak frees nothing.
    ///
    /// With ONI_TWEAK_VERIFY=1, before each release every material's textures, every component's
    /// fields holding textures (or arrays or lists of them) and the checks above are searched for
    /// the texture: a reference found is counted wrong, and the texture is kept.
    public static class FastOverlayTextures
    {
        public const string Name = "fast-overlay-textures";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly string[] CopyGlobals = { "MRT0_Copy", "MRT1_Copy", "MRT2_Copy", "_MRT0", "_MRT1", "_MRT2" };

        static MethodBase createRenderTarget;

        struct Dropped
        {
            public RenderTexture Texture;
            public MultipleRenderTargetProxy Proxy;
            public string Global;
            public int Frame;
        }

        static readonly List<Dropped> pending = new List<Dropped>();
        static long released, kept, verified, wrong;

        public static void Install(Harmony harmony)
        {
            createRenderTarget = AccessTools.Method(typeof(MultipleRenderTargetProxy), "CreateRenderTarget");
            harmony.Patch(createRenderTarget, prefix: new HarmonyMethod(typeof(FastOverlayTextures), nameof(Before)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(FastOverlayTextures), nameof(After)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(MultipleRenderTargetProxy), "Update"), postfix: new HarmonyMethod(typeof(FastOverlayTextures), nameof(Update)));
        }

        /// The method another owner's patch makes this tweak stand down (ModOverlaps).
        internal static MethodBase Guarded() => createRenderTarget;

        static bool Foreign()
        {
            var info = Harmony.GetPatchInfo(createRenderTarget);
            if (info == null) return false;
            foreach (var owner in info.Owners)
                if (!ModOverlapsCore.IsTweakOwner(owner)) return true;
            return false;
        }

        static void Before(MultipleRenderTargetProxy __instance, out RenderTexture[] __state)
            => __state = (RenderTexture[])__instance.TexturesCopies.Clone();

        static void After(MultipleRenderTargetProxy __instance, RenderTexture[] __state)
        {
            if (__state == null || Foreign()) return;
            foreach (var old in __state)
            {
                if (old == null || Held(__instance, old)) continue;
                bool queued = false;
                foreach (var d in pending) queued |= ReferenceEquals(d.Texture, old);
                if (!queued) pending.Add(new Dropped { Texture = old, Proxy = __instance, Global = old.name, Frame = Time.frameCount });
            }
        }

        static bool Held(MultipleRenderTargetProxy proxy, RenderTexture t)
            => Array.IndexOf(proxy.TexturesCopies, t) >= 0 || Array.IndexOf(proxy.Textures, t) >= 0;

        static void Update()
        {
            if (pending.Count == 0) return;
            int now = Time.frameCount;
            List<Dropped> due = null;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                // Two frames: the recorded command buffer has run in a frame's rendering since.
                if (now - pending[i].Frame < 2) continue;
                (due ??= new List<Dropped>()).Add(pending[i]);
                pending.RemoveAt(i);
            }
            if (due == null) return;
            if (Foreign()) return;
            HashSet<RenderTexture> found = verify ? FindReferenced(due) : null;
            foreach (var d in due)
            {
                var t = d.Texture;
                if (t == null) continue; // destroyed by someone else
                if (verify) verified++;
                if (Referenced(d) || (found != null && found.Contains(t)))
                {
                    kept++;
                    if (verify) wrong++;
                    continue;
                }
                t.Release();
                UnityEngine.Object.Destroy(t);
                released++;
            }
            if (verify) global::Debug.Log($"{Name}: {verified} released textures checked, {wrong} wrong");
        }

        /// The cheap checks, made for every release.
        static bool Referenced(Dropped d)
        {
            var t = d.Texture;
            if (d.Proxy != null && Held(d.Proxy, t)) return true;
            if (ReferenceEquals(RenderTexture.active, t)) return true;
            if (!string.IsNullOrEmpty(d.Global) && ReferenceEquals(Shader.GetGlobalTexture(d.Global), t)) return true;
            foreach (var name in CopyGlobals)
                if (ReferenceEquals(Shader.GetGlobalTexture(name), t)) return true;
            foreach (var camera in Camera.allCameras)
                if (ReferenceEquals(camera.targetTexture, t)) return true;
            return false;
        }

        // ---- Verify: every material and component field ----

        static readonly Dictionary<Type, FieldInfo[]> textureFields = new Dictionary<Type, FieldInfo[]>();

        static FieldInfo[] TextureFields(Type type)
        {
            if (textureFields.TryGetValue(type, out var fields)) return fields;
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var ft = f.FieldType;
                    if (typeof(Texture).IsAssignableFrom(ft) || (ft.IsArray && typeof(Texture).IsAssignableFrom(ft.GetElementType()))
                        || (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>) && typeof(Texture).IsAssignableFrom(ft.GetGenericArguments()[0])))
                        list.Add(f);
                }
            return textureFields[type] = list.ToArray();
        }

        static HashSet<RenderTexture> FindReferenced(List<Dropped> due)
        {
            var wanted = new HashSet<Texture>();
            foreach (var d in due) wanted.Add(d.Texture);
            var found = new HashSet<RenderTexture>();
            void Look(object value)
            {
                switch (value)
                {
                    case Texture t when wanted.Contains(t): found.Add((RenderTexture)t); break;
                    case System.Collections.IEnumerable items when !(value is Texture):
                        foreach (var item in items)
                            if (item is Texture i && wanted.Contains(i)) found.Add((RenderTexture)i);
                        break;
                }
            }
            var ids = new List<int>();
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                ids.Clear();
                material.GetTexturePropertyNameIDs(ids);
                foreach (var id in ids) Look(material.GetTexture(id));
            }
            foreach (var component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (component == null) continue;
                foreach (var f in TextureFields(component.GetType())) Look(f.GetValue(component));
            }
            return found;
        }
    }
}
