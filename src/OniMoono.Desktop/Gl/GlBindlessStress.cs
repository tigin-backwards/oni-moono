using System;
using System.Collections.Generic;
using Brutal.GlfwApi;
using Brutal.OpenGlApi;
using OniMoono.Engine;
using N = Brutal.OpenGlApi.OpenGl.Native;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Gl
{
    /// Diagnostics (ONI_BINDLESS_STRESS=COUNT[:SIZE[:MIB[:CHECKMIB]]]): how many texture handles the driver
    /// keeps resident. At start-up, before the game draws anything, COUNT textures of SIZE x SIZE
    /// RGBA8 (default 64) are made, filled white, and each one's handle is made resident, with
    /// glGetError and glIsTextureHandleResidentARB read after each; the driver's debug messages
    /// are collected meanwhile. Every 256 handles, and at the end, one draw samples every handle
    /// reported resident so far and counts those that do not read white. It stops at the first
    /// failure (a few more are tried, to see whether it persists), at COUNT, or at MIB megabytes
    /// of textures (default 4096), with no check draw past CHECKMIB (the fourth part); then everything it made is deleted and the game starts as
    /// usual. Handles that failed are never sampled. The log has `bindless stress:` lines.
    internal static unsafe class GlBindlessStress
    {
        public static void RunIfAsked()
        {
            var spec = Environment.GetEnvironmentVariable("ONI_BINDLESS_STRESS");
            if (string.IsNullOrEmpty(spec) || !GlBindless.Enabled) return;
            var parts = spec.Split(':');
            int count = int.Parse(parts[0]);
            int size = parts.Length > 1 ? int.Parse(parts[1]) : 64;
            long budget = (parts.Length > 2 ? long.Parse(parts[2]) : 4096) << 20;
            checkLimit = (parts.Length > 3 ? long.Parse(parts[3]) : 1L << 40) << 20;
            try { Run(count, size, budget); }
            catch (Exception e) { EngineLog.Engine($"bindless stress: failed: {e}"); }
            GlBindings.Invalidate();
            GlStateCache.Invalidate();
        }

        /// No check draw once the textures exceed this (MiB, the fourth part): a draw whose
        /// textures do not fit in video memory at once is not what is measured.
        static long checkLimit;

        static readonly List<string> messages = new();
        static int messageCount;

        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        static void OnMessage(uint source, uint type, uint id, uint severity, int length, byte* message, void* user)
        {
            messageCount++;
            var text = length >= 0 ? System.Text.Encoding.UTF8.GetString(message, length) : new string((sbyte*)message);
            if (messages.Count < 8) messages.Add($"id {id} type 0x{type:X} severity 0x{severity:X}: {text}");
        }

        const string Vertex = "#version 450\nvoid main() { vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }\n";
        const string Fragment = "#version 450\n#extension GL_ARB_bindless_texture : require\n"
            + "layout(std430, binding = 0) readonly buffer Handles { uvec2 handles[]; };\n"
            + "uniform int count;\nout vec4 color;\n"
            + "void main() { float bad = 0.0; for (int i = 0; i < count; i++) { vec4 c = textureLod(sampler2D(handles[i]), vec2(0.5), 0.0); if (c.r < 0.5 || c.a < 0.5) bad += 1.0; } color = vec4(bad, 0.0, 0.0, 1.0); }\n";

        static void Run(int count, int size, long budget)
        {
            var getHandle = (delegate* unmanaged[Cdecl]<uint, uint, ulong>)Glfw.GetProcAddress("glGetTextureSamplerHandleARB"u8);
            var resident = (delegate* unmanaged[Cdecl]<ulong, void>)Glfw.GetProcAddress("glMakeTextureHandleResidentARB"u8);
            var nonResident = (delegate* unmanaged[Cdecl]<ulong, void>)Glfw.GetProcAddress("glMakeTextureHandleNonResidentARB"u8);
            var isResident = (delegate* unmanaged[Cdecl]<ulong, byte>)Glfw.GetProcAddress("glIsTextureHandleResidentARB"u8);
            var getError = (delegate* unmanaged[Cdecl]<uint>)Glfw.GetProcAddress("glGetError"u8);
            var finish = (delegate* unmanaged[Cdecl]<void>)Glfw.GetProcAddress("glFinish"u8);
            var bindBase = (delegate* unmanaged[Cdecl]<uint, uint, uint, void>)Glfw.GetProcAddress("glBindBufferBase"u8);
            var uniform1i = (delegate* unmanaged[Cdecl]<uint, int, int, void>)Glfw.GetProcAddress("glProgramUniform1i"u8);
            EngineLog.Engine($"bindless stress: up to {count} textures of {size}x{size} RGBA8, at most {budget >> 20} MiB; {GlApi.GetString(RendererName)}, {GlApi.GetString(GlConst.Version)}");

            const uint DebugOutputState = 0x92E0, DebugOutputSync = 0x8242;
            N.glEnable((GlCapability)DebugOutputState);
            N.glEnable((GlCapability)DebugOutputSync);
            N.glDebugMessageCallback((nint*)(delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, byte*, void*, void>)&OnMessage, null);
            while (getError() != 0) { }

            uint sampler = GlSamplers.Default;
            var texels = new byte[size * size * 4];
            Array.Fill(texels, (byte)255);
            var names = new List<uint>();
            var handles = new List<ulong>();
            var good = new List<ulong>();
            long bytes = 0;
            int firstFailure = -1, failures = 0;
            // The check: one fragment of a 1x1 R32F target samples every handle reported resident.
            uint vs = GlApi.CompileShader(VertexShader, Vertex, out var vlog), fs = GlApi.CompileShader(FragmentShader, Fragment, out var flog);
            uint program = vs != 0 && fs != 0 ? GlApi.LinkProgram(vs, fs, 0, out var plog) : 0;
            if (program == 0) { EngineLog.Engine($"bindless stress: no check program: {vlog} {flog}"); }
            int countLocation = program != 0 ? GlApi.UniformLocation(program, "count") : -1;
            uint target = GlApi.GenTexture();
            GlApi.BindTexture(TexTarget2D, target);
            N.glTexStorage2D(TexTarget2D, 1, R32F, 1, 1);
            uint fbo = GlApi.GenFramebuffer();
            GlApi.BindFramebuffer(Framebuffer, fbo);
            N.glFramebufferTexture2D(Framebuffer, ColorAttachment0, TexTarget2D, target, 0);
            uint ssbo = GlApi.GenBuffer();
            var started = System.Diagnostics.Stopwatch.StartNew();

            void Check(string when)
            {
                if (program == 0 || bytes > checkLimit) return;
                const uint ShaderStorage = 0x90D2;
                GlApi.BindBuffer(ShaderStorage, ssbo);
                var data = good.ToArray();
                fixed (ulong* p = data) N.glBufferData(ShaderStorage, (nint)Math.Max(8, data.Length * 8), p, StreamDraw);
                bindBase(ShaderStorage, 0, ssbo);
                GlApi.BindFramebuffer(Framebuffer, fbo);
                N.glViewport(0, 0, 1, 1);
                N.glDisable((GlCapability)Blend);
                N.glDisable((GlCapability)DepthTest);
                N.glDisable((GlCapability)ScissorTest);
                N.glUseProgram(new GlProgram(program));
                uniform1i(program, countLocation, data.Length);
                N.glDrawArrays(Triangles, 0, 3);
                float bad = -1;
                N.glReadPixels(0, 0, 1, 1, RedFormat, Float, &bad);
                uint e = getError();
                EngineLog.Engine($"bindless stress: {when}: sampled {data.Length} resident handles in one draw: {bad} did not read white; error 0x{e:X}; {messageCount} debug messages; {VideoMemory()}");
            }

            for (int i = 0; i < count; i++)
            {
                if (bytes + texels.Length > budget) { EngineLog.Engine($"bindless stress: stopped at the {budget >> 20} MiB budget"); break; }
                uint id = GlApi.GenTexture();
                GlApi.BindTexture(TexTarget2D, id);
                N.glTexStorage2D(TexTarget2D, 1, Rgba8, size, size);
                N.glPixelStorei(UnpackAlignment, 1);
                fixed (byte* p = texels) N.glTexSubImage2D(TexTarget2D, 0, 0, 0, size, size, Rgba, UnsignedByte, p);
                names.Add(id);
                bytes += texels.Length;
                int messagesBefore = messageCount;
                ulong h = getHandle(id, sampler);
                uint handleError = getError();
                if (h == 0) { EngineLog.Engine($"bindless stress: texture {i}: no handle (error 0x{handleError:X})"); break; }
                resident(h);
                uint error = getError();
                bool reported = isResident(h) != 0;
                uint isError = getError();
                handles.Add(h);
                bool failed = error != 0 || !reported || isError != 0 || messageCount != messagesBefore;
                if (failed)
                {
                    failures++;
                    if (firstFailure < 0)
                    {
                        firstFailure = i;
                        EngineLog.Engine($"bindless stress: first failure at texture {i} ({handles.Count} resident handles, {bytes >> 20} MiB): MakeTextureHandleResident error 0x{error:X}, IsTextureHandleResident {reported} (error 0x{isError:X}), {messageCount - messagesBefore} new debug messages; after {started.ElapsedMilliseconds} ms; {VideoMemory()}");
                    }
                    if (failures >= 16) break;
                    continue;
                }
                good.Add(h);
                if (handles.Count % 256 == 0)
                {
                    finish();
                    if (handles.Count % 2048 == 0) EngineLog.Engine($"bindless stress: {handles.Count} resident handles ({bytes >> 20} MiB), {started.ElapsedMilliseconds} ms; {VideoMemory()}");
                    if (handles.Count % 1024 == 0) Check($"at {handles.Count}");
                }
            }
            finish();
            EngineLog.Engine($"bindless stress: made {handles.Count} handles resident ({bytes >> 20} MiB), {good.Count} reported resident without an error, first failure {(firstFailure < 0 ? "none" : firstFailure.ToString())}, {failures} failures, {messageCount} debug messages in {started.ElapsedMilliseconds} ms");
            Check("end");
            foreach (var m in messages) EngineLog.Engine($"bindless stress: debug message: {m}");
            foreach (var h in good) nonResident(h);
            foreach (var id in names) GlApi.DeleteTexture(id);
            GlApi.DeleteTexture(target);
            GlApi.DeleteFramebuffer(fbo);
            GlApi.DeleteBuffer(ssbo);
            if (program != 0) N.glDeleteProgram(new GlProgram(program));
            if (vs != 0) N.glDeleteShader(new GlShader(vs));
            if (fs != 0) N.glDeleteShader(new GlShader(fs));
            GlApi.BindFramebuffer(Framebuffer, 0);
            finish();
            uint last = getError();
            EngineLog.Engine($"bindless stress: cleaned up (error 0x{last:X}, {messageCount} debug messages); {VideoMemory()}");
            N.glDisable((GlCapability)DebugOutputSync);
            N.glDisable((GlCapability)DebugOutputState);
            N.glDebugMessageCallback(null, null);
        }

        static string VideoMemory()
        {
            if (!GlPrograms.Extensions.Contains("GL_NVX_gpu_memory_info")) return "";
            int free = 0, evictions = 0;
            N.glGetIntegerv(0x9049, &free);
            N.glGetIntegerv(0x904A, &evictions);
            return $"{free >> 10} MiB free, {evictions} evictions";
        }
    }
}
