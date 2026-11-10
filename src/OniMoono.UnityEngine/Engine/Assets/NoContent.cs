using System;

namespace OniMoono.Engine.Assets
{
    /// No-content mode (ONI_NO_CONTENT=1), for a stripped copy of the game (tools/GameStrip):
    /// the copy keeps every serialized object and header but not the payloads only ever drawn or
    /// played, and records each dropped one as offset 0, size 0 of its resource file. In this mode
    /// such a texture loads with its declared size and format and no texels: the GL device
    /// uploads a 1×1 grey placeholder instead, and a CPU read sees zeros of the declared size. A
    /// video clip without its movie plays as blank frames for its length. The FMOD banks the copy
    /// keeps have their events but no samples, so audio is silent with no change here. Without
    /// the mode, such a payload is an error naming it, so normal play never meets a placeholder.
    internal static class NoContent
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("ONI_NO_CONTENT") == "1";

        /// What a payload's absence is outside the mode.
        public static Exception Missing(string what, string path)
            => new System.IO.InvalidDataException($"{what} has no data in {path}: this is a stripped copy of the game (tools/GameStrip), which only runs with ONI_NO_CONTENT=1.");
    }
}
