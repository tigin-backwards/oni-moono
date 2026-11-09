using System.Collections.Generic;
using System.Threading.Tasks;
using Brutal.OpenGlApi;
using OniMoono.Engine.Rendering;
using static OniMoono.Desktop.Gl.GlConst;
using N = Brutal.OpenGlApi.OpenGl.Native;

namespace OniMoono.Desktop.Gl
{
    /// The loading splash (SplashRain) drawn by the OpenGL presenter thread on the window's
    /// context.
    internal sealed unsafe class GlSplash
    {
        readonly SplashRain rain;
        uint program, texture, buffer, vertexArray;
        int sizeLocation, flipLocation;

        public GlSplash(Task<SplashArt> art) => rain = new SplashRain(art);

        /// The rain is still to be drawn (it has not finished fading out).
        public bool Active => rain.Active;

        /// A game frame was presented.
        public void GameFrame() => rain.GameFrame();

        bool Ready()
        {
            if (program != 0) return true;
            var a = rain.Art;
            if (a == null) return false;
            const string vs = "#version 330 core\nlayout(location = 0) in vec2 pos;\nlayout(location = 1) in vec3 uva;\nuniform vec2 size;\nuniform float flip;\nout vec3 v;\nvoid main() { v = uva; gl_Position = vec4(pos.x / size.x * 2.0 - 1.0, (1.0 - pos.y / size.y * 2.0) * flip, 0.0, 1.0); }\n";
            const string fs = "#version 330 core\nin vec3 v;\nuniform sampler2D atlas;\nout vec4 color;\nvoid main() { vec4 c = texture(atlas, v.xy); color = c * v.z; }\n";
            uint v = GlApi.CompileShader(VertexShader, vs, out var vlog), f = GlApi.CompileShader(FragmentShader, fs, out var flog);
            program = v != 0 && f != 0 ? GlApi.LinkProgram(v, f, 0, out _) : 0;
            if (v != 0) N.glDeleteShader(new GlShader(v));
            if (f != 0) N.glDeleteShader(new GlShader(f));
            if (program == 0)
            {
                rain.Fail($"program failed: {vlog} {flog}");
                return false;
            }
            sizeLocation = GlApi.UniformLocation(program, "size");
            flipLocation = GlApi.UniformLocation(program, "flip");
            texture = GlApi.GenTexture();
            N.glBindTexture(TexTarget2D, texture);
            var texels = SplashRain.PremultipliedTexels(a);
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
            rain.Start();
            return true;
        }

        readonly List<float> vertices = new();
        readonly List<SplashRain.BarRect> bar = new();

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
            float fade = rain.Vertices(width, height, vertices);
            N.glViewport(0, 0, width, height);
            SplashRain.Bar(width, height, fade, bar);
            if (bar.Count > 0)
            {
                GlApi.Enable(ScissorTest, true);
                foreach (var r in bar)
                {
                    N.glScissor(r.X, flip ? height - r.Y - r.Height : r.Y, r.Width, r.Height);
                    N.glClearColor(r.R, r.G, r.B, 1f);
                    N.glClear(ColorBufferBit);
                }
                GlApi.Enable(ScissorTest, false);
            }
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
