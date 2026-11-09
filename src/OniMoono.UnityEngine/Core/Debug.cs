using System;
using System.Runtime.CompilerServices;
using OniMoono.Engine.Logging;

namespace UnityEngine
{
    public partial class Debug
    {
        static readonly ILogger s_Logger = new UnityLogger(new DefaultLogHandler());

        public static ILogger unityLogger => s_Logger;

        /// A release player (ONI ships as one).
        public static bool isDebugBuild => false;

        /// The development console overlay exists only in development builds; the flag is kept.
        public static bool developerConsoleVisible { get; set; }

        public static void Log(object message) => s_Logger.Log(LogType.Log, message);
        public static void Log(object message, Object context) => s_Logger.Log(LogType.Log, message, context);
        public static void LogFormat(string format, params object[] args) => s_Logger.LogFormat(LogType.Log, format, args);
        public static void LogFormat(Object context, string format, params object[] args) => s_Logger.LogFormat(LogType.Log, context, format, args);

        public static void LogWarning(object message) => s_Logger.Log(LogType.Warning, message);
        public static void LogWarning(object message, Object context) => s_Logger.Log(LogType.Warning, message, context);
        public static void LogWarningFormat(string format, params object[] args) => s_Logger.LogFormat(LogType.Warning, format, args);
        public static void LogWarningFormat(Object context, string format, params object[] args) => s_Logger.LogFormat(LogType.Warning, context, format, args);

        public static void LogError(object message) => s_Logger.Log(LogType.Error, message);
        public static void LogError(object message, Object context) => s_Logger.Log(LogType.Error, message, context);
        public static void LogErrorFormat(string format, params object[] args) => s_Logger.LogFormat(LogType.Error, format, args);
        public static void LogErrorFormat(Object context, string format, params object[] args) => s_Logger.LogFormat(LogType.Error, context, format, args);

        // Not inlined: their frames are in the logged traces (LogTraces), as the original's are.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void LogException(Exception exception) => s_Logger.LogException(exception, null);
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void LogException(Exception exception, Object context) => s_Logger.LogException(exception, context);

        public static void LogAssertion(object message) => s_Logger.Log(LogType.Assert, message);
        public static void LogAssertion(object message, Object context) => s_Logger.Log(LogType.Assert, message, context);

        // Development builds' assertions (U60's public test build calls them). A release build
        // compiles them out, so they do nothing here.
        public static void Assert(bool condition) { }
        public static void Assert(bool condition, string message) { }
        public static void Assert(bool condition, string message, Object context) { }
        public static void AssertFormat(bool condition, string format, params object[] args) { }

        // Debug lines are drawn by the editor's scene view only; a player draws nothing.
        public static void DrawLine(Vector3 start, Vector3 end, Color color, float duration, bool depthTest) { }
        public static void DrawLine(Vector3 start, Vector3 end, Color color, float duration) { }
        public static void DrawLine(Vector3 start, Vector3 end, Color color) { }
        public static void DrawLine(Vector3 start, Vector3 end) { }
        public static void DrawRay(Vector3 start, Vector3 dir, Color color, float duration, bool depthTest) { }
        public static void DrawRay(Vector3 start, Vector3 dir, Color color, float duration) { }
        public static void DrawRay(Vector3 start, Vector3 dir, Color color) { }
        public static void DrawRay(Vector3 start, Vector3 dir) { }
    }
}
