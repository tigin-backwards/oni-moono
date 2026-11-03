using System;
using System.Collections.Generic;
using System.Threading;
using Brutal.GlfwApi;

namespace OniMoono.Desktop.Gl
{
    /// The thread that owns the rendering context and executes all GL work, as Unity's render
    /// thread: the main thread records a frame's work as commands that carry everything they
    /// read (resolved state, uniform values, copies of changed texel and vertex data) and hands
    /// the frame over at present; the GL thread executes it while the main thread records the
    /// next one. One frame is in flight: handing over a frame first waits until the previous
    /// one has been executed. Work whose result the main thread needs (pixel read-back,
    /// program compilation, diagnostics) is a call that runs after everything recorded before
    /// it and waits for it.
    internal sealed class GlWorker : IDisposable
    {
        /// One frame's commands and the data they reference.
        internal sealed class Frame
        {
            public readonly List<Action> Commands = new();
            public readonly FrameArena Arena = new();

            public void Reset()
            {
                Commands.Clear();
                Arena.Reset();
            }
        }

        readonly Thread thread;
        readonly object gate = new();
        Frame recording = new();
        Frame spare = new();
        // Handed to the GL thread under the gate.
        Frame submitted;
        bool stopping;
        Exception failure;

        /// Reports a command that threw (GL thread). The frame goes on with the next command, as
        /// an exception in a draw on the main thread used to unwind only that draw.
        readonly Action<Exception> commandFailed;

        /// Runs on the GL thread before each handed-over batch of commands executes.
        public Action BatchStarting;

        /// Work the GL thread does while no frame is handed over, one short piece at a time
        /// (false: there was none), and whether there is any (GlBackgroundUploads).
        public Func<bool> IdleWork;
        public Func<bool> HasIdleWork;
        // The last idle work could not go on although there is more (GL thread).
        bool idleBlocked;

        /// Wakes the GL thread for idle work.
        public void Wake()
        {
            lock (gate) Monitor.PulseAll(gate);
        }

        public GlWorker(GlfwWindow context, Action<Exception> commandFailed)
        {
            this.commandFailed = commandFailed;
            // The context moves from the creating thread to the GL thread.
            Glfw.PInvoke.MakeContextCurrent(null!);
            thread = new Thread(() => Run(context)) { Name = "GL", IsBackground = true };
            thread.Start();
        }

        /// A worker that only records (tests): no GL thread runs what is posted, and nothing is
        /// handed over.
        internal GlWorker()
        {
        }

        /// The frame being recorded (main thread).
        public Frame Recording => recording;

        public FrameArena Arena => recording.Arena;

        /// Appends work to the frame being recorded.
        public void Post(Action command) => recording.Commands.Add(command);

        /// Hands the recorded frame to the GL thread once the previous one has been executed.
        public void Submit()
        {
            if (thread == null) throw new InvalidOperationException("This worker only records.");
            Frame next;
            lock (gate)
            {
                // After shutdown nothing executes recorded work any more.
                if (stopping)
                {
                    recording.Reset();
                    return;
                }
                while (submitted != null && failure == null) Monitor.Wait(gate);
                ThrowIfFailed();
                // The previous frame has been executed: its storage records the next one.
                next = finished ?? spare;
                finished = null;
                spare = null;
                submitted = recording;
                Monitor.PulseAll(gate);
            }
            recording = next ?? new Frame();
        }

        // The executed frame, returned to the main thread for reuse.
        Frame finished;

        /// Runs `work` on the GL thread after everything recorded so far and returns its result.
        /// Throws GlDeviceStoppedException once the GL thread has stopped (the window closed),
        /// when nothing runs the work.
        public T Call<T>(Func<T> work)
        {
            if (!TryCall(work, out var result)) throw new GlDeviceStoppedException();
            return result;
        }

        public void Call(Action work) => Call(() => { work(); return 0; });

        /// Call, or false (and no result) once the GL thread has stopped.
        public bool TryCall<T>(Func<T> work, out T result)
        {
            T value = default;
            Exception error = null;
            bool ran = false;
            Post(() =>
            {
                try { value = work(); }
                catch (Exception e) { error = e; }
                ran = true;
            });
            Submit();
            WaitIdle();
            // After shutdown Submit drops the work, and WaitIdle also returns when shutdown
            // starts while the work is still queued: it has not run (read under the gate, which
            // the GL thread takes after running a frame).
            lock (gate)
            {
                if (!ran)
                {
                    result = default;
                    return false;
                }
            }
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            result = value;
            return true;
        }

        /// Waits until every submitted frame has been executed.
        public void WaitIdle()
        {
            lock (gate)
            {
                while (submitted != null && failure == null && !stopping) Monitor.Wait(gate);
                ThrowIfFailed();
            }
        }

        void ThrowIfFailed()
        {
            if (failure != null) throw new InvalidOperationException("Rendering failed on the GL thread.", failure);
        }

        void Run(GlfwWindow context)
        {
            try
            {
                context.MakeContextCurrent();
                while (true)
                {
                    Frame frame;
                    lock (gate)
                    {
                        while (submitted == null && !stopping && (HasIdleWork?.Invoke() != true || idleBlocked))
                        {
                            // Idle work that could not go on (waiting for the GPU) is tried again soon.
                            if (idleBlocked) { Monitor.Wait(gate, 2); idleBlocked = false; }
                            else Monitor.Wait(gate);
                        }
                        if (submitted == null && stopping) break;
                        frame = submitted;
                    }
                    if (frame == null)
                    {
                        try { idleBlocked = IdleWork?.Invoke() == false && HasIdleWork?.Invoke() == true; }
                        catch (Exception e) { commandFailed(e); }
                        continue;
                    }
                    BatchStarting?.Invoke();
                    long start = GlTimings.Now;
                    foreach (var command in frame.Commands)
                    {
                        try { command(); }
                        catch (Exception e) { commandFailed(e); }
                    }
                    GlTimings.Busy += GlTimings.Now - start;
                    System.Threading.Interlocked.Add(ref OniMoono.Engine.StartupStats.GlBusyTicks, GlTimings.Now - start);
                    frame.Reset();
                    lock (gate)
                    {
                        submitted = null;
                        finished = frame;
                        Monitor.PulseAll(gate);
                    }
                }
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    failure = e;
                    Monitor.PulseAll(gate);
                }
            }
            finally
            {
                Glfw.PInvoke.MakeContextCurrent(null!);
            }
        }

        /// Executes what was submitted, then stops the thread.
        public void Dispose()
        {
            if (thread == null) return;
            lock (gate)
            {
                stopping = true;
                Monitor.PulseAll(gate);
            }
            thread.Join();
        }
    }

    /// A call for the GL thread's result after it stopped (the window closed): nothing runs it.
    internal sealed class GlDeviceStoppedException : InvalidOperationException
    {
        public GlDeviceStoppedException() : base("The OpenGL device has stopped (the window closed); nothing runs GL work any more.") { }
    }

    /// Per-frame storage for command data (uniform values and block contents), reused once the
    /// frame has been executed. Growing replaces the arrays: commands recorded earlier keep
    /// the arrays they were given.
    internal sealed class FrameArena
    {
        float[] floats;
        int floatCount;
        byte[] bytes;
        int byteCount;

        public FrameArena() : this(1 << 16, 1 << 18) { }

        FrameArena(int floatCapacity, int byteCapacity)
        {
            floats = new float[floatCapacity];
            bytes = new byte[byteCapacity];
        }
        readonly List<object> objects = new();
        int objectCount;
        /// Arenas of their own for the frame's worker threads (Child), reset with this one.
        readonly List<FrameArena> children = new();

        public void Reset()
        {
            floatCount = 0;
            byteCount = 0;
            objectCount = 0;
            foreach (var list in stateCommands.Values) list.Count = 0;
            foreach (var c in children) c.Reset();
        }

        abstract class StateCommands
        {
            public int Count;
        }

        sealed class StateCommands<T> : StateCommands
        {
            public readonly List<StateCommand<T>> Items = new();
        }

        readonly Dictionary<Type, StateCommands> stateCommands = new();

        /// A command that runs `work` (its delegate made once) with `state`, from a list per state
        /// type reused once the frame has run (beside the draws' objects in Next, whose places it
        /// does not take): a texture or mesh upload, sampler change, target, viewport, scissor or
        /// clear recorded this way allocates nothing.
        public Action Command<T>(Action<T> work, T state)
        {
            if (!stateCommands.TryGetValue(typeof(T), out var list)) stateCommands[typeof(T)] = list = new StateCommands<T>();
            var typed = (StateCommands<T>)list;
            StateCommand<T> command;
            if (typed.Count < typed.Items.Count) command = typed.Items[typed.Count];
            else typed.Items.Add(command = new StateCommand<T>());
            typed.Count++;
            return command.Set(work, state);
        }

        /// The frame's `index`th child arena, for one thread's recording while others record into
        /// their own (made on the recording thread before they start).
        public FrameArena Child(int index)
        {
            // A chunk of a queue needs a fraction of a frame's storage; it grows as the frame's does.
            while (children.Count <= index) children.Add(new FrameArena(1 << 12, 1 << 14));
            return children[index];
        }

        /// An object for this frame's commands, reused from the frame's last use when there is
        /// one of the type at this position (the GL thread is done with it once the frame has
        /// run); `create` makes one otherwise.
        public T Next<T>(Func<T> create) where T : class
        {
            if (objectCount < objects.Count && objects[objectCount] is T reused)
            {
                objectCount++;
                return reused;
            }
            var made = create();
            if (objectCount < objects.Count) objects[objectCount] = made;
            else objects.Add(made);
            objectCount++;
            return made;
        }

        /// `length` floats: the array and the offset of the range in it.
        public (float[] Array, int Offset) Floats(int length)
        {
            if (floatCount + length > floats.Length)
            {
                floats = new float[Math.Max(floats.Length * 2, length)];
                floatCount = 0;
            }
            int offset = floatCount;
            floatCount += length;
            return (floats, offset);
        }

        /// `length` zeroed bytes.
        public (byte[] Array, int Offset) Bytes(int length)
        {
            if (byteCount + length > bytes.Length)
            {
                bytes = new byte[Math.Max(bytes.Length * 2, length)];
                byteCount = 0;
            }
            int offset = byteCount;
            byteCount += length;
            Array.Clear(bytes, offset, length);
            return (bytes, offset);
        }
    }

    /// What a recorded StateCommand will run (tests).
    internal interface IStateCommand
    {
        System.Reflection.MethodInfo Work { get; }
    }

    /// Work with its state recorded for the GL thread (FrameArena.Command): reused with its
    /// delegate, and it lets go of both when it runs.
    internal sealed class StateCommand<T> : IStateCommand
    {
        public readonly Action Run;
        Action<T> work;
        T state;

        public System.Reflection.MethodInfo Work => work?.Method;

        public StateCommand() => Run = Execute;

        public Action Set(Action<T> work, T state)
        {
            (this.work, this.state) = (work, state);
            return Run;
        }

        void Execute()
        {
            var (w, s) = (work, state);
            (work, state) = (null, default);
            w(s);
        }
    }
}
