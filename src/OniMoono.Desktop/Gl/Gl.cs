using System;
using System.Runtime.InteropServices;
using System.Text;
using Brutal.OpenGlApi;
using N = Brutal.OpenGlApi.OpenGl.Native;

namespace OniMoono.Desktop.Gl
{
    /// OpenGL enumerants used by the device (values from the GL 4.5 core specification).
    internal static class GlConst
    {
        public const uint TexTarget2D = 0x0DE1, TexTarget3D = 0x806F, TexTarget2DArray = 0x8C1A, TexTarget2DMultisample = 0x9100, TexTargetCubeMap = 0x8513, TexCubeMapPositiveX = 0x8515;
        public const uint TexTarget1D = 0x0DE0, TexTarget1DArray = 0x8C18, TexTargetRectangle = 0x84F5, TexTargetBuffer = 0x8C2A, TexTargetCubeMapArray = 0x9009, TexTarget2DMultisampleArray = 0x9102;
        public const uint Texture0 = 0x84C0;
        public const uint TextureMinFilter = 0x2801, TextureMagFilter = 0x2800, TextureWrapS = 0x2802, TextureWrapT = 0x2803, TextureWrapR = 0x8072;
        public const uint TextureMaxLevel = 0x813D, TextureBaseLevel = 0x813C, TextureLodBias = 0x8501, TextureMaxAnisotropy = 0x84FE;
        public const uint TextureSwizzleR = 0x8E42, TextureSwizzleG = 0x8E43, TextureSwizzleB = 0x8E44, TextureSwizzleA = 0x8E45;
        public const uint Nearest = 0x2600, Linear = 0x2601, NearestMipmapNearest = 0x2700, LinearMipmapNearest = 0x2701, NearestMipmapLinear = 0x2702, LinearMipmapLinear = 0x2703;
        public const uint Repeat = 0x2901, ClampToEdge = 0x812F, MirroredRepeat = 0x8370, MirrorClampToEdge = 0x8743;
        public const uint Red = 0x1903, Green = 0x1904, Blue = 0x1905, Alpha = 0x1906, Zero = 0, One = 1;
        public const uint Rgb = 0x1907, Rgba = 0x1908, Bgra = 0x80E1, Rg = 0x8227, RedFormat = 0x1903, DepthComponent = 0x1902, DepthStencil = 0x84F9;
        public const uint UnsignedByte = 0x1401, Byte = 0x1400, UnsignedShort = 0x1403, Short = 0x1402, UnsignedInt = 0x1405, Int = 0x1404, Float = 0x1406, HalfFloat = 0x140B;
        public const uint UnsignedShort4444 = 0x8033, UnsignedShort4444Rev = 0x8365, UnsignedShort565 = 0x8363, UnsignedShort1555Rev = 0x8366, UnsignedInt248 = 0x84FA;
        public const uint R8 = 0x8229, Rg8 = 0x822B, Rgb8 = 0x8051, Rgba8 = 0x8058, Rgba4 = 0x8056, Rgb565 = 0x8D62, Rgb5A1 = 0x8057;
        public const uint R16 = 0x822A, Rg16 = 0x822C, Rgb16 = 0x8054, Rgba16 = 0x805B;
        public const uint R16F = 0x822D, Rg16F = 0x822F, Rgba16F = 0x881A, R32F = 0x822E, Rg32F = 0x8230, Rgba32F = 0x8814;
        public const uint R11G11B10F = 0x8C3A, Rgb9E5 = 0x8C3D, Rgb10A2 = 0x8059, R32I = 0x8235, Rg32I = 0x823B, Rgba32I = 0x8D82, Rgba16UI = 0x8D76;
        public const uint DepthComponent16 = 0x81A5, Depth24Stencil8 = 0x88F0, Depth32FStencil8 = 0x8CAD, DepthComponent32F = 0x8CAC;
        public const uint CompressedRgbS3tcDxt1 = 0x83F0, CompressedRgbaS3tcDxt5 = 0x83F3, CompressedRedRgtc1 = 0x8DBB, CompressedRgRgtc2 = 0x8DBD;
        public const uint CompressedRgbaBptcUnorm = 0x8E8C, CompressedRgbBptcUnsignedFloat = 0x8E8F;
        public const uint UnpackAlignment = 0x0CF5, PackAlignment = 0x0D05, UnpackRowLength = 0x0CF2;
        public const uint ArrayBuffer = 0x8892, ElementArrayBuffer = 0x8893, UniformBuffer = 0x8A11, StaticDraw = 0x88E4, DynamicDraw = 0x88E8, StreamDraw = 0x88E0;
        public const uint Framebuffer = 0x8D40, ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9, Renderbuffer = 0x8D41;
        public const uint ColorAttachment0 = 0x8CE0, DepthAttachment = 0x8D00, DepthStencilAttachment = 0x821A, FramebufferComplete = 0x8CD5;
        public const uint BackLeft = 0x0402, Back = 0x0405, NoneBuffer = 0;
        public const uint ColorBufferBit = 0x4000, DepthBufferBit = 0x100, StencilBufferBit = 0x400;
        public const uint Triangles = 0x0004, TriangleStrip = 0x0005, Lines = 0x0001, LineStrip = 0x0003, Points = 0x0000;
        public const uint DepthTest = 0x0B71, Blend = 0x0BE2, CullFace = 0x0B44, StencilTest = 0x0B90, ScissorTest = 0x0C11, PolygonOffsetFill = 0x8037;
        public const uint SampleAlphaToCoverage = 0x809E, DepthClamp = 0x864F, Multisample = 0x809D, DebugOutput = 0x92E0, DebugOutputSynchronous = 0x8242;
        public const uint Cw = 0x0900, FrontFace = 0x0404, BackFace = 0x0405, FrontAndBack = 0x0408;
        public const uint FuncAdd = 0x8006, FuncSubtract = 0x800A, FuncReverseSubtract = 0x800B, Min = 0x8007, Max = 0x8008;
        public const uint SrcColor = 0x0300, OneMinusSrcColor = 0x0301, SrcAlpha = 0x0302, OneMinusSrcAlpha = 0x0303, DstAlpha = 0x0304, OneMinusDstAlpha = 0x0305;
        public const uint DstColor = 0x0306, OneMinusDstColor = 0x0307, SrcAlphaSaturate = 0x0308;
        public const uint Never = 0x0200, Less = 0x0201, Equal = 0x0202, Lequal = 0x0203, Greater = 0x0204, NotEqual = 0x0205, Gequal = 0x0206, Always = 0x0207;
        public const uint Keep = 0x1E00, Replace = 0x1E01, Incr = 0x1E02, Decr = 0x1E03, Invert = 0x150A, IncrWrap = 0x8507, DecrWrap = 0x8508;
        public const uint CompileStatus = 0x8B81, LinkStatus = 0x8B82, InfoLogLength = 0x8B84, ActiveUniforms = 0x8B86, ActiveAttributes = 0x8B89, ActiveUniformBlocks = 0x8A36;
        public const uint UniformBlockIndex = 0x8A3A, UniformOffset = 0x8A3B, UniformArrayStride = 0x8A3C, UniformMatrixStride = 0x8A3D, UniformType = 0x8A37, UniformSize = 0x8A38;
        public const uint UniformBlockDataSize = 0x8A40;
        public const uint FloatType = 0x1406, FloatVec2 = 0x8B50, FloatVec3 = 0x8B51, FloatVec4 = 0x8B52, IntType = 0x1404, IntVec2 = 0x8B53, IntVec3 = 0x8B54, IntVec4 = 0x8B55;
        public const uint UnsignedIntType = 0x1405, UintVec2 = 0x8DC6, UintVec3 = 0x8DC7, UintVec4 = 0x8DC8, BoolType = 0x8B56, BoolVec2 = 0x8B57, BoolVec3 = 0x8B58, BoolVec4 = 0x8B59, FloatMat4 = 0x8B5C;
        // The sampler types (uniform type tokens, OpenGL 4.6 table 7.3; glcorearb.h).
        public const uint Sampler1D = 0x8B5D, Sampler2D = 0x8B5E, Sampler3D = 0x8B5F, SamplerCube = 0x8B60, Sampler1DShadow = 0x8B61, Sampler2DShadow = 0x8B62;
        public const uint Sampler2DRect = 0x8B63, Sampler2DRectShadow = 0x8B64, Sampler1DArray = 0x8DC0, Sampler2DArray = 0x8DC1, SamplerBuffer = 0x8DC2;
        public const uint Sampler1DArrayShadow = 0x8DC3, Sampler2DArrayShadow = 0x8DC4, SamplerCubeShadow = 0x8DC5;
        public const uint IntSampler1D = 0x8DC9, IntSampler2D = 0x8DCA, IntSampler3D = 0x8DCB, IntSamplerCube = 0x8DCC, IntSampler2DRect = 0x8DCD, IntSampler1DArray = 0x8DCE, IntSampler2DArray = 0x8DCF, IntSamplerBuffer = 0x8DD0;
        public const uint UnsignedIntSampler1D = 0x8DD1, UnsignedIntSampler2D = 0x8DD2, UnsignedIntSampler3D = 0x8DD3, UnsignedIntSamplerCube = 0x8DD4;
        public const uint UnsignedIntSampler2DRect = 0x8DD5, UnsignedIntSampler1DArray = 0x8DD6, UnsignedIntSampler2DArray = 0x8DD7, UnsignedIntSamplerBuffer = 0x8DD8;
        public const uint SamplerCubeMapArray = 0x900C, SamplerCubeMapArrayShadow = 0x900D, IntSamplerCubeMapArray = 0x900E, UnsignedIntSamplerCubeMapArray = 0x900F;
        public const uint Sampler2DMultisample = 0x9108, IntSampler2DMultisample = 0x9109, UnsignedIntSampler2DMultisample = 0x910A;
        public const uint Sampler2DMultisampleArray = 0x910B, IntSampler2DMultisampleArray = 0x910C, UnsignedIntSampler2DMultisampleArray = 0x910D;
        public const uint Vendor = 0x1F00, RendererName = 0x1F01, Version = 0x1F02, ShadingLanguageVersion = 0x8B8C, Extensions = 0x1F03, NumExtensions = 0x821D;
        public const uint MaxTextureSize = 0x0D33, MaxDrawBuffers = 0x8824, MaxTextureImageUnits = 0x8872, MaxCombinedTextureImageUnits = 0x8B4D, MaxSamples = 0x8D57;
        public const uint UniformBufferOffsetAlignment = 0x8A34;
        public const uint VertexShader = 0x8B31, FragmentShader = 0x8B30, GeometryShader = 0x8DD9;
    }

    /// Typed calls over the loaded GL entry points.
    internal static unsafe class GlApi
    {
        public static void Enable(uint cap, bool on)
        {
            if (on) N.glEnable((GlCapability)cap);
            else N.glDisable((GlCapability)cap);
        }

        public static string GetString(uint name)
        {
            var p = N.glGetString(name);
            return p == null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";
        }

        public static string GetStringi(uint name, uint index)
        {
            var p = N.glGetStringi(name, index);
            return p == null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";
        }

        public static int GetInteger(uint name)
        {
            int v = 0;
            N.glGetIntegerv(name, &v);
            return v;
        }

        public static uint GenTexture()
        {
            Brutal.OpenGlApi.GlTexture t = default;
            N.glGenTextures(1, &t);
            return t.Id;
        }

        public static void DeleteTexture(uint id)
        {
            var t = new Brutal.OpenGlApi.GlTexture(id);
            N.glDeleteTextures(1, &t);
        }

        public static uint GenBuffer()
        {
            GlBuffer b = default;
            N.glGenBuffers(1, &b);
            return b.Id;
        }

        /// A buffer object usable with the direct-state-access calls right away.
        public static uint CreateBuffer()
        {
            GlBuffer b = default;
            N.glCreateBuffers(1, &b);
            return b.Id;
        }

        public static void DeleteBuffer(uint id)
        {
            var b = new GlBuffer(id);
            N.glDeleteBuffers(1, &b);
        }

        public static uint GenFramebuffer()
        {
            GlFramebuffer f = default;
            N.glGenFramebuffers(1, &f);
            return f.Id;
        }

        public static void DeleteFramebuffer(uint id)
        {
            var f = new GlFramebuffer(id);
            N.glDeleteFramebuffers(1, &f);
        }

        public static uint GenRenderbuffer()
        {
            GlRenderbuffer r = default;
            N.glGenRenderbuffers(1, &r);
            return r.Id;
        }

        public static void DeleteRenderbuffer(uint id)
        {
            var r = new GlRenderbuffer(id);
            N.glDeleteRenderbuffers(1, &r);
        }

        public static void BindFramebuffer(uint target, uint id) => N.glBindFramebuffer(target, new GlFramebuffer(id));
        public static void BindBuffer(uint target, uint id) => N.glBindBuffer(target, new GlBuffer(id));
        public static void BindTexture(uint target, uint id) => N.glBindTexture(target, id);

        public static void BufferData<T>(uint target, ReadOnlySpan<T> data, uint usage) where T : unmanaged
        {
            fixed (T* p = data) N.glBufferData(target, (nint)(data.Length * sizeof(T)), p, usage);
        }

        public static void BufferSubData<T>(uint target, nint offset, ReadOnlySpan<T> data) where T : unmanaged
        {
            fixed (T* p = data) N.glBufferSubData(target, offset, (nint)(data.Length * sizeof(T)), p);
        }

        public static uint CompileShader(uint type, string source, out string log)
        {
            var shader = N.glCreateShader((GlShaderType)type);
            var bytes = Encoding.UTF8.GetBytes(source);
            fixed (byte* p = bytes)
            {
                byte* ptr = p;
                int length = bytes.Length;
                N.glShaderSource(shader, 1, &ptr, &length);
            }
            N.glCompileShader(shader);
            int ok = 0, logLength = 0;
            N.glGetShaderiv(shader, GlConst.CompileStatus, &ok);
            N.glGetShaderiv(shader, GlConst.InfoLogLength, &logLength);
            log = "";
            if (logLength > 1)
            {
                var buffer = new byte[logLength];
                fixed (byte* b = buffer) N.glGetShaderInfoLog(shader, logLength, null, b);
                log = Encoding.UTF8.GetString(buffer, 0, logLength - 1);
            }
            if (ok == 0)
            {
                N.glDeleteShader(shader);
                return 0;
            }
            return shader.Id;
        }

        public static uint LinkProgram(uint vertex, uint fragment, uint geometry, out string log)
        {
            var program = N.glCreateProgram();
            N.glAttachShader(program, new GlShader(vertex));
            N.glAttachShader(program, new GlShader(fragment));
            if (geometry != 0) N.glAttachShader(program, new GlShader(geometry));
            N.glLinkProgram(program);
            N.glDetachShader(program, new GlShader(vertex));
            N.glDetachShader(program, new GlShader(fragment));
            if (geometry != 0) N.glDetachShader(program, new GlShader(geometry));
            int ok = 0, logLength = 0;
            N.glGetProgramiv(program, GlConst.LinkStatus, &ok);
            N.glGetProgramiv(program, GlConst.InfoLogLength, &logLength);
            log = "";
            if (logLength > 1)
            {
                var buffer = new byte[logLength];
                fixed (byte* b = buffer) N.glGetProgramInfoLog(program, logLength, null, b);
                log = Encoding.UTF8.GetString(buffer, 0, logLength - 1);
            }
            if (ok == 0)
            {
                N.glDeleteProgram(program);
                return 0;
            }
            return program.Id;
        }

        public static int GetProgram(uint program, uint name)
        {
            int v = 0;
            N.glGetProgramiv(new GlProgram(program), name, &v);
            return v;
        }

        public static int AttribLocation(uint program, string name)
        {
            var bytes = Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* p = bytes) return N.glGetAttribLocation(new GlProgram(program), p);
        }

        public static int UniformLocation(uint program, string name)
        {
            var bytes = Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* p = bytes) return N.glGetUniformLocation(new GlProgram(program), p);
        }

        /// Name, GL type and array size of active uniform i.
        public static (string Name, uint Type, int Size) ActiveUniform(uint program, uint index)
        {
            var buffer = stackalloc byte[512];
            int length = 0, size = 0;
            GlEnum type = default;
            N.glGetActiveUniform(new GlProgram(program), index, 512, &length, &size, &type, buffer);
            return (Encoding.UTF8.GetString(buffer, length), type.Id, size);
        }

        public static int ActiveUniformParameter(uint program, uint index, uint pname)
        {
            int v = 0;
            N.glGetActiveUniformsiv(new GlProgram(program), 1, &index, pname, &v);
            return v;
        }

        public static string UniformBlockName(uint program, uint block)
        {
            var buffer = stackalloc byte[512];
            int length = 0;
            N.glGetActiveUniformBlockName(new GlProgram(program), block, 512, &length, buffer);
            return Encoding.UTF8.GetString(buffer, length);
        }

        public static int UniformBlockParameter(uint program, uint block, uint pname)
        {
            int v = 0;
            N.glGetActiveUniformBlockiv(new GlProgram(program), block, pname, &v);
            return v;
        }
    }
}
