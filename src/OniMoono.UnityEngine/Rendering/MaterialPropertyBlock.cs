using System.Collections.Generic;
using OniMoono.Engine.Rendering;

namespace UnityEngine
{
    /// Per-renderer overrides of material properties.
    public partial class MaterialPropertyBlock
    {
        /// The block's values. Graphics.DrawMesh queues a draw with this sheet itself rather than
        /// a copy and marks it shared until the frame renders (PropertySheet.SharedEpoch); a change
        /// to the block before then moves the block to a copy (Writable), so the queued draw keeps
        /// the values it was given, as Unity's copy at DrawMesh does. ONI draws every animation
        /// batch through DrawMesh with a block it sets each frame before drawing.
        internal PropertySheet Sheet { get; private set; } = new(PropertySheet.BlockTextureCapacity);

        /// The sheet, made the block's own first if a queued draw still shares it. The sheet the
        /// block leaves is retired (PropertySheet.Retire): the queued draws keep reading it until
        /// the frame after next begins, and the block's new one is a recycled sheet.
        internal PropertySheet Writable
        {
            get
            {
                if (Sheet.SharedEpoch == PropertySheet.RenderEpoch)
                {
                    var copy = PropertySheet.Rent();
                    copy.CopyFrom(Sheet);
                    PropertySheet.Retire(Sheet);
                    Sheet = copy;
                }
                return Sheet;
            }
        }

        public MaterialPropertyBlock() { }

        public bool isEmpty => Sheet.IsEmpty;

        public void Clear()
        {
            if (Sheet.SharedEpoch == PropertySheet.RenderEpoch)
            {
                PropertySheet.Retire(Sheet);
                Sheet = PropertySheet.Rent();
            }
            else Sheet.Clear();
        }

        public void SetFloat(string name, float value) => Writable.SetFloat(Shader.PropertyToID(name), value);
        public void SetFloat(int nameID, float value) => Writable.SetFloat(nameID, value);
        // SetInt writes a float property, as on materials.
        public void SetInt(string name, int value) => Writable.SetFloat(Shader.PropertyToID(name), value);
        public void SetInt(int nameID, int value) => Writable.SetFloat(nameID, value);
        public void SetInteger(string name, int value) => Writable.SetInt(Shader.PropertyToID(name), value);
        public void SetInteger(int nameID, int value) => Writable.SetInt(nameID, value);
        public void SetColor(string name, Color value) => Writable.SetVector(Shader.PropertyToID(name), value);
        public void SetColor(int nameID, Color value) => Writable.SetVector(nameID, value);
        public void SetVector(string name, Vector4 value) => Writable.SetVector(Shader.PropertyToID(name), value);
        public void SetVector(int nameID, Vector4 value) => Writable.SetVector(nameID, value);
        public void SetMatrix(string name, Matrix4x4 value) => Writable.SetMatrix(Shader.PropertyToID(name), value);
        public void SetMatrix(int nameID, Matrix4x4 value) => Writable.SetMatrix(nameID, value);
        public void SetTexture(string name, Texture value) => Writable.SetTexture(Shader.PropertyToID(name), value);
        public void SetTexture(int nameID, Texture value) => Writable.SetTexture(nameID, value);

        public void SetFloatArray(string name, float[] values) => SetFloatArray(Shader.PropertyToID(name), values);
        public void SetFloatArray(int nameID, float[] values)
        {
            Material.CheckArray(values, values?.Length ?? 0);
            Writable.SetFloatArray(nameID, values, values.Length);
        }

        public void SetFloatArray(string name, List<float> values) => SetFloatArray(Shader.PropertyToID(name), values?.ToArray());
        public void SetFloatArray(int nameID, List<float> values) => SetFloatArray(nameID, values?.ToArray());

        public void SetVectorArray(string name, Vector4[] values) => SetVectorArray(Shader.PropertyToID(name), values);
        public void SetVectorArray(int nameID, Vector4[] values)
        {
            Material.CheckArray(values, values?.Length ?? 0);
            Writable.SetVectorArray(nameID, values, values.Length);
        }

        public void SetVectorArray(string name, List<Vector4> values) => SetVectorArray(Shader.PropertyToID(name), values?.ToArray());
        public void SetVectorArray(int nameID, List<Vector4> values) => SetVectorArray(nameID, values?.ToArray());

        public float GetFloat(string name) => GetFloat(Shader.PropertyToID(name));
        public float GetFloat(int nameID) => Sheet.Floats.GetValueOrDefault(nameID);
        public int GetInt(string name) => (int)GetFloat(Shader.PropertyToID(name));
        public int GetInt(int nameID) => (int)GetFloat(nameID);
        public Vector4 GetVector(string name) => GetVector(Shader.PropertyToID(name));
        public Vector4 GetVector(int nameID) => Sheet.Vectors.GetValueOrDefault(nameID);
        public Color GetColor(string name) => GetVector(Shader.PropertyToID(name));
        public Color GetColor(int nameID) => GetVector(nameID);
        public Texture GetTexture(string name) => GetTexture(Shader.PropertyToID(name));
        public Texture GetTexture(int nameID) => Sheet.Textures.TryGetValue(nameID, out var t) && t.Texture != null ? t.Texture : null;
    }
}
