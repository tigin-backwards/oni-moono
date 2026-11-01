using N = Brutal.OpenGlApi.OpenGl.Native;
using Brutal.OpenGlApi;

namespace OniMoono.Desktop.Gl
{
    /// Object bindings of the render context (the GL thread's), remembered so that a bind
    /// that would not change anything is not issued: draws rebind their program, textures and
    /// buffers, and most of those binds repeat the previous draw's. Every bind of the render
    /// context goes through here; the presenter thread's context binds directly (GlApi).
    /// Deleting an object clears the bindings that name it, as GL does, so an id the driver
    /// hands out again is bound afresh.
    internal static unsafe class GlBindings
    {
        const uint Texture0 = 0x84C0;
        const uint ArrayBuffer = 0x8892, ElementArrayBuffer = 0x8893;
        const int Units = 32;
        /// Texture targets with a remembered binding per unit (2D, 2D array, 3D, cube map).
        static readonly uint[] targets = [0x0DE1, 0x8C1A, 0x806F, 0x8513];

        static uint program;
        static int activeUnit;
        static readonly uint[,] textures = new uint[Units, 4];
        static uint arrayBuffer, elementBuffer, vertexArray;

        /// Binds issued and skipped since the last TakeCounts (for --frame-stats).
        public static int ProgramBinds, TextureBinds, BufferBinds, VertexArrayBinds, Skipped;

        /// After code outside this class changed bindings (context creation): nothing is known.
        public static void Invalidate()
        {
            program = uint.MaxValue;
            activeUnit = -1;
            for (int u = 0; u < Units; u++)
                for (int t = 0; t < targets.Length; t++) textures[u, t] = uint.MaxValue;
            arrayBuffer = elementBuffer = vertexArray = uint.MaxValue;
        }

        static GlBindings() => Invalidate();

        public static void UseProgram(uint id)
        {
            if (program == id) { Skipped++; return; }
            program = id;
            ProgramBinds++;
            N.glUseProgram(new GlProgram(id));
        }

        public static void ActiveUnit(int unit)
        {
            if (activeUnit == unit) return;
            activeUnit = unit;
            N.glActiveTexture(Texture0 + (uint)unit);
        }

        /// Binds a texture on the active unit (uploads and parameter changes use this).
        public static void BindTexture(uint target, uint id)
        {
            if (activeUnit < 0) ActiveUnit(0);
            int t = TargetIndex(target);
            if (t >= 0 && activeUnit < Units)
            {
                if (textures[activeUnit, t] == id) { Skipped++; return; }
                textures[activeUnit, t] = id;
            }
            TextureBinds++;
            N.glBindTexture(target, id);
        }

        /// Binds a texture for sampling on a unit (glBindTextureUnit: the active unit stays).
        public static void BindTexture(int unit, uint target, uint id)
        {
            int t = TargetIndex(target);
            if (t >= 0 && unit < Units)
            {
                if (textures[unit, t] == id) { Skipped++; return; }
                textures[unit, t] = id;
            }
            TextureBinds++;
            N.glBindTextureUnit((uint)unit, id);
        }

        public static void BindBuffer(uint target, uint id)
        {
            if (target == ArrayBuffer)
            {
                if (arrayBuffer == id) { Skipped++; return; }
                arrayBuffer = id;
            }
            else if (target == ElementArrayBuffer)
            {
                if (elementBuffer == id) { Skipped++; return; }
                elementBuffer = id;
            }
            BufferBinds++;
            N.glBindBuffer(target, new GlBuffer(id));
        }

        /// The element buffer binding belongs to the vertex array, so it is unknown after a switch.
        public static void BindVertexArray(uint id)
        {
            if (vertexArray == id) { Skipped++; return; }
            vertexArray = id;
            elementBuffer = uint.MaxValue;
            VertexArrayBinds++;
            N.glBindVertexArray(new GlVertexArray(id));
        }

        public static void DeleteVertexArray(uint id)
        {
            if (vertexArray == id)
            {
                vertexArray = uint.MaxValue;
                elementBuffer = uint.MaxValue;
            }
            var a = new GlVertexArray(id);
            N.glDeleteVertexArrays(1, &a);
        }

        public static void DeleteTexture(uint id)
        {
            for (int u = 0; u < Units; u++)
                for (int t = 0; t < targets.Length; t++)
                    if (textures[u, t] == id) textures[u, t] = 0;
            GlApi.DeleteTexture(id);
        }

        public static void DeleteBuffer(uint id)
        {
            if (arrayBuffer == id) arrayBuffer = 0;
            if (elementBuffer == id) elementBuffer = 0;
            GlApi.DeleteBuffer(id);
        }

        /// A deleted program stays in use until another is bound; forgetting it makes the next
        /// UseProgram of a reused id bind it.
        public static void ForgetProgram(uint id)
        {
            if (program == id) program = uint.MaxValue;
        }

        static int TargetIndex(uint target)
        {
            for (int i = 0; i < targets.Length; i++) if (targets[i] == target) return i;
            return -1;
        }
    }
}
