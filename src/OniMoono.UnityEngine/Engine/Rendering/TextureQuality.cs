using System;
using OniMoono.Assets.TypeTrees;

namespace OniMoono.Engine.Rendering
{
    /// The active quality level's texture settings (QualitySettings.SetQualityLevel), as Unity 6
    /// documents them:
    ///
    /// - **Global mipmap limit** (`globalTextureMipmapLimit`): how many of the highest-resolution
    ///   levels of each texture are not uploaded. It applies to 2D textures and 2D texture arrays
    ///   with mips that were loaded from asset files and do not ignore it (`m_IgnoreMipmapLimit`).
    ///   Textures made by scripts ignore it by default (`Texture2D.ignoreMipmapLimit` is true when
    ///   created in a script), as do render textures, textures without mips and textures made
    ///   non-readable at run time (here also loaded ones whose texels a script replaced). A texture's
    ///   mipmap limit group falls back to the global limit when the project does not define it;
    ///   this game defines none (`m_TextureMipmapLimitGroupNames` is empty). Unity keeps a
    ///   platform-dependent number of levels whatever the limit; here at least one.
    ///   `width`, `height` and `mipmapCount` report the asset's sizes, and a readable texture's CPU
    ///   copy keeps every level: only the device copy is smaller. When the limit changes, every
    ///   loaded texture it affects is uploaded again.
    /// - **Anisotropic textures** (`anisotropicTextures`): Disable turns anisotropic filtering off
    ///   for every texture, Enable (per texture) uses each texture's `anisoLevel`, ForceEnable
    ///   uses at least 9 for a texture whose level is not 0.
    ///
    /// This game's level 0 ("Default") has limit 0 and per-texture anisotropy; level 1 ("Low",
    /// the options screen's Low Res Textures) limit 1 and anisotropy off. The defaults below are
    /// level 0's, and change nothing.
    internal static class TextureQuality
    {
        /// AnisotropicFiltering's values.
        public const int AnisoDisable = 0, AnisoPerTexture = 1, AnisoForceEnable = 2;

        public static int MipmapLimit { get; private set; }
        public static int Anisotropic { get; private set; } = AnisoPerTexture;

        /// A quality level's mipmap limit and anisotropic mode (an entry of QualitySettings'
        /// m_QualitySettings). Unity before 2022.2 named the limit `textureQuality`.
        public static (int MipmapLimit, int Anisotropic) Of(Fields level)
        {
            int limit = level.Contains("globalTextureMipmapLimit") ? level.Int("globalTextureMipmapLimit")
                : level.Contains("textureQuality") ? level.Int("textureQuality") : 0;
            int aniso = level.Contains("anisotropicTextures") ? level.Int("anisotropicTextures") : AnisoPerTexture;
            return (Math.Max(0, limit), aniso);
        }

        /// The settings the engine starts with (ProjectSettings.Load, before anything is loaded).
        public static void Configure(Fields level) => (MipmapLimit, Anisotropic) = Of(level);

        /// A quality level was made active (main thread). A changed limit uploads every loaded
        /// texture it affects again; a changed anisotropic mode applies every texture's sampler
        /// settings again.
        public static void Apply(Fields level)
        {
            var (limit, aniso) = Of(level);
            Apply(limit, aniso);
        }

        public static void Apply(int limit, int aniso)
        {
            limit = Math.Max(0, limit);
            bool limitChanged = limit != MipmapLimit, anisoChanged = aniso != Anisotropic;
            if (!limitChanged && !anisoChanged) return;
            MipmapLimit = limit;
            Anisotropic = aniso;
            int reuploaded = 0, kept = 0;
            foreach (var native in ObjectTable.Snapshot())
            {
                if (native is not TextureNative t || t.Destroyed) continue;
                if (anisoChanged) t.SamplerChanged();
                if (limitChanged && t is TexelTextureNative tt && tt.FollowsMipmapLimit && tt.MipCount > 1)
                {
                    if (tt.MipmapLimitChanged()) reuploaded++;
                    else if (tt.Gpu != null) kept++;
                }
            }
            if (limitChanged)
                EngineLog.Engine($"texture mipmap limit {limit}: {reuploaded} textures to upload again" + (kept > 0 ? $", {kept} kept as uploaded (their asset files are no longer mounted)" : ""));
        }

        /// The levels of `t` the device copy leaves out: none unless the texture follows the
        /// limit and has mips, and never its last level.
        public static int SkippedLevels(TexelTextureNative t)
        {
            int limit = MipmapLimit;
            if (limit == 0 || !t.FollowsMipmapLimit || t.MipCount <= 1 || t.Placeholder) return 0;
            return Math.Min(limit, t.MipCount - 1);
        }

        /// The anisotropic level a texture's sampler gets for its `anisoLevel`.
        public static int Aniso(int level) => Anisotropic switch
        {
            AnisoDisable => 1,
            AnisoForceEnable => level == 0 ? 0 : Math.Clamp(level, 9, 16),
            _ => level,
        };
    }
}
