namespace GameStrip
{
    /// What the stripped copy keeps.
    static class Selection
    {
        /// Streamed payloads kept: those game or engine code reads on the CPU. Unity lets code
        /// read a texture's texels (GetPixels, GetPixelBilinear, GetRawTextureData,
        /// GetPixelData) only when the texture is readable, so every readable texture is kept;
        /// a non-readable one is only ever drawn. In ONI's code (U59 and U60) the reads of asset
        /// textures are:
        /// - CameraController.Awake: ColorCorrectionLookup.Convert(dayColourCube / nightColourCube)
        ///   reads the day and night colour-grading LUTs with GetPixels into 3D textures, and
        ///   warns when their dimensions are not a LUT's;
        /// - UIPrimitiveBase.IsRaycastLocationValid (firstpass) and uGUI's Image with an alpha hit
        ///   threshold: GetPixelBilinear on a sprite's texture, deciding whether a click hits;
        /// - TextMeshPro's dynamic font atlases (isReadable checked before adding glyphs).
        /// The game's other texel reads (KAnimBatchGroup and TextureRegion pages, PropertyTextures,
        /// the overlay occlusion textures, SubworldZoneRenderData, Timelapser, RetireColonyUtility)
        /// are of textures it makes at run time, and worldgen reads no images (its inputs are
        /// YAML, in StreamingAssets).
        /// Meshes are dropped: the game builds its tile, conduit and anim geometry at run time,
        /// and its asset meshes (quads, the space background) are only drawn.
        public static bool Keeps(StreamRef r) => r.ClassId switch
        {
            Classes.Texture2D or Classes.Cubemap or Classes.Texture3D or Classes.Texture2DArray => r.Readable,
            _ => false,
        };

        /// Files and folders of the install Moono never loads, by path relative to it. Managed/ is
        /// kept whole, though Moono's facades, packages and the .NET runtime answer for Unity's
        /// assemblies and Mono's class libraries there: mods' patches resolve their references
        /// through it (Harmony's Cecil resolver reads UnityEngine.UI, mscorlib… from Managed/).
        public static bool Skips(string relative)
        {
            return relative is "UnityPlayer.so" or "UnityPlayer_s.debug" or "OxygenNotIncluded" or "OxygenNotIncluded_s.debug"
                       or "steamapps" or "unity.lock" or "Restarter" or "OxygenNotIncluded_Data/MonoBleedingEdge"
                   || relative.StartsWith("libdecor", StringComparison.Ordinal);
        }
    }
}
