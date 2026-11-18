using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Engine
{
    /// Diagnostics: what stays allocated. `ONI_OBJECT_CENSUS=N[:MARK]` logs, every N frames, the
    /// live render textures (how many, how many created on the device, an estimate of their
    /// bytes), textures, meshes, materials and sprites, each with its change since the mark (the
    /// first census at or after frame MARK; by default the first). `ONI_OBJECT_SITES=1` also
    /// records where each object of those types made at run time was created (the first few
    /// frames of game or mod code) and lists, with each census, the sites with most live objects
    /// made since the mark: for finding what piles up until Resources.UnloadUnusedAssets.
    internal static class ObjectCensus
    {
        static readonly string setting = Environment.GetEnvironmentVariable("ONI_OBJECT_CENSUS") ?? "";
        static readonly int every = int.TryParse(setting.Split(':')[0], out var n) ? n : 0;
        static readonly int markFrame = setting.Contains(':') && int.TryParse(setting.Split(':')[1], out var m) ? m : 0;
        public static readonly bool Sites = every > 0 && Environment.GetEnvironmentVariable("ONI_OBJECT_SITES") == "1";

        static readonly ConditionalWeakTable<NativeObject, Site> sites = new();
        static int frames, marked = -1;
        static Dictionary<string, int> baseline;

        sealed class Site
        {
            public string Where;
            public int Frame;
        }

        public static void Install()
        {
            if (every <= 0) return;
            PlayerLoop.FrameStart += () =>
            {
                if (++frames % every == 0) Take();
            };
        }

        static bool Tracked(Type t) => typeof(Texture).IsAssignableFrom(t) || t == typeof(Mesh) || typeof(Material).IsAssignableFrom(t) || t == typeof(Sprite);

        /// From Object.Bind: the creation site of a run-time object of a tracked type.
        public static void Created(NativeObject native)
        {
            if (native.IsPersistent || !native.Managed || !Tracked(native.Managed.GetType())) return;
            var trace = new System.Diagnostics.StackTrace(2, false);
            var parts = new List<string>(3);
            string engine = null;
            foreach (var frame in trace.GetFrames())
            {
                var method = frame.GetMethod();
                var type = method?.DeclaringType;
                if (type == null) continue;
                string ns = type.Namespace ?? "";
                if (ns.StartsWith("System")) continue;
                if (ns.StartsWith("UnityEngine") || ns.StartsWith("OniMoono.Engine") || ns.StartsWith("TMPro"))
                {
                    // The engine's or a package's own work, named when no game code is on the stack.
                    if (engine == null && method.Name != ".ctor" && type != typeof(UnityEngine.Object)) engine = type.Name + "." + method.Name;
                    continue;
                }
                parts.Add(type.Name + "." + method.Name);
                if (parts.Count == 3) break;
            }
            if (engine != null && (parts.Count == 0 || parts[0].StartsWith("Player."))) parts.Insert(0, engine);
            sites.AddOrUpdate(native, new Site { Where = $"{native.Managed.GetType().Name} {string.Join(" < ", parts)}", Frame = Time.frameCount });
        }

        static void Take()
        {
            var counts = new Dictionary<string, int>();
            int created = 0;
            long rtBytes = 0, texBytes = 0;
            var live = new Dictionary<string, int>();
            foreach (var native in ObjectTable.Snapshot())
            {
                if (native.Destroyed || !native.Managed) continue;
                var type = native.Managed.GetType();
                if (!Tracked(type)) continue;
                string key = native is RenderTextureNative ? "RenderTexture" : typeof(Texture).IsAssignableFrom(type) ? "Texture" : typeof(Material).IsAssignableFrom(type) ? "Material" : type.Name;
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
                if (native is RenderTextureNative rt)
                {
                    if (rt.Created)
                    {
                        created++;
                        rtBytes += (long)rt.Width * rt.Height * Math.Max(1, rt.VolumeDepth) * (TexelBytes(rt.Format) + (rt.DepthBits > 0 ? 4 : 0)) * Math.Max(1, rt.AntiAliasing);
                    }
                }
                else if (native is TexelTextureNative tt) texBytes += tt.SliceSize * tt.Depth;
                if (Sites && marked >= 0 && sites.TryGetValue(native, out var site) && site.Frame > marked)
                    live[site.Where] = live.TryGetValue(site.Where, out var l) ? l + 1 : 1;
            }
            if (baseline == null && Time.frameCount >= markFrame)
            {
                baseline = new Dictionary<string, int>(counts);
                marked = Time.frameCount;
            }
            string Delta(string key)
            {
                int now = counts.TryGetValue(key, out var v) ? v : 0;
                if (baseline == null) return now.ToString();
                int then = baseline.TryGetValue(key, out var b) ? b : 0;
                return $"{now} ({(now - then >= 0 ? "+" : "")}{now - then})";
            }
            EngineLog.Engine($"object census f{Time.frameCount}: render textures {Delta("RenderTexture")}, {created} created, about {rtBytes >> 20} MB; textures {Delta("Texture")}, {texBytes >> 20} MB of texels; meshes {Delta("Mesh")}; materials {Delta("Material")}; sprites {Delta("Sprite")}");
            if (live.Count > 0)
                EngineLog.Engine($"object census sites since f{marked}: " + string.Join("; ", live.OrderByDescending(kv => kv.Value).Take(25).Select(kv => $"{kv.Value}× {kv.Key}")));
        }

        static int TexelBytes(RenderTextureFormat format) => format switch
        {
            RenderTextureFormat.ARGBHalf or RenderTextureFormat.RGFloat or RenderTextureFormat.ARGB64 => 8,
            RenderTextureFormat.ARGBFloat => 16,
            RenderTextureFormat.RHalf or RenderTextureFormat.R16 or RenderTextureFormat.RG16 => 2,
            RenderTextureFormat.R8 => 1,
            RenderTextureFormat.Depth or RenderTextureFormat.Shadowmap => 0,
            _ => 4,
        };
    }
}
