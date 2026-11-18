using System;
using System.Linq;
using System.Reflection;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// A test hook for the Restarter (scripts/check-restart.sh): ONI_TEST_APP_RESTART=FRAME calls
    /// the game's own App.Restart, as the language screen's restart does, at the first frame from
    /// FRAME on that has App.instance. Take reads the variable at start-up and clears it, so the
    /// player the restart starts plays on. Unset, it costs one comparison a frame.
    internal static class TestAppRestart
    {
        static int at = -1;

        public static void Take()
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("ONI_TEST_APP_RESTART"), out int frame) && frame >= 0) at = frame;
            Environment.SetEnvironmentVariable("ONI_TEST_APP_RESTART", null);
        }

        public static void Frame(int frame)
        {
            if (at < 0 || frame < at) return;
            var app = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp-firstpass")?.GetType("App");
            var instance = app?.GetField("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (instance == null) return;
            at = -1;
            EngineLog.Engine($"ONI_TEST_APP_RESTART: App.Restart at frame {frame}");
            app!.GetMethod("Restart", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!.Invoke(instance, null);
        }
    }
}
