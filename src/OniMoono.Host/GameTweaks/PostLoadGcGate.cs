#nullable enable

namespace OniMoono.Host.GameTweaks
{
    /// When post-load-gc collects (PostLoadGc): fed one frame at a time from the colony's
    /// Game.OnSpawn on, it answers on the first frame under FastFrameSeconds (the load's long
    /// frames over), or at the latest on the MaxFrames-th, while the game is still paused. Once
    /// the game is unpaused it never collects: the game is playing then. Plain .NET (the tests
    /// compile it in).
    internal sealed class PostLoadGcGate
    {
        public enum Verdict { Wait, Collect, Skip }

        /// The prototype's mark: on a large colony the first such frame was the 9th, still paused.
        public const double FastFrameSeconds = 0.1;
        /// Where every paused frame is slow (a slow PC): the load's allocations are over by then.
        public const int MaxFrames = 30;
        /// The speed controls not up by then: give up.
        public const int GiveUpFrames = 600;

        int frames;
        bool done;

        /// One frame: its real time in seconds and whether the game is paused (null: the speed
        /// controls aren't up). Collect or Skip is answered once; Wait after that.
        public Verdict Frame(double seconds, bool? paused)
        {
            if (done) return Verdict.Wait;
            frames++;
            var verdict = paused switch
            {
                false => Verdict.Skip,
                null => frames >= GiveUpFrames ? Verdict.Skip : Verdict.Wait,
                true => seconds < FastFrameSeconds || frames >= MaxFrames ? Verdict.Collect : Verdict.Wait,
            };
            done = verdict != Verdict.Wait;
            return verdict;
        }

        /// The frames seen, the one answered on included.
        public int Frames => frames;
    }
}
