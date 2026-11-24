using System;
using System.Collections.Generic;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;

namespace UnityEngine.Rendering
{
    /// A recorded list of rendering commands, executed when a camera reaches the CameraEvent the
    /// buffer is attached to (or by Graphics.ExecuteCommandBuffer).
    public partial class CommandBuffer
    {
        internal readonly List<RenderCommand> Commands = new();

        public CommandBuffer() { }

        public string name { get; set; }

        public int sizeInBytes => Commands.Count;

        public void Clear() => Commands.Clear();

        public void Release() => Commands.Clear();

        public virtual void Dispose() => Release();

        public void Blit(Texture source, RenderTargetIdentifier dest) => Blit(source, dest, null, -1);

        public void Blit(Texture source, RenderTargetIdentifier dest, Material mat) => Blit(source, dest, mat, -1);

        public void Blit(Texture source, RenderTargetIdentifier dest, Material mat, int pass)
            => Commands.Add(new BlitCommand(new RenderTargetIdentifier(source), dest, mat, pass));

        public void Blit(RenderTargetIdentifier source, RenderTargetIdentifier dest) => Blit(source, dest, null, -1);

        public void Blit(RenderTargetIdentifier source, RenderTargetIdentifier dest, Material mat) => Blit(source, dest, mat, -1);

        public void Blit(RenderTargetIdentifier source, RenderTargetIdentifier dest, Material mat, int pass)
            => Commands.Add(new BlitCommand(source, dest, mat, pass));

        public void DrawRenderer(Renderer renderer, Material material) => DrawRenderer(renderer, material, 0, -1);

        public void DrawRenderer(Renderer renderer, Material material, int submeshIndex) => DrawRenderer(renderer, material, submeshIndex, -1);

        /// Draws the renderer's geometry with another material, at execution time.
        public void DrawRenderer(Renderer renderer, Material material, int submeshIndex, int shaderPass)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (material == null) throw new ArgumentNullException(nameof(material));
            if (submeshIndex < 0) submeshIndex = 0;
            Commands.Add(new DrawRendererCommand(renderer, material, submeshIndex, shaderPass));
        }

        public void SetGlobalTexture(string name, RenderTargetIdentifier value) => SetGlobalTexture(Shader.PropertyToID(name), value);

        public void SetGlobalTexture(int nameID, RenderTargetIdentifier value) => Commands.Add(new SetGlobalTextureCommand(nameID, value));

        public void SetRenderTarget(RenderTargetIdentifier rt) => Commands.Add(new SetRenderTargetCommand([rt], rt));

        public void SetRenderTarget(RenderTargetIdentifier color, RenderTargetIdentifier depth) => Commands.Add(new SetRenderTargetCommand([color], depth));

        public void SetRenderTarget(RenderTargetIdentifier[] colors, RenderTargetIdentifier depth)
        {
            if (colors == null) throw new ArgumentNullException(nameof(colors));
            if (colors.Length < 1) throw new ArgumentException("colors must not be empty");
            Commands.Add(new SetRenderTargetCommand((RenderTargetIdentifier[])colors.Clone(), depth));
        }

        public void SetViewport(Rect pixelRect) => Commands.Add(new SetViewportCommand(pixelRect));

        /// Replaces the view and projection matrices for the commands that follow (the camera's
        /// are restored after the buffer).
        public void SetViewProjectionMatrices(Matrix4x4 view, Matrix4x4 proj) => Commands.Add(new SetViewProjectionCommand(view, proj));

        /// Draws only inside a pixel rectangle (origin bottom left) until DisableScissorRect.
        public void EnableScissorRect(Rect scissor) => Commands.Add(new ScissorCommand(scissor));

        public void DisableScissorRect() => Commands.Add(new ScissorCommand(null));

        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material) => DrawMesh(mesh, matrix, material, 0, -1, null);

        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int submeshIndex) => DrawMesh(mesh, matrix, material, submeshIndex, -1, null);

        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int submeshIndex, int shaderPass) => DrawMesh(mesh, matrix, material, submeshIndex, shaderPass, null);

        /// Draws the mesh with the material at execution time; the property block is copied now.
        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int submeshIndex, int shaderPass, MaterialPropertyBlock properties)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (material == null) throw new ArgumentNullException(nameof(material));
            if (submeshIndex < 0 || submeshIndex >= Math.Max(1, mesh.subMeshCount)) throw new ArgumentOutOfRangeException(nameof(submeshIndex), submeshIndex, "Invalid submesh index.");
            PropertySheet copy = null;
            if (properties != null && !properties.isEmpty)
            {
                copy = new PropertySheet();
                copy.CopyFrom(properties.Sheet);
            }
            Commands.Add(new DrawMeshCommand(mesh, matrix, material, submeshIndex, shaderPass, copy));
        }

        public void ClearRenderTarget(bool clearDepth, bool clearColor, Color backgroundColor) => ClearRenderTarget(clearDepth, clearColor, backgroundColor, 1f);

        public void ClearRenderTarget(bool clearDepth, bool clearColor, Color backgroundColor, float depth)
            => Commands.Add(new ClearCommand(clearDepth, clearColor, backgroundColor, depth));
    }

    public partial struct RenderTargetIdentifier
    {
        // BuiltinRenderTextureType values used in m_Type.
        internal const int PropertyNameType = -4, BufferPtrType = -3, RenderTextureType = -2, BindableTextureType = -1, CameraTargetType = 2;

        public RenderTargetIdentifier(Texture tex)
        {
            this = default;
            if (tex == null) m_Type = 0;
            else
            {
                m_Type = tex is RenderTexture ? RenderTextureType : BindableTextureType;
                m_InstanceID = tex.GetInstanceID();
            }
            m_CubeFace = -1;
        }

        static RenderTargetIdentifier FromBuffer(RenderBuffer buf)
        {
            var id = default(RenderTargetIdentifier);
            id.m_Type = BufferPtrType;
            id.m_InstanceID = buf.m_RenderTextureInstanceID;
            id.m_BufferPointer = buf.m_BufferPtr;
            id.m_CubeFace = -1;
            return id;
        }

        public RenderTargetIdentifier(int nameID)
        {
            this = default;
            m_Type = PropertyNameType;
            m_NameID = nameID;
            m_CubeFace = -1;
        }

        public RenderTargetIdentifier(string name) : this(Shader.PropertyToID(name)) { }

        public static implicit operator RenderTargetIdentifier(Texture tex) => new(tex);
        public static implicit operator RenderTargetIdentifier(RenderBuffer buf) => FromBuffer(buf);
        public static implicit operator RenderTargetIdentifier(int nameID) => new(nameID);
        public static implicit operator RenderTargetIdentifier(string name) => new(name);

        /// The texture this identifies at execution time: an object by instance id, or the
        /// global texture of a property name.
        internal Texture Resolve()
        {
            switch (m_Type)
            {
                case RenderTextureType or BindableTextureType or BufferPtrType:
                    return ObjectTable.FindByInstanceId(m_InstanceID) as Texture;
                case PropertyNameType:
                    return ShaderGlobals.Properties.Textures.TryGetValue(m_NameID, out var b) ? b.Texture : null;
                default:
                    return null;
            }
        }

        public override string ToString() => $"Type {m_Type} NameID {m_NameID} InstanceID {m_InstanceID}";
    }
}

namespace OniMoono.Engine.Rendering
{
    using UnityEngine;
    using UnityEngine.Rendering;

    /// One recorded CommandBuffer command.
    internal abstract class RenderCommand
    {
        public abstract void Execute(Camera camera);
    }

    internal static class CommandExecution
    {
        /// Runs the buffer's commands. Scissoring and matrices set by the buffer last until its
        /// end.
        public static void Run(CommandBuffer buffer, Camera camera)
        {
            var device = GraphicsDevice.Current;
            try
            {
                foreach (var command in buffer.Commands.ToArray()) command.Execute(camera);
            }
            finally
            {
                device.SetScissor(null);
            }
        }

        /// Graphics.ExecuteCommandBuffer: the commands run now, outside any camera; the active
        /// render target is restored afterwards.
        public static void RunImmediately(CommandBuffer buffer)
        {
            var device = GraphicsDevice.Current;
            if (!device.RendersFrames) return;
            var active = RenderTexture.active;
            var size = GLState.TargetSize;
            device.SetCamera(new CameraConstants(Matrix4x4.identity, Matrix4x4.identity, Vector3.zero, -1f, 1f, true, size));
            try
            {
                Run(buffer, null);
            }
            finally
            {
                RenderTexture.active = active;
            }
        }
    }

    sealed class SetViewportCommand(Rect pixels) : RenderCommand
    {
        public override void Execute(Camera camera) => GraphicsDevice.Current.SetViewport(pixels);
    }

    sealed class SetViewProjectionCommand(Matrix4x4 view, Matrix4x4 projection) : RenderCommand
    {
        public override void Execute(Camera camera)
            => GraphicsDevice.Current.SetCamera(new CameraConstants(view, projection, view.inverse.GetColumn(3), -1f, 1f, projection.m33 == 1f, GLState.TargetSize));
    }

    sealed class ScissorCommand(Rect? pixels) : RenderCommand
    {
        public override void Execute(Camera camera) => GraphicsDevice.Current.SetScissor(pixels);
    }

    sealed class DrawMeshCommand(Mesh mesh, Matrix4x4 matrix, Material material, int subMesh, int pass, PropertySheet properties) : RenderCommand
    {
        public override void Execute(Camera camera)
        {
            if (!mesh || !material) return;
            var m = (MeshNative)mesh.NativeOrThrow;
            if (subMesh >= m.SubMeshes.Count) return;
            GraphicsDevice.Current.Draw(new DrawRequest(m, subMesh, matrix, material, pass, properties));
        }
    }

    sealed class BlitCommand(RenderTargetIdentifier source, RenderTargetIdentifier dest, Material material, int pass) : RenderCommand
    {
        public override void Execute(Camera camera)
        {
            var src = source.Resolve();
            var dst = dest.Resolve() as RenderTexture;
            // The blit draws with its own matrices; later draws of the buffer keep the camera's
            // (or the buffer's own SetViewProjectionMatrices), as in Unity. The destination
            // stays the active target.
            var device = GraphicsDevice.Current;
            var matrices = device.Camera;
            Blits.Blit(src, dst, material, pass, Vector2.one, Vector2.zero);
            device.SetCamera(matrices);
        }
    }

    sealed class DrawRendererCommand(Renderer renderer, Material material, int subMesh, int pass) : RenderCommand
    {
        public override void Execute(Camera camera)
        {
            if (!renderer || !material || renderer.EngineState is not RendererNative r) return;
            var draws = new List<DrawItem>();
            RendererDraws.Collect(r, camera, draws);
            foreach (var d in draws)
            {
                if (d.SubMesh != Math.Min(subMesh, Math.Max(0, d.Mesh.SubMeshes.Count - 1))) continue;
                GraphicsDevice.Current.Draw(new DrawRequest(d.Mesh, d.SubMesh, d.ObjectToWorld, material, pass, d.Properties));
                break;
            }
        }
    }

    sealed class SetGlobalTextureCommand(int nameID, RenderTargetIdentifier value) : RenderCommand
    {
        public override void Execute(Camera camera) => ShaderGlobals.Properties.SetTexture(nameID, value.Resolve());
    }

    sealed class SetRenderTargetCommand(RenderTargetIdentifier[] colors, RenderTargetIdentifier depth) : RenderCommand
    {
        public override void Execute(Camera camera)
        {
            var natives = new RenderTextureNative[colors.Length];
            for (int i = 0; i < colors.Length; i++) natives[i] = colors[i].Resolve()?.Native as RenderTextureNative;
            var depthNative = depth.Resolve()?.Native as RenderTextureNative;
            if (natives.Length == 1 && natives[0] == null) natives = null;
            var device = GraphicsDevice.Current;
            // No texture: the screen, as RenderTexture.active null binds it (ScreenBinding).
            FrameRenderer.Bind(device, natives == null && depthNative == null ? FrameRenderer.ScreenBinding : new RenderTargetBinding(natives, depthNative, 0));
            var first = natives?[0];
            var size = first != null ? new Vector2(first.Width, first.Height) : GLState.TargetSize;
            device.SetViewport(new Rect(0f, 0f, size.x, size.y));
        }
    }

    sealed class ClearCommand(bool depth, bool color, Color background, float depthValue) : RenderCommand
    {
        public override void Execute(Camera camera) => GraphicsDevice.Current.Clear(color, depth, background, depthValue);
    }
}
