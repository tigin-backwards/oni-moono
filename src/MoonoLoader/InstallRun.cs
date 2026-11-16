#nullable enable
using System;
using System.Threading;

namespace MoonoLoader
{
    /// One run of the Copy button's install (ItemInstall.Install), off the game's main thread
    /// when it copies a version: the copy and its hashing take seconds, and the game would
    /// freeze for them. The run touches no Unity or game object; the dialog polls Finished from
    /// its own Update and shows the outcome on the main thread. Plain .NET (the tests compile
    /// this file too).
    ///
    /// The thread is a background one, so quitting the game never waits for it. An install cut
    /// off that way leaves only its player\.incoming-VERSION-PID folder (PlayerFiles.Copy renames
    /// it into place as its last step), which Moono's next start removes.
    internal sealed class InstallRun
    {
        readonly Func<string> install;
        string? done, failure;
        volatile bool finished;

        InstallRun(Func<string> install) => this.install = install;

        /// Whether the install has ended; Done or Failure then says how.
        public bool Finished => finished;

        /// What the install did, once it succeeded.
        public string? Done => finished ? done : null;

        /// Why the install failed, once it did.
        public string? Failure => finished ? failure : null;

        /// The run in progress when there is one (a second click, or the dialog shown again
        /// from the Mods screen, joins it: two runs would share the same incoming folder);
        /// else a new run of `install`, on the calling thread when `needsCopy` says it has
        /// nothing to copy (it then returns finished), else on a background thread.
        public static InstallRun Join(InstallRun? running, Func<bool> needsCopy, Func<string> install)
        {
            if (running != null && !running.Finished) return running;
            var run = new InstallRun(install);
            if (!needsCopy()) run.Run();
            else new Thread(run.Run) { IsBackground = true, Name = "Moono install" }.Start();
            return run;
        }

        void Run()
        {
            try { done = install(); }
            catch (ItemInstall.Failure e) { failure = e.Message; }
            // Anything else too: an exception escaping a thread of its own would end the game.
            catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; }
            finally { finished = true; }
        }
    }
}
