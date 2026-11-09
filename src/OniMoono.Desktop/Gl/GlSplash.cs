using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Brutal.OpenGlApi;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using static OniMoono.Desktop.Gl.GlConst;
using N = Brutal.OpenGlApi.OpenGl.Native;

namespace OniMoono.Desktop.Gl
{
    /// The player's own loading splash (--splash moo): Gassy Moo comets falling over whatever the
    /// window last showed, drawn by the presenter thread on the window's context, so they keep
    /// moving while the main thread and the GL thread are busy loading. Game frames are not
    /// changed: the comets are drawn into the window after a frame is copied there. The rain
    /// fades out once frames arrive steadily (the main menu), and does not come back.
    ///
    /// The comets follow onimaxxing.com's moo rain, after the game's GassyMooComet: one of two
    /// trajectories 15° off vertical (mirrored for the leftward one), the fall_loop at its
    /// authored rate, each comet at its own phase.
    internal sealed unsafe class GlSplash
    {
        readonly Task<SplashArt> art;
        readonly Stopwatch clock = Stopwatch.StartNew();
        uint program, texture, buffer, vertexArray;
        int sizeLocation, flipLocation;
        bool failed;

        // Steady frames: this many in a row, each within SteadyGap of the one before.
        const int SteadyFrames = 30;
        const double SteadyGap = 0.1, FadeSeconds = 0.6, MaxSeconds = 300;
        double lastFrame = -1, fadeStart = -1;
        int steady;

        public GlSplash(Task<SplashArt> art) => this.art = art;

        /// The rain is still to be drawn (it has not finished fading out).
        public bool Active => !failed && (fadeStart < 0 || Now - fadeStart < FadeSeconds) && Now < MaxSeconds;

        double Now => clock.Elapsed.TotalSeconds;

        /// A game frame was presented.
        public void GameFrame()
        {
            double now = Now;
            steady = lastFrame >= 0 && now - lastFrame < SteadyGap ? steady + 1 : 0;
            lastFrame = now;
            if (steady >= SteadyFrames && fadeStart < 0)
            {
                fadeStart = now;
                EngineLog.Engine($"splash: fading out after {now:F1} s");
            }
        }

        struct Comet
        {
            public float X, Size, Delay, Duration;
            public int Phase;
            public bool FlipX;
        }

        Comet[] comets;

        static Comet[] Roll(int count)
        {
            var random = new Random();
            var result = new Comet[count];
            for (int i = 0; i < count; i++)
                result[i] = new Comet
                {
                    X = (float)(random.NextDouble() * 1.1 - 0.05),
                    Size = (float)(0.3 + random.NextDouble() * 0.25),
                    // The first comets come in at once; the rest staggered.
                    Delay = i < 2 ? (float)(random.NextDouble() * 0.5) : (float)(random.NextDouble() * 4),
                    Duration = (float)(3.5 + random.NextDouble() * 2.5),
                    Phase = random.Next(1000),
                    FlipX = random.Next(2) == 0,
                };
            return result;
        }

        bool Ready()
        {
            if (failed) return false;
            if (program != 0) return true;
            if (!art.IsCompleted) return false;
            if (art.IsFaulted || art.Result == null)
            {
                failed = true;
                EngineLog.Engine("splash: no Gassy Moo art in the game data" + (art.Exception != null ? ": " + art.Exception.InnerException?.Message : ""));
                return false;
            }
            var a = art.Result;
            const string vs = "#version 330 core\nlayout(location = 0) in vec2 pos;\nlayout(location = 1) in vec3 uva;\nuniform vec2 size;\nuniform float flip;\nout vec3 v;\nvoid main() { v = uva; gl_Position = vec4(pos.x / size.x * 2.0 - 1.0, (1.0 - pos.y / size.y * 2.0) * flip, 0.0, 1.0); }\n";
            const string fs = "#version 330 core\nin vec3 v;\nuniform sampler2D atlas;\nout vec4 color;\nvoid main() { vec4 c = texture(atlas, v.xy); color = c * v.z; }\n";
            uint v = GlApi.CompileShader(VertexShader, vs, out var vlog), f = GlApi.CompileShader(FragmentShader, fs, out var flog);
            program = v != 0 && f != 0 ? GlApi.LinkProgram(v, f, 0, out _) : 0;
            if (v != 0) N.glDeleteShader(new GlShader(v));
            if (f != 0) N.glDeleteShader(new GlShader(f));
            if (program == 0)
            {
                failed = true;
                EngineLog.Engine($"splash: program failed: {vlog} {flog}");
                return false;
            }
            sizeLocation = GlApi.UniformLocation(program, "size");
            flipLocation = GlApi.UniformLocation(program, "flip");
            texture = GlApi.GenTexture();
            N.glBindTexture(TexTarget2D, texture);
            // Premultiplied: the atlas's transparent texels are white, which filtering and the
            // smaller mips would otherwise bleed into the sprites' soft edges as a pale fringe.
            var texels = (byte[])a.Rgba.Clone();
            for (int i = 0; i < texels.Length; i += 4)
            {
                int alpha = texels[i + 3];
                texels[i] = (byte)((texels[i] * alpha + 127) / 255);
                texels[i + 1] = (byte)((texels[i + 1] * alpha + 127) / 255);
                texels[i + 2] = (byte)((texels[i + 2] * alpha + 127) / 255);
            }
            fixed (byte* p = texels) N.glTexImage2D(TexTarget2D, 0, (int)Rgba8, a.Width, a.Height, 0, Rgba, UnsignedByte, p);
            N.glGenerateMipmap(TexTarget2D);
            N.glTexParameteri(TexTarget2D, TextureMinFilter, (int)LinearMipmapLinear);
            N.glTexParameteri(TexTarget2D, TextureMagFilter, (int)Linear);
            GlVertexArray vao = default;
            N.glGenVertexArrays(1, &vao);
            vertexArray = vao.Id;
            buffer = GlApi.GenBuffer();
            N.glBindVertexArray(vao);
            GlApi.BindBuffer(ArrayBuffer, buffer);
            N.glEnableVertexAttribArray(0);
            N.glVertexAttribPointer(0, 2, Float, false, 20, (void*)0);
            N.glEnableVertexAttribArray(1);
            N.glVertexAttribPointer(1, 3, Float, false, 20, (void*)8);
            N.glBindVertexArray(default);
            comets = Roll(8);
            clock.Restart();
            lastFrame = fadeStart = -1;
            steady = 0;
            EngineLog.Engine($"splash: Gassy Moo rain ({a.Frames.Length} frames at {a.Rate} fps)");
            return true;
        }

        readonly List<float> vertices = new();

        // The bar's colours, from the game's menus: its dark purple panels and pink highlights.
        static readonly (float R, float G, float B) Track = (0.20f, 0.18f, 0.27f), Fill = (0.83f, 0.35f, 0.55f);

        /// Start-up done so far (StartupProgress), as a thin bar low in the window, over a dark
        /// track; gone once the rain fades. `flip`: the framebuffer's rows run top-down.
        static void DrawBar(int width, int height, float fade, bool flip)
        {
            if (fade < 1f) return;
            void Scissor(int sx, int sy, int sw, int sh) => N.glScissor(sx, flip ? height - sy - sh : sy, sw, sh);
            float progress = StartupProgress.Value;
            int barWidth = Math.Max(120, width * 2 / 5), barHeight = Math.Max(4, height / 160);
            int x = (width - barWidth) / 2, y = Math.Max(barHeight * 2, height / 14);
            GlApi.Enable(ScissorTest, true);
            Scissor(x - 2, y - 2, barWidth + 4, barHeight + 4);
            N.glClearColor(Track.R, Track.G, Track.B, 1f);
            N.glClear(ColorBufferBit);
            // The fill's edge column is part-covered, so the edge moves on smoothly even when
            // slower than a pixel a frame.
            float edge = barWidth * progress;
            int filled = (int)edge;
            if (filled > 0)
            {
                Scissor(x, y, filled, barHeight);
                N.glClearColor(Fill.R, Fill.G, Fill.B, 1f);
                N.glClear(ColorBufferBit);
            }
            float cover = edge - filled;
            if (filled < barWidth && cover > 0)
            {
                Scissor(x + filled, y, 1, barHeight);
                N.glClearColor(Track.R + (Fill.R - Track.R) * cover, Track.G + (Fill.G - Track.G) * cover, Track.B + (Fill.B - Track.B) * cover, 1f);
                N.glClear(ColorBufferBit);
            }
            GlApi.Enable(ScissorTest, false);
        }

        /// Draws the comets into the window's framebuffer (bound), `width`×`height` pixels.
        /// `flip`: its rows run top-down (the DXGI target's).
        /// The presenter now draws with another context: the vertex array (not shared between
        /// contexts) is made anew in it; the program, texture and buffer are shared.
        public void ContextReplaced()
        {
            if (vertexArray == 0) return;
            GlVertexArray vao = default;
            N.glGenVertexArrays(1, &vao);
            vertexArray = vao.Id;
            N.glBindVertexArray(vao);
            GlApi.BindBuffer(ArrayBuffer, buffer);
            N.glEnableVertexAttribArray(0);
            N.glVertexAttribPointer(0, 2, Float, false, 20, (void*)0);
            N.glEnableVertexAttribArray(1);
            N.glVertexAttribPointer(1, 3, Float, false, 20, (void*)8);
            N.glBindVertexArray(default);
        }

        public void Draw(int width, int height, bool flip = false)
        {
            if (!Ready() || !Active || width <= 0 || height <= 0) return;
            var a = art.Result;
            double now = Now;
            float fade = fadeStart < 0 ? 1f : (float)Math.Max(0, 1 - (now - fadeStart) / FadeSeconds);
            float bw = a.Bounds.width, bh = a.Bounds.height, cx = a.Bounds.center.x, cy = a.Bounds.center.y;
            float drift = MathF.Tan(15f * MathF.PI / 180f);
            vertices.Clear();
            foreach (var c in comets)
            {
                double local = now - c.Delay;
                if (local < 0) continue;
                float progress = (float)(local % c.Duration / c.Duration);
                float alpha = fade * (progress < 0.08f ? progress / 0.08f : progress > 0.92f ? (1 - progress) / 0.08f : 1);
                float s = c.Size * height / Math.Max(bw, bh);
                float cometH = bh * s;
                float startY = -cometH / 2 - 20, endY = height + cometH / 2;
                float y = startY + (endY - startY) * progress;
                float dir = c.FlipX ? -1 : 1;
                float x = c.X * width + drift * (endY - startY) * progress * dir;
                var frame = a.Frames[(int)((long)(local * a.Rate) + c.Phase) % a.Frames.Length];
                foreach (var q in frame)
                {
                    void V(UnityEngine.Vector2 p, UnityEngine.Vector2 uv)
                    {
                        vertices.Add(x + s * dir * (p.x - cx));
                        vertices.Add(y + s * (p.y - cy));
                        vertices.Add(uv.x);
                        vertices.Add(uv.y);
                        vertices.Add(alpha * q.Alpha);
                    }
                    V(q.P0, q.Uv0); V(q.P1, q.Uv1); V(q.P2, q.Uv2);
                    V(q.P0, q.Uv0); V(q.P2, q.Uv2); V(q.P3, q.Uv3);
                }
            }
            N.glViewport(0, 0, width, height);
            DrawBar(width, height, fade, flip);
            if (vertices.Count == 0) return;
            GlApi.Enable(Blend, true);
            GlApi.Enable(DepthTest, false);
            GlApi.Enable(CullFace, false);
            GlApi.Enable(ScissorTest, false);
            GlApi.Enable(StencilTest, false);
            N.glBlendFuncSeparate(One, OneMinusSrcAlpha, One, OneMinusSrcAlpha);
            N.glUseProgram(new GlProgram(program));
            N.glUniform2f(sizeLocation, width, height);
            N.glUniform1f(flipLocation, flip ? -1f : 1f);
            N.glActiveTexture(Texture0);
            N.glBindTexture(TexTarget2D, texture);
            N.glBindVertexArray(new GlVertexArray(vertexArray));
            GlApi.BindBuffer(ArrayBuffer, buffer);
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices);
            GlApi.BufferData<float>(ArrayBuffer, span, StreamDraw);
            N.glDrawArrays(Triangles, 0, vertices.Count / 5);
            N.glBindVertexArray(default);
            GlApi.Enable(Blend, false);
        }
    }
}
