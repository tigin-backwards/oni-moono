using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Brutal.GlfwApi;
using Brutal.OpenGlApi;
using N = Brutal.OpenGlApi.OpenGl.Native;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Gl
{
    /// ARB_bindless_texture, where the driver has it: sampler uniforms are set to resident
    /// handles of (texture, sampler object) pairs instead of texture units, so draws never change
    /// texture bindings. NVIDIA's OpenGL driver serializes draws whose vertex stage reads
    /// textures other than the previous draw's (the game's animation batches each have their
    /// own data textures: ~37 µs a draw, most of a large colony's frame); with handles there are
    /// no binding changes. The programs' sampler uniforms are declared bindless when compiled
    /// (`layout(bindless_sampler) uniform;`). A texture with a handle keeps its parameters and
    /// image layout fixed (sampling state lives in sampler objects, GlSamplers), so a texture
    /// whose image is specified again is recreated under a new name (GlTextures).
    internal static unsafe class GlBindless
    {
        /// Diagnostics (--no-bindless): the binding path even where bindless textures exist.
        public static bool Disabled;

        /// --bindless: bindless textures even on a driver that binds by default (SlowOnDriver).
        public static bool Forced;

        public static bool Enabled { get; private set; }

        static delegate* unmanaged[Cdecl]<uint, uint, ulong> getTextureSamplerHandle;
        static delegate* unmanaged[Cdecl]<ulong, void> makeResident, makeNonResident;
        static delegate* unmanaged[Cdecl]<uint, int, ulong, void> programUniformHandle;
        static delegate* unmanaged[Cdecl]<ulong, byte> isResident;
        static delegate* unmanaged[Cdecl]<uint, uint, void> bindSampler;
        static delegate* unmanaged[Cdecl]<uint, int, int, void> programUniform1i;

        /// AMD's Windows driver (GL_VENDOR "ATI Technologies Inc."; Mesa's AMD driver reports
        /// "AMD") advertises the extension, but there every draw and every texture upload costs a
        /// flat ~0.25 ms of GPU time with handles resident, against 0.001-0.009 ms bound to units:
        /// a loaded colony's frame took 270-570 ms of GPU time on an RX 9070 XT, 26-30 ms with
        /// --no-bindless (issue #27, measured with --gpu-profile; likely also issue #21's collapse,
        /// the same card, worsening as a colony's resident textures grow). Binding to units has
        /// no such cost there, so that driver binds.
        ///
        /// Mesa's AMD driver (radeonsi, GL_VENDOR "AMD") binds too, as AMD's Windows driver does:
        /// it adds every resident handle's buffer to each command submission
        /// (si_resident_buffers_add_all_to_bo_list), so the cost grows with the textures ever
        /// sampled. A save's colony preview samples about 4.5 times a normal frame's textures, and
        /// on an RX 9070 a colony that ran at 40-50 fps stayed at 20-25 after its first save,
        /// paused or not (issue #43). --bindless turns them on there anyway.
        public static bool SlowOnDriver(string vendor)
            => vendor.Contains("ATI Technologies", StringComparison.Ordinal) || vendor == "AMD";

        /// Why bindless textures are off, or null when they are on.
        public static string Reason { get; private set; }

        /// Loads the entry points (the rendering context is current).
        public static void Initialize(string vendor)
        {
            if (Disabled) { Reason = "--no-bindless"; return; }
            if (!GlPrograms.Extensions.Contains("GL_ARB_bindless_texture")) { Reason = "no ARB_bindless_texture"; return; }
            if (SlowOnDriver(vendor) && !Forced) { Reason = "AMD's driver, where resident handles slow every draw (--bindless turns them on)"; return; }
            getTextureSamplerHandle = (delegate* unmanaged[Cdecl]<uint, uint, ulong>)Glfw.GetProcAddress("glGetTextureSamplerHandleARB"u8);
            makeResident = (delegate* unmanaged[Cdecl]<ulong, void>)Glfw.GetProcAddress("glMakeTextureHandleResidentARB"u8);
            makeNonResident = (delegate* unmanaged[Cdecl]<ulong, void>)Glfw.GetProcAddress("glMakeTextureHandleNonResidentARB"u8);
            programUniformHandle = (delegate* unmanaged[Cdecl]<uint, int, ulong, void>)Glfw.GetProcAddress("glProgramUniformHandleui64ARB"u8);
            isResident = (delegate* unmanaged[Cdecl]<ulong, byte>)Glfw.GetProcAddress("glIsTextureHandleResidentARB"u8);
            bindSampler = (delegate* unmanaged[Cdecl]<uint, uint, void>)Glfw.GetProcAddress("glBindSampler"u8);
            programUniform1i = (delegate* unmanaged[Cdecl]<uint, int, int, void>)Glfw.GetProcAddress("glProgramUniform1i"u8);
            // The extension string above is what says the functions are there. These null checks
            // catch a missing one only where the loader returns null for it, as wglGetProcAddress
            // does; glXGetProcAddress and eglGetProcAddress may return an address for a function
            // the context lacks (GLX 1.4 section 3.3.12, EGL 1.5 section 3.11).
            Enabled = getTextureSamplerHandle != null && makeResident != null && makeNonResident != null && programUniformHandle != null
                      && isResident != null && bindSampler != null && programUniform1i != null;
            if (!Enabled) Reason = "entry points missing";
        }

        static readonly Regex FirstUniform = new(@"^[ \t]*(UNITY_LOCATION\(\d+\)[ \t]*)?uniform\b", RegexOptions.Multiline);

        /// A program stage's text with its sampler uniforms declared bindless (the default
        /// qualifier goes before the first uniform declaration), and the extension it requires.
        public static string Prepare(string body, out string extension)
        {
            extension = "#extension GL_ARB_bindless_texture : require\n";
            var m = FirstUniform.Match(body);
            if (!m.Success) return body;
            return body[..m.Index] + "layout(bindless_sampler) uniform;\n" + body[m.Index..];
        }

        /// The resident handle of a texture with its current sampler object, or 0 when GL gives
        /// it none. GetTextureSamplerHandleARB returns 0, with an error, for a texture name that
        /// has no texture object or a texture that is not complete; 0 is never a valid handle,
        /// and a shader sampling through one that is not a valid resident handle has undefined
        /// results that may include program termination (ARB_bindless_texture, section
        /// 3.9.X and its edits to section 2.14.6, Samplers). A 0 is neither made resident
        /// nor kept, so a texture that becomes complete gets its handle then. The caller samples
        /// a default texture instead, as a unit with an incomplete texture bound reads
        /// (0, 0, 0, 1) (OpenGL 4.6 section 11.1.3.5).
        ///
        /// `bind`: the handle could not be made resident; the caller binds the texture and its
        /// sampler object to the sampler's unit for this draw instead (BindlessResidency.Handle).
        public static ulong Handle(DeviceTexture t, out bool bind)
            => BindlessResidency.Handle(t, t.Sampler != 0 ? t.Sampler : GlSamplers.Default, default(GlResidencyCalls), out bind);

        static bool loggedNoHandle, loggedNotResident;

        public static bool HasHandles(DeviceTexture t) => t.Handles is { Count: > 0 };

        /// The texture's name is about to be deleted or replaced: true when its handles were
        /// resident, in which case the name is deleted once the GPU has finished the frames that
        /// may have sampled it (BindlessResidency.Release); false: the caller deletes it now.
        public static bool Release(DeviceTexture t) => BindlessResidency.Release(t, default(GlResidencyCalls));

        public static void EndFrame() => BindlessResidency.EndFrame(default(GlResidencyCalls));

        /// Validation (ONI_VERIFY_BINDLESS=1): the GL's own word on a handle's residency.
        public static bool IsResident(ulong handle) => isResident(handle) != 0;

        /// Binds a texture with its sampler object to a sampler's unit and points the sampler
        /// uniform at that unit: a texture whose handle is not resident is sampled this way,
        /// as without bindless textures. A bindless_sampler uniform takes either a handle or a
        /// unit, whichever was set last (ARB_bindless_texture, edits to section 2.14.6, and
        /// Issue 12); the sampler object gives the same sampling state as the handle's pair.
        public static void BindToUnit(uint program, int location, int unit, DeviceTexture t)
        {
            GlBindings.BindTexture(unit, t.Target, t.Id);
            bindSampler((uint)unit, t.Sampler != 0 ? t.Sampler : GlSamplers.Default);
            programUniform1i(program, location, unit);
        }

        internal readonly struct GlResidencyCalls : BindlessResidency.ICalls
        {
            public ulong Create(DeviceTexture t, uint sampler)
            {
                ulong handle = t.Id == 0 ? 0 : getTextureSamplerHandle(t.Id, sampler);
                if (handle == 0 && !loggedNoHandle)
                {
                    loggedNoHandle = true;
                    OniMoono.Engine.EngineLog.Engine($"bindless: no handle for texture {t.Id} ({t.Width}x{t.Height}, target 0x{t.Target:X}, sampler {sampler}): not a texture or not complete; sampling the default texture instead (logged once)");
                }
                return handle;
            }

            /// MakeTextureHandleResidentARB, checked: no GL error and IsTextureHandleResidentARB
            /// true. The spec names no error for running out of residency (Issue 16), and a
            /// driver may report one with GL_OUT_OF_MEMORY and a debug message only, or not at
            /// all. Errors already queued are taken first, so that they are not blamed on it.
            public bool Resident(ulong handle)
            {
                while (getError() != 0) { }
                makeResident(handle);
                uint error = getError();
                bool resident = isResident(handle) != 0;
                if (error == 0 && resident) return true;
                // Reported resident with an error: made non-resident again, so that a retry
                // starts from a known state.
                if (resident) makeNonResident(handle);
                while (getError() != 0) { }
                if (!loggedNotResident)
                {
                    loggedNotResident = true;
                    OniMoono.Engine.EngineLog.Engine($"bindless: a texture handle could not be made resident (GL error 0x{error:X}, resident {resident}) with {BindlessResidency.ResidentCount} textures resident; such textures are bound to texture units instead (logged once)");
                }
                return false;
            }

            public void NonResident(ulong handle) => makeNonResident(handle);
            public void Delete(uint name) => GlBindings.DeleteTexture(name);

            public long Fence()
            {
                long token = ++lastFence;
                fences[token] = N.glFenceSync(SyncGpuCommandsComplete, default);
                return token;
            }

            /// The fence has signalled (it is deleted then); a failed wait counts as signalled.
            public bool Signalled(long token)
            {
                var fence = fences[token];
                if (N.glClientWaitSync(fence, default, 0) == TimeoutExpired) return false;
                N.glDeleteSync(fence);
                fences.Remove(token);
                return true;
            }
        }

        // glGetError is OpenGL 1.1, which wglGetProcAddress does not give: the loader's.
        static uint getError() => (uint)N.glGetError();

        const uint SyncGpuCommandsComplete = 0x9117, TimeoutExpired = 0x911B;
        static readonly Dictionary<long, GlSync> fences = new();
        static long lastFence;

        public static void SetSampler(uint program, int location, ulong handle) => programUniformHandle(program, location, handle);
    }

    /// Which textures have resident bindless handles (GL thread), and when handles stop being
    /// resident.
    ///
    /// - A handle is made resident when a draw first samples its texture with that sampler
    ///   object, and checked (ICalls.Resident). One that fails is not sampled: the texture is
    ///   bound to a unit for the draw (Handle's `bind`), and residency is tried again
    ///   RetryFrames later. The resident count at a failure becomes a cap (below).
    /// - A texture deleted or given a new name (its image specified anew) while resident keeps
    ///   its old name and resident handles until the GPU has finished every frame that may
    ///   have sampled it: they go with the frame's fence, and are made non-resident and deleted
    ///   once it has signalled. ARB_bindless_texture leaves open whether
    ///   MakeTextureHandleNonResidentARB waits for draws already issued, and sampling a handle
    ///   that is not resident is undefined and may terminate the program (sections 3.9.X and
    ///   2.14.6); the frames in flight are the current one and up to two queued behind the
    ///   presenter.
    /// - Idle textures (IdleFrames, ONI_BINDLESS_IDLE_FRAMES=N: not sampled for N frames) and,
    ///   past the cap (MaxResident, ONI_BINDLESS_MAX_RESIDENT, or a failure's count, whichever
    ///   is lower) textures, the least recently sampled
    ///   ones, are made non-resident the same way, after a fence, and resident again when next
    ///   sampled, as the extension's own example does with a handle it "doesn't need to use
    ///   [...] for a while", "to reduce the overall memory footprint" (Overview). A texture
    ///   sampled again before its fence has signalled simply stays resident.
    internal static class BindlessResidency
    {
        /// The residency calls (GL's, or a test's).
        public interface ICalls
        {
            /// A new handle of the texture with the sampler object (not resident), or 0.
            ulong Create(DeviceTexture t, uint sampler);
            /// Makes the handle resident; false when it could not be (it is not resident then).
            bool Resident(ulong handle);
            void NonResident(ulong handle);
            /// Deletes a texture name.
            void Delete(uint name);
            /// A fence after the commands issued so far.
            long Fence();
            /// Whether the fence has signalled (it is forgotten then).
            bool Signalled(long fence);
        }

        public static int IdleFrames = Setting("ONI_BINDLESS_IDLE_FRAMES", 0);

        /// The cap, a safety net (0: none). NVIDIA's driver 610.88 on an RTX 3090 kept 200,000
        /// handles resident without an error, and 28 GB of textures past its 24 GB, while a
        /// Khronos forum report (2022, an NVIDIA GPU on Windows) had residency fail at 1,600 to
        /// 4,000. Large colonies sample 1,400 to 4,200 textures in 60 frames (DeCrew the most),
        /// but what they have ever sampled reaches 7,000 to 20,000 (an autosave's colony
        /// preview, or DeCrew's first frames), all resident from then on without a cap. 8,192 is
        /// twice the largest working set measured: what a preview left resident goes, the
        /// textures in use stay.
        public static int MaxResident = Setting("ONI_BINDLESS_MAX_RESIDENT", 8192);

        static int Setting(string name, int value) => int.TryParse(Environment.GetEnvironmentVariable(name), out var n) && n >= 0 ? n : value;

        /// Idle textures are looked for every SweepFrames frames.
        public const int SweepFrames = 16;

        /// Frames after a failed residency before the texture's handle is tried again.
        public const int RetryFrames = 64;

        /// Past the cap, textures are made non-resident down to this share of it (eighths).
        const int CapLowEighths = 7;

        /// Textures with resident handles (also those waiting for their fence to go idle).
        static readonly HashSet<DeviceTexture> resident = new(ReferenceEqualityComparer.Instance);
        static readonly List<DeviceTexture> idle = new();

        /// Waits for a fence: a name to delete with its resident handles, or (Texture set) a
        /// texture to make non-resident for being idle, if it still is (Eviction unchanged).
        readonly record struct Retired(DeviceTexture Texture, int Eviction, ulong[] Handles, uint Name);

        /// What the current frame retired so far, and earlier frames' by their fences.
        static List<Retired> retiring = new();
        static readonly Queue<(long Fence, List<Retired> Items)> fenced = new();
        static int waiting, evicting;
        static long frame;

        /// Textures made non-resident for being idle and made resident again, failed
        /// residencies and draws that bound a texture instead, since the last counters.
        static long slept, woken, failed, boundDraws;
        static int peak;

        public static int ResidentCount => resident.Count;

        /// Retired names and handles waiting for their fence.
        public static int Waiting => waiting;

        /// The handle of `t` with `sampler` for a draw, made resident first, or 0 (no handle:
        /// ICalls.Create gave none). `bind`: no resident handle can be had now; the texture is
        /// to be sampled bound to a unit.
        public static ulong Handle<T>(DeviceTexture t, uint sampler, T calls, out bool bind) where T : ICalls
        {
            bind = false;
            t.SampledFrame = frame;
            if (t.Evicting)
            {
                // Sampled again before its fence: it stays resident (the queued eviction is void).
                t.Evicting = false;
                evicting--;
            }
            if (t.Idle && !Wake(t, calls))
            {
                bind = true;
                boundDraws++;
                return 0;
            }
            if (t.Handles != null && t.Handles.TryGetValue(sampler, out var handle)) return handle;
            if (t.FailedFrame >= 0 && frame - t.FailedFrame < RetryFrames)
            {
                bind = true;
                boundDraws++;
                return 0;
            }
            handle = calls.Create(t, sampler);
            if (handle == 0) return 0;
            if (!calls.Resident(handle))
            {
                Failed(t);
                bind = true;
                boundDraws++;
                return 0;
            }
            t.FailedFrame = -1;
            (t.Handles ??= new Dictionary<uint, ulong>())[sampler] = handle;
            if (resident.Add(t)) peak = Math.Max(peak, resident.Count);
            return handle;
        }

        /// An idle texture's handles made resident again; on a failure none stay resident.
        static bool Wake<T>(DeviceTexture t, T calls) where T : ICalls
        {
            if (t.FailedFrame >= 0 && frame - t.FailedFrame < RetryFrames) return false;
            int made = 0;
            foreach (var handle in t.Handles.Values)
            {
                if (calls.Resident(handle)) { made++; continue; }
                // Those made resident just now were never sampled: non-resident at once.
                foreach (var h in t.Handles.Values)
                {
                    if (made-- == 0) break;
                    calls.NonResident(h);
                }
                Failed(t);
                return false;
            }
            t.Idle = false;
            t.FailedFrame = -1;
            if (resident.Add(t)) peak = Math.Max(peak, resident.Count);
            woken++;
            return true;
        }

        /// A residency failed: from then on the cap is at most the resident count it failed at,
        /// so the least recently sampled textures make room (a driver's limit, met once, is
        /// kept under).
        static void Failed(DeviceTexture t)
        {
            t.FailedFrame = frame;
            failed++;
            if (failedAt == 0 || resident.Count < failedAt) failedAt = Math.Max(resident.Count, MinFailedCap);
        }

        /// The fewest textures a failure lowers the cap to.
        const int MinFailedCap = 64;

        /// The resident count at the lowest failure (0: none), and the cap in effect.
        static int failedAt;
        static int Cap => failedAt > 0 && (MaxResident == 0 || failedAt < MaxResident) ? failedAt : MaxResident;

        /// The texture's name is to be deleted or replaced: its handles are forgotten. When they
        /// were resident, the name and handles wait for the frame's fence (true: they are
        /// deleted later); false: nothing samples it (no handles, or all non-resident since a
        /// fence), and the caller deletes the name now.
        public static bool Release<T>(DeviceTexture t, T calls) where T : ICalls
        {
            t.FailedFrame = -1;
            if (t.Handles == null || t.Handles.Count == 0) return false;
            bool wasResident = !t.Idle;
            if (wasResident)
            {
                retiring.Add(new Retired(null, 0, [.. t.Handles.Values], t.Id));
                waiting++;
                resident.Remove(t);
            }
            if (t.Evicting) evicting--;
            t.Evicting = false;
            t.Eviction++;
            t.Idle = false;
            t.Handles.Clear();
            return wasResident;
        }

        /// The GL thread's frame ended (its present is about to be posted): idle textures are
        /// queued to go non-resident, the frame's retirements get a fence, and those of earlier
        /// frames whose fence has signalled are done.
        public static void EndFrame<T>(T calls) where T : ICalls
        {
            frame++;
            if (frame % SweepFrames == 0) Sweep();
            if (retiring.Count > 0)
            {
                fenced.Enqueue((calls.Fence(), retiring));
                retiring = new List<Retired>();
            }
            while (fenced.Count > 0 && calls.Signalled(fenced.Peek().Fence))
                foreach (var r in fenced.Dequeue().Items) Finish(r, calls);
        }

        static void Finish<T>(Retired r, T calls) where T : ICalls
        {
            if (r.Texture == null)
            {
                foreach (var handle in r.Handles) calls.NonResident(handle);
                calls.Delete(r.Name);
                waiting--;
                return;
            }
            var t = r.Texture;
            waiting--;
            if (!t.Evicting || t.Eviction != r.Eviction) return;
            foreach (var handle in t.Handles.Values) calls.NonResident(handle);
            t.Evicting = false;
            evicting--;
            t.Idle = true;
            resident.Remove(t);
            slept++;
        }

        /// Queues textures to go non-resident: those idle for IdleFrames, and past the cap
        /// the least recently sampled (none sampled in the last two frames is left out).
        static void Sweep()
        {
            if (IdleFrames > 0)
                foreach (var t in resident)
                    if (!t.Evicting && frame - t.SampledFrame > IdleFrames) idle.Add(t);
            int cap = Cap;
            int over = cap > 0 ? resident.Count - evicting - idle.Count - cap * CapLowEighths / 8 : 0;
            if (cap > 0 && resident.Count - evicting - idle.Count > cap)
            {
                var oldest = new List<DeviceTexture>();
                foreach (var t in resident)
                    if (!t.Evicting && frame - t.SampledFrame > 2 && !(IdleFrames > 0 && frame - t.SampledFrame > IdleFrames)) oldest.Add(t);
                oldest.Sort((a, b) => a.SampledFrame.CompareTo(b.SampledFrame));
                for (int i = 0; i < oldest.Count && i < over; i++) idle.Add(oldest[i]);
            }
            foreach (var t in idle)
            {
                t.Evicting = true;
                t.Eviction++;
                evicting++;
                waiting++;
                retiring.Add(new Retired(t, t.Eviction, null, 0));
            }
            idle.Clear();
        }

        /// The frames the counters' working set covers.
        const int RecentFrames = 60;

        /// Diagnostics: the textures with resident handles, their handles and estimated bytes,
        /// the most at once, how many of them were sampled in the last RecentFrames frames, what waits for a fence, and since the last call the idle textures
        /// made non-resident and resident again, failed residencies and draws that bound one.
        public static string TakeCounters()
        {
            int handles = 0, recent = 0;
            long bytes = 0;
            foreach (var t in resident)
            {
                handles += t.Handles.Count;
                bytes += t.Bytes;
                if (frame - t.SampledFrame < RecentFrames) recent++;
            }
            string text = $"{resident.Count} resident ({handles} handles, {bytes >> 20} MiB; most {peak}; {recent} sampled in the last {RecentFrames} frames), {waiting} waiting for a fence";
            int cap = Cap;
            if (IdleFrames > 0 || cap > 0)
                text += $", {(IdleFrames > 0 ? $"idle after {IdleFrames} frames" : "")}{(IdleFrames > 0 && cap > 0 ? ", " : "")}{(cap > 0 ? $"at most {cap}" : "")}: {slept} made non-resident, {woken} resident again";
            if (failed > 0 || boundDraws > 0) text += $", {failed} failed residencies, {boundDraws} textures bound instead";
            slept = woken = failed = boundDraws = 0;
            return text;
        }

        /// Validation: the texture has resident handles and `handle` is one of them.
        public static bool IsResident(DeviceTexture t, ulong handle)
        {
            if (t.Idle || !resident.Contains(t) || t.Handles == null) return false;
            foreach (var h in t.Handles.Values)
                if (h == handle) return true;
            return false;
        }

        /// Tests: forgets every texture.
        internal static void Reset()
        {
            resident.Clear();
            idle.Clear();
            retiring = new List<Retired>();
            fenced.Clear();
            waiting = evicting = peak = failedAt = 0;
            frame = slept = woken = failed = boundDraws = 0;
        }
    }

    /// Validation (ONI_VERIFY_BINDLESS=1): before each draw, every handle it samples is one of
    /// its texture's, the texture is not deleted, and its handles are resident by our count
    /// and by GL's (IsTextureHandleResidentARB); a texture with handles never has its image or
    /// parameters specified again, which ARB_bindless_texture forbids (INVALID_OPERATION,
    /// Issues 4 and 5), and a mip generation on one gives no GL error. Logs `bindless handles:
    /// N draws verified, M wrong` (wrong: draws and texture specifications) every 100,000 draws
    /// for scripts/qa-tools.py verify, and the first 20 problems.
    internal static unsafe class BindlessVerify
    {
        public static bool Enabled = Environment.GetEnvironmentVariable("ONI_VERIFY_BINDLESS") == "1";

        static long draws, wrong, bound, logged;
        static bool drawWrong;

        /// Sampler `index` of the draw's program samples `t` through `handle`, or bound to its
        /// unit (`bind`).
        public static void Sampler(DeviceProgram program, int index, DeviceTexture t, ulong handle, bool bind)
        {
            string problem = null;
            if (t.Id == 0) problem = "a deleted texture";
            else if (bind) bound++;
            else if (handle == 0) problem = "no handle";
            else if (!BindlessResidency.IsResident(t, handle)) problem = t.Idle ? "a handle made non-resident" : "a handle not among its texture's resident ones";
            else if (!GlBindless.IsResident(handle)) problem = "a handle GL says is not resident";
            if (problem == null) return;
            Log($"sampler {program.Samplers[index].Location} samples {problem} (texture {t.Id}, {t.Width}x{t.Height}, handle 0x{handle:X})");
            drawWrong = true;
        }

        public static void Drawn()
        {
            draws++;
            if (drawWrong) wrong++;
            drawWrong = false;
            if (draws == 1000 || draws % 100_000 == 0)
            {
                OniMoono.Engine.EngineLog.Engine($"bindless handles: {draws} draws verified, {wrong} wrong");
                OniMoono.Engine.EngineLog.Engine($"bindless residency: {bound} textures bound instead, {BindlessResidency.ResidentCount} resident, {BindlessResidency.Waiting} waiting for a fence");
            }
        }

        /// The texture's image or parameters are about to be specified (`what`).
        public static void Specify(DeviceTexture g, string what)
        {
            if (!Enabled || !GlBindless.HasHandles(g)) return;
            wrong++;
            Log($"{what} on texture {g.Id} ({g.Width}x{g.Height}), which has handles");
        }

        /// Before a glGenerateMipmap: earlier GL errors are taken, so that they are not blamed on it.
        public static void BeforeMips()
        {
            if (Enabled) while (N.glGetError() != 0) { }
        }

        /// glGenerateMipmap ran on the texture: any GL error it gave when it has handles.
        public static void MipsGenerated(DeviceTexture g)
        {
            if (!Enabled || !GlBindless.HasHandles(g)) return;
            uint error = (uint)N.glGetError();
            if (error == 0) return;
            wrong++;
            Log($"glGenerateMipmap on texture {g.Id} ({g.Width}x{g.Height}), which has handles: GL error 0x{error:X}");
        }

        static void Log(string problem)
        {
            if (logged++ < 20) OniMoono.Engine.EngineLog.Engine($"bindless handles: wrong: {problem}");
        }
    }

    /// Sampler objects by sampling state (bindless handles pair a texture with one).
    internal static class GlSamplers
    {
        static readonly Dictionary<(GlTextures.SamplerSettings Settings, bool Mips, uint Target), uint> samplers = new();
        static uint defaultSampler;

        /// The state of a texture that no sampler settings have been applied to: linear
        /// filtering and repeat, as the device's default textures have. Repeat and a linear
        /// magnification filter are GL's defaults; its minification default is
        /// NEAREST_MIPMAP_LINEAR (OpenGL 4.6 section 8.22, table 23.18), set here to LINEAR.
        public static uint Default
        {
            get
            {
                if (defaultSampler == 0)
                {
                    defaultSampler = Create();
                    Set(defaultSampler, TextureMinFilter, (int)Linear);
                    Set(defaultSampler, TextureMagFilter, (int)Linear);
                }
                return defaultSampler;
            }
        }

        public static uint Get(GlTextures.SamplerSettings s, bool mips, uint target)
        {
            var key = (s, mips, target);
            if (samplers.TryGetValue(key, out var id)) return id;
            id = Create();
            uint min = s.FilterMode switch
            {
                UnityEngine.FilterMode.Point => mips ? NearestMipmapNearest : Nearest,
                UnityEngine.FilterMode.Bilinear => mips ? LinearMipmapNearest : Linear,
                _ => mips ? LinearMipmapLinear : Linear,
            };
            Set(id, TextureMinFilter, (int)min);
            Set(id, TextureMagFilter, (int)(s.FilterMode == UnityEngine.FilterMode.Point ? Nearest : Linear));
            Set(id, TextureWrapS, (int)GlTextures.Wrap(s.WrapU));
            Set(id, TextureWrapT, (int)GlTextures.Wrap(s.WrapV));
            Set(id, TextureWrapR, (int)GlTextures.Wrap(s.WrapW));
            SetF(id, TextureLodBias, s.MipBias);
            if (GlDevice.MaxAnisotropy > 1f)
                SetF(id, TextureMaxAnisotropy, s.FilterMode == UnityEngine.FilterMode.Point ? 1f : Math.Clamp(s.Aniso, 1, GlDevice.MaxAnisotropy));
            samplers[key] = id;
            return id;
        }

        static unsafe uint Create()
        {
            GlSampler id = default;
            N.glCreateSamplers(1, &id);
            return id.Id;
        }

        static unsafe void Set(uint sampler, uint name, int value) => N.glSamplerParameteri(new GlSampler(sampler), name, value);
        static unsafe void SetF(uint sampler, uint name, float value) => N.glSamplerParameterf(new GlSampler(sampler), name, value);
    }
}
