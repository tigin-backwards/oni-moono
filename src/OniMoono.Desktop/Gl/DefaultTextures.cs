using OniMoono.Engine.Rendering;

namespace OniMoono.Desktop.Gl
{
    /// What a sampler reads when it has no texture, as Unity's GLCore player gives it (measured by
    /// tools/UnityProbe/TextureFallback.cs; DefaultTextureTests encodes the table).
    ///
    /// - A texture property declared with a default Unity knows (white, black, grey, gray, bump,
    ///   red) reads that texture; one declared with "" or a name Unity does not know, and a sampler
    ///   the shader does not declare, read grey (128).
    /// - Only the first kind keeps the globals out: the material's empty value stands. The others
    ///   read a global of the same name when the material has no texture.
    /// - A global set to a texture since destroyed counts as not set (Shader.GetGlobalTexture
    ///   returns null): the property's default.
    /// - A material or block value since destroyed, and a render texture not created (released,
    ///   or resized and not created again), read as no texture bound: (0, 0, 0, 1).
    internal static class DefaultTextures
    {
        /// No texture bound: GL's (0, 0, 0, 1) for an incomplete texture.
        public const string Unbound = "<unbound>";

        /// The default of "", of names Unity does not know and of undeclared samplers.
        public const string Unnamed = "";

        /// Each default's colour (RGBA, 8 bits). The named greys are 127 and the unnamed one 128,
        /// as read back in the original.
        public static readonly (string Name, byte R, byte G, byte B, byte A)[] Colors =
        [
            ("white", 255, 255, 255, 255),
            ("black", 0, 0, 0, 0),
            ("grey", 127, 127, 127, 127),
            ("gray", 127, 127, 127, 127),
            ("bump", 127, 127, 255, 255),
            ("red", 255, 0, 0, 0),
            (Unnamed, 128, 128, 128, 128),
            (Unbound, 0, 0, 0, 255),
        ];

        /// A default name Unity knows.
        public static bool Known(string name) => name is "white" or "black" or "grey" or "gray" or "bump" or "red";

        /// The default texture a sampler of the shader reads when nothing gives it a texture.
        public static string Of(ShaderNative shader, int propertyId)
            => shader.PropertiesById.TryGetValue(propertyId, out var p) && Known(p.DefaultTexture) ? p.DefaultTexture : Unnamed;

        /// Whether the shader declares the property with a default Unity knows, so that the
        /// material's empty value keeps a global of the same name out.
        public static bool KeepsGlobalsOut(ShaderNative shader, int propertyId)
            => shader != null && shader.PropertiesById.TryGetValue(propertyId, out var p) && Known(p.DefaultTexture);
    }
}
