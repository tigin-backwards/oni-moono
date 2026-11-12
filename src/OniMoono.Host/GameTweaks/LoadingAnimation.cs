#nullable enable
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `loading-animation`: the loading screen keeps moving while a save loads. The
    /// whole load (the scene, SaveLoader, Game.OnSpawn, every object's OnSpawn) runs inside one
    /// engine frame, 18–33 s on a mid- to late-game colony, during which no frame is presented
    /// (docs/research/save-load-and-loading-screen.md). The presenter thread then shows the last
    /// frame, the loading screen, again 30 times a second, with a small pulsing bar of its
    /// own low on the screen (LoadingScreen, GlLoadAnimation); the first frame after the
    /// loading screen's ends it, and nothing is drawn over a game frame.
    ///
    /// - The game's LoadingOverlay.Load (after mods' postfixes) and Clear tell the engine when
    ///   the overlay is up; a frame shown with it up that no frame follows for 250 ms starts the
    ///   animation until the next frame comes.
    /// - A scene load that starts while the overlay is up (SceneLoader.LoadStarting, before the
    ///   old scenes go) arms it for the whole load.
    /// - A kanim the overlay plays (a mod's: Pip Licks Butt's Pip) moves on: at that moment its
    ///   anim's frames are rendered once each, the overlay canvases drawn as the last frame drew
    ///   them with only the frame number in the kanim's batch instance data changed, and the
    ///   original bytes put back (LoadingScreen.RenderKanim). The controller is not touched: its
    ///   frame, time, flags and events are as they were. The presenter plays them on from the
    ///   frame shown, at the anim's frame rate, looping a loop (a Loop play mode, or an anim
    ///   named *_loop, Klei's naming) and holding the last frame of anything else. The
    ///   rendering is bounded (LoadingScreen.BudgetMs, BudgetBytes) and logged.
    ///
    /// - A progress bar (LoadProgress): the load's phases marked as it reaches them, SaveManager
    ///   .Load's progress read from its reader's position (by the presenter, not in the loop),
    ///   and the Start pass counted; each phase weighted by what it took the last time the save
    ///   was loaded (LoadTimes, `oni-moono/load-times.json`). When a mod transpiles
    ///   SaveManager.Load, its part moves by time instead.
    ///
    /// It changes nothing the game or a mod can observe, but the batch texture's updateCount
    /// (each Apply counts) and when GC pauses fall.
    [GameMember(GameUse.Postfix, "System.Void LoadingOverlay::Load(System.Action)")]
    [GameMember(GameUse.Postfix, "System.Void LoadingOverlay::Clear()")]
    [GameMember(GameUse.Uses, "LoadingOverlay LoadingOverlay::instance")]
    [GameMember(GameUse.Uses, "KBatchedAnimController")]
    [GameMember(GameUse.Uses, "KAnimBatch KBatchedAnimController::batch")]
    [GameMember(GameUse.Uses, "KAnim/Anim KAnimControllerBase::curAnim")]
    [GameMember(GameUse.Uses, "System.Int32 KAnimControllerBase::curAnimFrameIdx")]
    [GameMember(GameUse.Uses, "KAnim/PlayMode KAnimControllerBase::mode")]
    [GameMember(GameUse.Uses, "KAnimBatchGroup/MaterialType KAnimControllerBase::materialType")]
    [GameMember(GameUse.Uses, "KAnimBatchGroup/KAnimBatchTextureCache/Entry KAnimBatch::get_dataTex()")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<KAnimConverter/IAnimConverter,System.Int32> KAnimBatch::controllersToIdx")]
    [GameMember(GameUse.Uses, "UnityEngine.Texture2D KAnimBatchGroup/KAnimBatchTextureCache/Entry::get_texture()")]
    [GameMember(GameUse.Uses, "System.Int32 KAnim/Anim::firstFrameIdx")]
    [GameMember(GameUse.Uses, "System.Int32 KAnim/Anim::numFrames")]
    [GameMember(GameUse.Uses, "System.Single KAnim/Anim::frameRate")]
    [GameMember(GameUse.Uses, "System.String KAnim/Anim::name")]
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Boolean SaveManager::Load(IReader)")]
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Byte[] SaveLoader::DecompressContents(System.Byte[])")]
    [GameMember(GameUse.Uses, "System.Int32 FastReader::idx")]
    [GameMember(GameUse.Uses, "System.Byte[] FastReader::bytes")]
    [GameMember(GameUse.Uses, "System.String SaveLoader::GetActiveSaveFilePath()")]
    internal static class LoadingAnimation
    {
        public const string Name = GameTweakNames.LoadingAnimation;

        /// Bytes of a kanim's instance record in its batch's data texture
        /// (KBatchedAnimInstanceData.SIZE_IN_BYTES); its first float is the frame number.
        const int InstanceBytes = 112;

        /// For specs (`type:OniMoono.Host.GameTweaks.LoadingAnimation::Armed`): scene loads armed
        /// under the overlay; kanims replayed; and from the presenter, through LoadingScreen,
        /// the loads it animated and the last one's redraws.
        public static int Armed, Replayed;
        public static int Animated => LoadingScreen.Loads;
        public static int LastRedraws => LoadingScreen.LastRedraws;
        public static double LastSeconds => LoadingScreen.LastSeconds;
        public static double LastLongestGapMs => LoadingScreen.LastLongestGapMs;
        public static double LastRenderMs => LoadingScreen.LastRenderMs;
        public static int LastReplayDiffer => LoadingScreen.LastReplayDiffer;
        public static double LastBar => LoadingScreen.LastBar;

        static LoadTimes? times;
        static string? loadKey;
        static MethodInfo? activeSave, saveManagerLoad;
        static Type? fastReader;
        static AccessTools.FieldRef<object, int>? readerIndex;
        static AccessTools.FieldRef<object, byte[]>? readerBytes;
        static string? harmonyId;

        static FieldInfo? instance, batch, curAnim, curFrame, mode, materialType, controllersToIdx, firstFrame, numFrames, frameRate, animName;
        static PropertyInfo? dataTex, texture;
        static Type? controllerType;

        public static void Install(Harmony harmony)
        {
            var overlay = AccessTools.TypeByName("LoadingOverlay") ?? throw new TypeLoadException("LoadingOverlay is not in the game");
            instance = AccessTools.Field(overlay, "instance") ?? throw new MissingFieldException("LoadingOverlay", "instance");
            controllerType = AccessTools.TypeByName("KBatchedAnimController");
            var controllerBase = AccessTools.TypeByName("KAnimControllerBase");
            var animBatch = AccessTools.TypeByName("KAnimBatch");
            var anim = AccessTools.TypeByName("KAnim+Anim");
            var entry = AccessTools.TypeByName("KAnimBatchGroup+KAnimBatchTextureCache+Entry");
            if (controllerType != null && controllerBase != null && animBatch != null && anim != null && entry != null)
            {
                batch = AccessTools.Field(controllerType, "batch");
                curAnim = AccessTools.Field(controllerBase, "curAnim");
                curFrame = AccessTools.Field(controllerBase, "curAnimFrameIdx");
                mode = AccessTools.Field(controllerBase, "mode");
                materialType = AccessTools.Field(controllerBase, "materialType");
                dataTex = AccessTools.Property(animBatch, "dataTex");
                controllersToIdx = AccessTools.Field(animBatch, "controllersToIdx");
                texture = AccessTools.Property(entry, "texture");
                firstFrame = AccessTools.Field(anim, "firstFrameIdx");
                numFrames = AccessTools.Field(anim, "numFrames");
                frameRate = AccessTools.Field(anim, "frameRate");
                animName = AccessTools.Field(anim, "name");
            }
            if (batch == null || curAnim == null || curFrame == null || mode == null || materialType == null || dataTex == null || controllersToIdx == null
                || texture == null || firstFrame == null || numFrames == null || frameRate == null || animName == null)
            {
                controllerType = null;
                EngineLog.Engine($"{Name}: the game's kanim members are not as expected; a kanim on the loading screen is not replayed");
            }
            harmony.Patch(AccessTools.Method(overlay, "Load") ?? throw new MissingMethodException("LoadingOverlay", "Load"),
                postfix: new HarmonyMethod(typeof(LoadingAnimation), nameof(Shown)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(overlay, "Clear") ?? throw new MissingMethodException("LoadingOverlay", "Clear"),
                postfix: new HarmonyMethod(typeof(LoadingAnimation), nameof(Cleared)) { priority = Priority.Last });
            SceneLoader.LoadStarting += Starting;
            harmonyId = harmony.Id;
            try
            {
                times = new LoadTimes(System.IO.Path.Combine(Application.persistentDataPath, "oni-moono", "load-times.json"));
                LoadProgress.Measured = Measured;
                activeSave = AccessTools.Method("SaveLoader:GetActiveSaveFilePath");
                fastReader = AccessTools.TypeByName("FastReader");
                if (fastReader != null && AccessTools.Field(fastReader, "idx") != null && AccessTools.Field(fastReader, "bytes") != null)
                {
                    readerIndex = AccessTools.FieldRefAccess<int>(fastReader, "idx");
                    readerBytes = AccessTools.FieldRefAccess<byte[]>(fastReader, "bytes");
                }
                // Phase marks only: a missing one leaves that phase to time.
                if (AccessTools.Method("SaveLoader:DecompressContents") is { } decompress)
                    harmony.Patch(decompress, prefix: new HarmonyMethod(typeof(LoadingAnimation), nameof(Decompressing)) { priority = int.MaxValue },
                        postfix: new HarmonyMethod(typeof(LoadingAnimation), nameof(Decompressed)) { priority = int.MinValue });
                saveManagerLoad = AccessTools.Method("SaveManager:Load", [AccessTools.TypeByName("IReader")]);
                if (saveManagerLoad != null)
                    harmony.Patch(saveManagerLoad, prefix: new HarmonyMethod(typeof(LoadingAnimation), nameof(ObjectsLoading)) { priority = Priority.Last },
                        postfix: new HarmonyMethod(typeof(LoadingAnimation), nameof(ObjectsLoaded)) { priority = Priority.First });
            }
            catch (Exception e)
            {
                EngineLog.Engine($"{Name}: the bar moves by time alone: {e.GetType().Name}: {e.Message}");
            }
            LoadingScreen.Enabled = true;
        }

        static void Shown() => LoadingScreen.OverlayShown();

        static void Cleared() => LoadingScreen.OverlayCleared();

        static MonoBehaviour? Overlay() => instance!.GetValue(null) as MonoBehaviour;

        /// A scene load starts (main thread): under the overlay, the animation is armed for it,
        /// and a kanim the overlay plays is rendered ahead.
        static void Starting(int index)
        {
            var overlay = Overlay();
            if (overlay == null || !overlay.isActiveAndEnabled)
            {
                LoadingScreen.OverlayCleared();
                return;
            }
            LoadingScreen.Arm();
            Armed++;
            BeginProgress();
            try
            {
                if (RenderKanim(overlay)) Replayed++;
            }
            catch (Exception e)
            {
                EngineLog.Engine($"{Name}: the loading screen's kanim is not replayed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// The bar's phases start, weighted by this save's last load when one was kept.
        static void BeginProgress()
        {
            double[]? seconds = null;
            loadKey = null;
            try
            {
                if (activeSave?.Invoke(null, null) is string path && path.Length > 0)
                {
                    var file = new System.IO.FileInfo(path);
                    if (file.Exists)
                    {
                        loadKey = LoadTimes.Key(path, file.Length);
                        seconds = times?.Find(loadKey);
                    }
                }
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
            ProfileKept = seconds != null;
            LoadProgress.Begin(seconds);
        }

        /// For specs: the last load's bar was weighted by a kept profile of the same save.
        public static bool ProfileKept;

        /// The save's body is decompressed (main thread; ahead of every other prefix, whether or
        /// not one of them skips the game's method), then is: phase marks.
        static void Decompressing()
        {
            if (LoadProgress.Phase >= 0 && Threads.IsMainThread) LoadProgress.Mark(LoadProgress.Decompression);
        }

        static void Decompressed()
        {
            if (LoadProgress.Phase >= 0 && Threads.IsMainThread) LoadProgress.Mark(LoadProgress.WorldSetup);
        }

        /// SaveManager.Load starts (main thread): its part of the bar follows its reader through
        /// the save's body, unless another mod transpiles it (its loop may read otherwise).
        static void ObjectsLoading(object reader)
        {
            if (LoadProgress.Phase < 0) return;
            LoadProgress.Mark(LoadProgress.Objects);
            if (reader == null || reader.GetType() != fastReader || readerIndex == null || readerBytes == null) return;
            var info = Harmony.GetPatchInfo(saveManagerLoad);
            if (info != null && info.Transpilers.Any(t => t.owner != harmonyId)) return;
            int start = readerIndex(reader), length = readerBytes(reader)?.Length ?? 0;
            if (length <= start) return;
            var index = readerIndex;
            LoadProgress.ObjectsFraction = () => (index(reader) - start) / (double)(length - start);
        }

        static void ObjectsLoaded()
        {
            LoadProgress.ObjectsFraction = null;
            LoadProgress.Mark(LoadProgress.AfterObjects);
        }

        /// A load ended: its phases' seconds are kept for the next load of the save (written on
        /// a worker).
        static void Measured(double[] seconds)
        {
            EngineLog.Engine($"loading animation: the load's phases took {string.Join(", ", Array.ConvertAll(seconds, s => s.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))} s");
            if (times == null || loadKey == null) return;
            var text = times.Record(loadKey, seconds);
            var t = times;
            System.Threading.Tasks.Task.Run(() => t.Write(text));
        }

        static bool RenderKanim(MonoBehaviour overlay)
        {
            if (controllerType == null) return false;
            Component? found = null;
            int playing = 0;
            foreach (var c in overlay.GetComponentsInChildren(controllerType, false))
            {
                if (c is not Behaviour b || !b.isActiveAndEnabled) continue;
                if (materialType!.GetValue(c)?.ToString() != "UI" || curAnim!.GetValue(c) == null || batch!.GetValue(c) == null) continue;
                playing++;
                found ??= c;
            }
            if (found == null) return false;
            var a = curAnim!.GetValue(found)!;
            int first = (int)firstFrame!.GetValue(a)!, count = (int)numFrames!.GetValue(a)!, frame = (int)curFrame!.GetValue(found)!;
            float rate = (float)frameRate!.GetValue(a)!;
            string name = (string)animName!.GetValue(a)! ?? "";
            bool loop = mode!.GetValue(found)?.ToString() == "Loop" || name.EndsWith("_loop", StringComparison.OrdinalIgnoreCase);
            var b2 = batch!.GetValue(found)!;
            var entry = dataTex!.GetValue(b2);
            var data = entry == null ? null : texture!.GetValue(entry) as Texture2D;
            if (data == null || controllersToIdx!.GetValue(b2) is not IDictionary slots || !slots.Contains(found)) return false;
            int offset = (int)slots[found]! * InstanceBytes;
            string what = $"{found.gameObject.name}'s {name}{(playing > 1 ? $" (the first of {playing} kanims playing)" : "")}";
            if (count < 2) return false;
            // The batch's data must hold the frame the controller is at, as the game last wrote it.
            var raw = data.GetRawTextureData<float>();
            if (offset / 4 >= raw.Length || raw[offset / 4] != frame)
            {
                EngineLog.Engine($"{Name}: {what}: its batch data does not hold its frame {frame}; not replayed");
                return false;
            }
            // Where it draws: its rect, half as large again on every side (a kanim overflows its
            // rect), in screen pixels (an overlay canvas's world space).
            var rect = found.transform as RectTransform;
            if (rect == null) return false;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            float x0 = Math.Min(corners[0].x, corners[2].x), x1 = Math.Max(corners[0].x, corners[2].x);
            float y0 = Math.Min(corners[0].y, corners[2].y), y1 = Math.Max(corners[0].y, corners[2].y);
            float mx = (x1 - x0) / 2, my = (y1 - y0) / 2;
            int ax0 = (int)Math.Floor(x0 - mx), ay0 = (int)Math.Floor(y0 - my), ax1 = (int)Math.Ceiling(x1 + mx), ay1 = (int)Math.Ceiling(y1 + my);
            return LoadingScreen.RenderKanim(data, offset, first, count, frame - first, rate, loop, new RectInt(ax0, ay0, ax1 - ax0, ay1 - ay0), what);
        }
    }
}
