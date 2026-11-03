using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Text;
using UnityEngine;

namespace OniMoono.Engine
{
    /// Resources.UnloadUnusedAssets. Unity unloads every object outside the loaded scenes that
    /// is not reachable from the scenes' objects, from managed code (statics, live objects) or
    /// from other reachable objects. The engine's object graph is ordinary managed references
    /// (a native record holds the managed objects it references), so reachability is the
    /// runtime's: the table holds the candidates weakly for one full collection, and whatever the
    /// collector reclaimed is unloaded. Engine caches (ObjectLoader, Shader.Find's table, font
    /// faces, device resources) hold objects weakly or by id, so they do not keep assets loaded.
    /// A later load of an unloaded asset creates a fresh object with the same instance id.
    internal static class AssetUnloader
    {
        public static int UnloadUnused()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var probed = WeakenCandidates();
            double weaken = watch.Elapsed.TotalMilliseconds;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            double collect = watch.Elapsed.TotalMilliseconds;
            int unloaded = ObjectTable.Strengthen(probed);
            ObjectLoader.Prune();
            GraphicsDevice.ReleaseOrphans();
            FontNative.ReleaseOrphanFaces();
            StartupProgress.Checkpoint("unload");
            EngineLog.Engine($"UnloadUnusedAssets: {unloaded} of {probed.Count} candidates unloaded in {watch.Elapsed.TotalMilliseconds:F0} ms (probe {weaken:F0}, collection {collect - weaken:F0})");
            PlayerLoop.LogVideoMemory("after UnloadUnusedAssets");
            return unloaded;
        }

        /// Separate frame, so no local of this method keeps a candidate alive during collection.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static List<ObjectTable.Probe> WeakenCandidates()
        {
            var candidates = new List<NativeObject>();
            string managers = ProjectSettings.Managers != null ? AssetSystem.KeyOf(ProjectSettings.Managers) : null;
            foreach (var native in ObjectTable.Snapshot())
                if (IsCandidate(native, managers)) candidates.Add(native);
            return ObjectTable.Weaken(candidates);
        }

        /// Objects outside the scenes: assets (prefab hierarchies included) and objects created
        /// at runtime that are not GameObjects or components. Excluded: DontUnloadUnusedAsset,
        /// the project managers, asset bundles (unloaded only by AssetBundle.Unload) and
        /// ScriptableObjects with OnDisable/OnDestroy (see ARCHITECTURE D9).
        static bool IsCandidate(NativeObject native, string managers)
        {
            if (native.Destroyed || native.DestroyRequested) return false;
            if ((native.HideFlags & HideFlags.DontUnloadUnusedAsset) != 0) return false;
            switch (native)
            {
                case GameObjectNative go:
                    return go.IsPersistent && go.Scene == null;
                case ComponentNative c:
                    return c.IsPersistent && c.GameObject is { Scene: null };
                case AssetBundleNative:
                    return false;
            }
            if (native.IsPersistent && native.Source.File == managers) return false;
            if (native.Managed is ScriptableObject so && HasUnloadCallbacks(so.GetType())) return false;
            return true;
        }

        static readonly ConcurrentDictionary<Type, bool> callbacks = new();

        static bool HasUnloadCallbacks(Type type) => callbacks.GetOrAdd(type, t =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var c = t; c != null && c != typeof(ScriptableObject); c = c.BaseType)
                if (c.GetMethod("OnDisable", flags, Type.EmptyTypes) != null || c.GetMethod("OnDestroy", flags, Type.EmptyTypes) != null) return true;
            return false;
        });
    }
}
