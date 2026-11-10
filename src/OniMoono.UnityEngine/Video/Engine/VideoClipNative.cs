using System;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Serialization;

namespace OniMoono.Engine.Video
{
    /// Native side of a VideoClip (class 329): the importer's description of the movie and where
    /// its bytes are (m_ExternalResources: a range of resources.resource holding the original
    /// WebM file).
    internal sealed class VideoClipNative : NativeObject
    {
        public string OriginalPath = "";
        public uint Width, Height;
        public uint PixelAspectRatioNumerator = 1, PixelAspectRatioDenominator = 1;
        public double FrameRate;
        public ulong FrameCount;
        /// VideoClip m_Format (the importer's container/codec family; 4 for the game's WebM clips).
        public int Format;
        public ushort[] AudioChannelCounts = Array.Empty<ushort>();
        public uint[] AudioSampleRates = Array.Empty<uint>();
        public string[] AudioLanguages = Array.Empty<string>();
        public string ResourcePath = "";
        public long ResourceOffset, ResourceSize;
        public bool HasSplitAlpha;
        public bool Srgb;

        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            OriginalPath = f.String("m_OriginalPath");
            Width = f.Get<uint>("Width");
            Height = f.Get<uint>("Height");
            PixelAspectRatioNumerator = f.Get<uint>("m_PixelAspecRatioNum");
            PixelAspectRatioDenominator = f.Get<uint>("m_PixelAspecRatioDen");
            FrameRate = f.Get<double>("m_FrameRate");
            FrameCount = f.Get<ulong>("m_FrameCount");
            Format = f.Int("m_Format");
            AudioChannelCounts = f.Get<ushort[]>("m_AudioChannelCount");
            AudioSampleRates = f.Get<uint[]>("m_AudioSampleRate");
            var languages = f.Array("m_AudioLanguage");
            AudioLanguages = new string[languages.Length];
            for (int i = 0; i < languages.Length; i++) AudioLanguages[i] = (string)languages[i];
            var resource = f.Struct("m_ExternalResources");
            ResourcePath = resource.String("m_Source");
            ResourceOffset = Convert.ToInt64(resource["m_Offset"]);
            ResourceSize = Convert.ToInt64(resource["m_Size"]);
            HasSplitAlpha = f.Bool("m_HasSplitAlpha");
            Srgb = f.Bool("m_sRGB");
        }

        /// length = frameCount / frameRate, as Unity's VideoClip reports it.
        public double Length => FrameRate > 0 ? FrameCount / FrameRate : 0;

        /// The movie was dropped from a stripped copy of the game (NoContent).
        public bool MovieMissing => ResourcePath.Length > 0 && ResourceSize == 0;

        /// The movie file's bytes.
        public byte[] ReadMovie()
        {
            if (MovieMissing && !NoContent.Enabled) throw NoContent.Missing($"VideoClip {Name}", ResourcePath);
            if (ResourcePath.Length == 0 || ResourceSize <= 0) throw new InvalidOperationException($"VideoClip {Name} has no movie data.");
            return AssetSystem.ReadStream(ResourcePath, ResourceOffset, ResourceSize);
        }
    }
}
