using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine
{
    /// One frame in Unity's order (ARCHITECTURE §3.3):
    ///
    ///   Initialization  time, pending scene load completion
    ///   EarlyUpdate     platform events, input, main-thread log callbacks
    ///   FixedUpdate     Start; 0..n fixed steps (FixedUpdate, WaitForFixedUpdate)
    ///   Update          Start; Update; yield null / WaitForSeconds / AsyncOperation
    ///   PreLateUpdate   Animators; LateUpdate
    ///   PostLateUpdate  destroy queue; canvases; particle systems; cameras render; OnGUI; WaitForEndOfFrame; destroy
    ///                   queue; present
    ///
    /// Subsystems attach to the phases through the hooks below, so this class stays the single
    /// definition of ordering.
    internal static class PlayerLoop
    {
        public static event Action FrameStart;          // pending scene loads complete here
        public static event Action EarlyUpdate;         // platform events, input state
        public static event Action DirectorUpdate;      // PlayableDirectors (Update.DirectorUpdate, after scripts and coroutines)
        public static event Action AnimationUpdate;     // Animators (PreLateUpdate.DirectorUpdateAnimationBegin/End, before LateUpdate)
        public static event Action FixedAnimationUpdate; // Fixed-mode Animators (FixedUpdate.DirectorFixedUpdate, after script FixedUpdate)
        public static event Action PostLateUpdate;      // e.g. canvas update (willRenderCanvases)
        public static event Action ParticleUpdate;      // ParticleSystems (PostLateUpdate.ParticleSystemEndUpdateAll, after LateUpdate, before rendering)
        public static event Action Render;              // cameras, overlay canvases
        public static event Action GUI;                 // OnGUI: the frame's input events, then Layout and Repaint (drawn on top)
        public static event Action Present;             // swap buffers

        public static bool HasFocus { get; set; } = true;
        public static int TargetFrameRate { get; set; } = -1;
        public static bool QuitRequested { get; private set; }
        public static int ExitCode { get; private set; }

        static bool started;

        /// Whether Application.wantsToQuit agreed to the quit requested.
        static bool quitAgreed;

        /// Forgets a quit that went ahead (tests, which go on running frames after one).
        internal static void ForgetQuit() => QuitRequested = quitAgreed = false;

        public static void RequestQuit(int exitCode)
        {
            if (QuitRequested) return;
            QuitRequested = true;
            ExitCode = exitCode;
        }

        public static void Initialize()
        {
            Threads.SetMainThread();
            started = true;
            ObjectCensus.Install();
        }

        /// Diagnostics (`--frame-stats`): average phase times logged every 60 frames.
        public static bool FrameStats;
        static readonly System.Diagnostics.Stopwatch statClock = new();
        static readonly double[] statTotals = new double[9]; // render, gui, end of frame, present, early+fixed, scripts' Update (+starts, coroutines, director), animation+LateUpdate, post-late update (canvases…), particles
        static int statFrames;
        static int statWindows;

        static readonly double[] framePhases = new double[9];

        static void Mark(int phase)
        {
            if (!FrameStats) return;
            double ms = statClock.Elapsed.TotalMilliseconds;
            statTotals[phase] += ms;
            framePhases[phase] += ms;
            statClock.Restart();
        }

        /// Frame stats: the device's video memory counters now, at a point of the start-up or a
        /// load (`when`), besides every 600 frames.
        public static void LogVideoMemory(string when)
        {
            if (!FrameStats) return;
            var video = OniMoono.Engine.Rendering.GraphicsDevice.Current.MemoryCounters();
            if (video == null) return;
            // The engine's side, to hold the device's counts against: live textures, and those
            // with a device resource.
            int textures = 0, onDevice = 0, renderTextures = 0, renderOnDevice = 0;
            foreach (var native in ObjectTable.Snapshot())
            {
                if (native is not OniMoono.Engine.Rendering.TextureNative t || t.Destroyed) continue;
                bool rt = t is OniMoono.Engine.Rendering.RenderTextureNative;
                textures++;
                if (rt) renderTextures++;
                if (t.Gpu == null) continue;
                onDevice++;
                if (rt) renderOnDevice++;
            }
            EngineLog.Engine($"video memory ({when}, frame {Time.frameCount}): {video}; engine textures {textures} ({onDevice} with a device resource), render textures among them {renderTextures} ({renderOnDevice})");
        }

        static void EndStats()
        {
            if (!FrameStats) return;
            // The first frame starts the 600-frame window of FrameStatsWindow's counts.
            if (statWindows == 0 && statFrames == 0) FrameStatsWindow.Take(0);
            // Single slow frames (hitches) are logged with their own breakdown.
            double total = 0;
            foreach (var v in framePhases) total += v;
            if (total > 250)
                EngineLog.Engine($"slow frame {Time.frameCount} ({total:F0} ms): early+fixed {framePhases[4]:F0}, update {framePhases[5]:F0}, late {framePhases[6]:F0}, post-late {framePhases[7]:F0}, render {framePhases[0]:F0}, gui {framePhases[1]:F0}, end of frame {framePhases[2]:F0}, present {framePhases[3]:F0}");
            Array.Clear(framePhases);
            if (++statFrames < 60) return;
            double T(int i) => statTotals[i] / 60;
            EngineLog.Engine($"frame stats (ms/frame over 60): early+fixed {T(4):F2}, update {T(5):F2}, late {T(6):F2}, post-late {T(7):F2}, particles {T(8):F2}, render {T(0):F2}, gui {T(1):F2}, end of frame {T(2):F2}, present {T(3):F2}, gpu {OniMoono.Engine.Rendering.GraphicsDevice.Current.TakeGpuFrameMilliseconds():F2}");
            var counters = OniMoono.Engine.Rendering.GraphicsDevice.Current.TakeFrameCounters(60);
            if (counters != null) EngineLog.Engine("frame counts (per frame over 60): " + counters);
            if (++statWindows % 10 == 0)
            {
                // Every 600 frames: the process's memory and the managed heap, to tell growth from a steady state.
                var gc = GC.GetGCMemoryInfo();
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                EngineLog.Engine("script time (ms/frame over 600): " + BehaviourManager.TakeScriptTimes(600, 25));
                EngineLog.Engine($"memory: working set {self.WorkingSet64 >> 20} MiB, private {self.PrivateMemorySize64 >> 20} MiB, managed heap {gc.HeapSizeBytes >> 20} MiB (committed {gc.TotalCommittedBytes >> 20} MiB), collections {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, paused {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms in all");
                var video = OniMoono.Engine.Rendering.GraphicsDevice.Current.MemoryCounters();
                if (video != null) EngineLog.Engine("video memory: " + video);
                foreach (var line in FrameStatsWindow.Take(600)) EngineLog.Engine(line);
            }
            Array.Clear(statTotals);
            statFrames = 0;
        }

        /// Runs one frame. realDelta is the wall time since the previous frame.
        /// Runs an engine phase's subscribers, each on its own: an exception in one (an engine
        /// fault) is logged and the frame goes on, as Unity's native engine never ends the game
        /// over a managed exception. (Game callbacks inside are guarded one by one already.)
        static void RunPhase(Action phase)
        {
            if (phase == null) return;
            foreach (var subscriber in phase.GetInvocationList())
            {
                try
                {
                    ((Action)subscriber)();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// When the running frame began (Stopwatch ticks; input was polled just before): frame
        /// stats' time from a frame's start to its picture's present.
        public static long FrameStartedAt;

        public static void RunFrame(float realDelta)
        {
            if (!started) throw new InvalidOperationException("PlayerLoop.Initialize must run first");
            FrameStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            FrameTimingManager.BeginFrame(FrameStartedAt);
            if (FrameStats) statClock.Restart();
            // The device's texel copies from the last frame finish before scripts can write texels.
            if (FrameStats)
            {
                long fence = FrameStatsWindow.Now;
                Rendering.TexelCopies.Fence();
                FrameStatsWindow.TexelCopyWait += FrameStatsWindow.Now - fence;
            }
            else Rendering.TexelCopies.Fence();
            Rendering.MeshNative.SweepSpares();
            Time.BeginFrame(realDelta);
            RunPhase(FrameStart);

            RunPhase(EarlyUpdate);

            // Passes of pending Starts (measured in the original, UnityProbe scenarios 13 to 18):
            // at the start of the frame, after each FixedUpdate pass, after Update and after
            // LateUpdate. Each runs the Starts queued before it began; the update passes start
            // their own members just before their first callback.
            BehaviourManager.RunStarts();
            while (Time.BeginFixedStep())
            {
                try
                {
                    BehaviourManager.RunFixedUpdate();
                    BehaviourManager.RunStarts();
                    RunPhase(FixedAnimationUpdate);
                    Coroutines.RunFixedUpdatePhase();
                    // Destroy called in FixedUpdate is carried out before Update (observed in
                    // Unity's player).
                    Lifecycle.ProcessDestroyQueue();
                }
                finally
                {
                    Time.EndFixedStep();
                }
            }

            Mark(4);
            BehaviourManager.RunUpdate();
            // Update.ScriptRunDelayedDynamicFrameRate: Start for behaviours enabled before or
            // during Update (so they start before LateUpdate), then the destruction point, Invoke
            // calls due and the coroutines resumed after Update.
            //
            // These Starts run before the destruction, as observed in Unity's player (UnityProbe
            // scenario 20): they still find what Start or Update destroyed, and a pending Start
            // runs on a descendant that Destroy left enabled (below the object's direct
            // children) before its OnDisable and OnDestroy. The game's SaveLoader makes Game
            // and MinionSelectScreen in a Start; Game's Start, just before its first Update,
            // destroys the screen, whose CharacterContainers start (and register) before their
            // OnDestroy unregisters them.
            BehaviourManager.RunStarts();
            // Destroy called in Start or Update is carried out when the Update pass ends, before
            // the coroutines resumed after it, as observed in Unity's player: LateUpdate already
            // finds the parts gone (Better Automation Overlay strips a template in Update and
            // clones it in LateUpdate).
            Lifecycle.ProcessDestroyQueue();
            DelayedCalls.Run();
            Coroutines.RunUpdatePhase();
            RunPhase(DirectorUpdate);

            Mark(5);
            RunPhase(AnimationUpdate);
            // Behaviours enabled after the Update pass (by coroutines, animation events) get
            // LateUpdate this frame, those with it starting just before it; the others start
            // after it (observed in Unity's player).
            BehaviourManager.RunLateUpdate();
            BehaviourManager.RunStarts();
            // Invoke calls made since the pass after Update.
            DelayedCalls.Run();

            // Destroy called after the Update pass (coroutines, animation events, LateUpdate) is
            // carried out after LateUpdate.
            Lifecycle.ProcessDestroyQueue();
            Mark(6);
            RunPhase(PostLateUpdate);
            Mark(7);
            RunPhase(ParticleUpdate);
            Mark(8);
            FrameTimingManager.Submitting(System.Diagnostics.Stopwatch.GetTimestamp());
            RunPhase(Render);
            Mark(0);
            RunPhase(GUI);
            Mark(1);
            // Destroy called after LateUpdate's destruction point waits for the next one: called
            // in OnGUI or at WaitForEndOfFrame, it is carried out after the next frame's Update
            // pass, and that Update still finds the parts (observed in Unity's player).
            Coroutines.RunEndOfFramePhase();
            Mark(2);
            FrameTimingManager.PresentCalled(System.Diagnostics.Stopwatch.GetTimestamp());
            Present?.Invoke();
            FrameTimingManager.FrameComplete(System.Diagnostics.Stopwatch.GetTimestamp());
            Event.DiscardUnconsumed();
            // A quit requested this frame is put to Application.wantsToQuit now, after
            // WaitForEndOfFrame (UnityProbe's quitevents); a refusal cancels it.
            if (QuitRequested && !quitAgreed)
            {
                if (Application.WantsToQuit()) quitAgreed = true;
                else QuitRequested = false;
            }
            Mark(3);
            EndStats();
        }

        /// Unity sends OnApplicationQuit to every active MonoBehaviour before shutting down, then
        /// raises Application.quitting (UnityProbe's quitevents), then saves PlayerPrefs.
        public static void SendApplicationQuit()
        {
            foreach (var go in Finder.AllSceneGameObjects())
                Messages.SendToComponents(go, s => s.OnApplicationQuit);
            Application.RaiseQuitting();
            PlayerPrefs.SaveOnQuit();
        }

        public static void SendApplicationFocus(bool focus)
        {
            HasFocus = focus;
            foreach (var go in Finder.AllSceneGameObjects())
            {
                if (!go.ActiveInHierarchy) continue;
                foreach (var c in go.Components)
                {
                    if (c is not MonoBehaviour mb || c.Native is not BehaviourNative bn || bn.Script?.OnApplicationFocus == null) continue;
                    try
                    {
                        bn.Script.OnApplicationFocus(mb, focus);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e, mb);
                    }
                }
            }
        }
    }
}
