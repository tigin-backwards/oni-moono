using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace OniMoono.Engine
{
    /// The player's log: what Unity's default log handler does natively. Writes Player.log and
    /// stdout, and calls the log callbacks on the logging thread: logMessageReceived only for a
    /// message logged on the main thread, logMessageReceivedThreaded for every message.
    ///
    /// Locks, always taken in this order: Console.Out's (EnterConsole), then fileLock, then the
    /// original console's. A Console.WriteLine holds Console.Out's (TextWriter.Synchronized) when
    /// it reaches LogWriter, and on Linux and macOS .NET's console stream takes Console.Out's again
    /// on every write (ConsolePal.Unix's WriteFromConsoleStream), whatever stdout is. So whatever
    /// writes to the original console under fileLock takes Console.Out's first; else a game line
    /// on one thread and an engine line on another wait for each other forever (#44).
    internal static class EngineLog
    {
        static readonly object fileLock = new();
        static CappedLogFile file;

        static TextWriter console;

        /// Opens the log file (overwriting it, as Unity does; capped, unlike Unity's: CappedLogFile).
        /// Called once by the host. Like the
        /// original player's log, it also takes the process's console output: the game's own
        /// logger writes its [INFO] lines with Console.WriteLine. With `keepPrevious` (the
        /// player's default log, not one named on the command line), the last session's log is
        /// kept beside it as NAME-prev.EXT (Player-prev.log), replacing the one before, as
        /// Unity's player does. With a `scrubber` (`--log-paths hidden`), the file and the console
        /// get each line with personal paths replaced; the log callbacks still get the text as
        /// logged (mods and the game's crash reporter read it there).
        /// The log file this session writes (null before Open).
        public static string FilePath { get; private set; }

        public static void Open(string path, bool keepPrevious = false, LogScrubber scrubber = null)
        {
            FilePath = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (keepPrevious && File.Exists(path))
            {
                var previous = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-prev" + Path.GetExtension(path));
                // Another running player may hold it open; the session's log is then written
                // over it, as before.
                try { File.Move(path, previous, overwrite: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            var held = EnterConsole();
            try
            {
                lock (fileLock)
                {
                    EngineLog.scrubber = scrubber;
                    pending.Clear();
                    file = new CappedLogFile(path);
                    console ??= Console.Out;
                    Console.SetOut(new LogWriter(console));
                }
            }
            finally { Monitor.Exit(held); }
        }

        public static void Close()
        {
            var held = EnterConsole();
            try
            {
                lock (fileLock)
                {
                    if (console != null) WritePending();
                    if (console != null) Console.SetOut(console);
                    console = null;
                    file?.Dispose();
                    file = null;
                    scrubber = null;
                }
            }
            finally { Monitor.Exit(held); }
        }

        /// Takes the lock of Console.Out, the writer it still is once the lock is held (Open and
        /// Close replace it only under that lock), and returns that writer, for Monitor.Exit.
        static TextWriter EnterConsole()
        {
            while (true)
            {
                var writer = Console.Out;
                Monitor.Enter(writer);
                if (ReferenceEquals(Console.Out, writer)) return writer;
                Monitor.Exit(writer);
            }
        }

        /// Replaces personal paths in what is written (null: written as logged).
        static LogScrubber scrubber;

        /// With a scrubber, console text not yet ended by a line break: a path written in pieces
        /// (Write(char), Write(string) without one) is replaced once its line is whole. Written
        /// as it is before any other output, on Flush and on Close, so the order stays as
        /// written; past PendingMost characters without a line break it is written too.
        static readonly StringBuilder pending = new();
        const int PendingMost = 16 << 10;

        static string Scrub(string text) => scrubber == null ? text : scrubber.Scrub(text);

        /// Under fileLock: text to the original console and the file, as given.
        static void Out(string text)
        {
            console.Write(text);
            file?.Write(text);
        }

        /// Under fileLock: the pending text, scrubbed.
        static void WritePending()
        {
            if (pending.Length == 0) return;
            var text = pending.ToString();
            pending.Clear();
            Out(Scrub(text));
        }

        /// Under fileLock, with a scrubber: the pending text through its last line break (all of
        /// it once it is too long).
        static void WritePendingLines()
        {
            if (pending.Length >= PendingMost)
            {
                WritePending();
                return;
            }
            int end = -1;
            for (int i = pending.Length - 1; i >= 0; i--)
                if (pending[i] == '\n') { end = i; break; }
            if (end < 0) return;
            var text = pending.ToString(0, end + 1);
            pending.Remove(0, end + 1);
            Out(scrubber.Scrub(text));
        }

        /// The console's writer while the log is open: each write goes to the original console and
        /// the log file. It is reached only through TextWriter.Synchronized, so its callers hold
        /// Console.Out's lock, and it takes only fileLock.
        sealed class LogWriter(TextWriter console) : TextWriter
        {
            public override Encoding Encoding => console.Encoding;

            public override void Write(char value)
            {
                lock (fileLock)
                {
                    if (scrubber != null)
                    {
                        pending.Append(value);
                        if (value == '\n' || pending.Length >= PendingMost) WritePendingLines();
                        return;
                    }
                    console.Write(value);
                    file?.Write(value);
                }
            }

            public override void Write(string value)
            {
                if (value == null) return;
                lock (fileLock)
                {
                    if (scrubber != null)
                    {
                        pending.Append(value);
                        if (value.Contains('\n') || pending.Length >= PendingMost) WritePendingLines();
                        return;
                    }
                    console.Write(value);
                    file?.Write(value);
                }
            }

            public override void WriteLine(string value)
            {
                // The game's own log lines (Klei's Debug writes them here) mark its start-up.
                if (!StartupProgress.Done && Threads.IsMainThread) StartupProgress.LogLine(value);
                Platform.MainThreadPump.Maybe();
                // Scrubbed before taking the lock, unless a piece of the line is pending.
                string scrubbed = Scrub(value);
                lock (fileLock)
                {
                    if (pending.Length > 0)
                    {
                        pending.Append(value);
                        scrubbed = Scrub(pending.ToString());
                        pending.Clear();
                    }
                    console.WriteLine(scrubbed);
                    file?.WriteLine(scrubbed);
                }
            }

            public override void Flush()
            {
                lock (fileLock)
                {
                    WritePending();
                    console.Flush();
                    file?.Flush();
                }
            }
        }

        /// Engine diagnostics (not game logging): written to the log only, tagged.
        public static void Engine(string message) => WriteRaw("[engine] " + message);

        static void WriteRaw(string text)
        {
            text = Scrub(text);
            // To the original console and the log file while the log is open.
            var held = EnterConsole();
            try
            {
                lock (fileLock)
                {
                    if (file != null && console != null)
                    {
                        WritePending();
                        console.WriteLine(text);
                        file.WriteLine(text);
                    }
                    else held.WriteLine(text);
                }
            }
            finally { Monitor.Exit(held); }
        }

        public static void Message(LogType type, string message, string stackTrace) => Message(type, message, stackTrace, stackTrace);

        /// A message whose log text has another stack trace than its callbacks get (an exception's:
        /// LogTraces.Exception). A trace ends with a line break, so the text then ends with an empty
        /// line; a message without one is a line of its own.
        ///
        /// As in the original (UnityProbe's logthreads scenario), the callbacks run first, on the
        /// logging thread (Application.CallLogCallback), and the text is written after them. A
        /// message logged from inside a callback is written but reaches no callback, and a
        /// callback's exception is written as a logged exception's text, before the message's,
        /// and reaches no callback either.
        public static void Message(LogType type, string message, string logTrace, string callbackTrace)
        {
            Platform.MainThreadPump.Maybe();
            if (!inCallback)
            {
                Exception thrown;
                inCallback = true;
                try { thrown = Application.CallLogCallback(message, callbackTrace, type, Threads.IsMainThread); }
                finally { inCallback = false; }
                if (thrown != null)
                {
                    var report = LogTraces.Exception(thrown, new StackTrace(1, true));
                    WriteRaw(report.Condition + "\n" + report.LogTrace);
                }
            }
            if (errors != null && type is LogType.Error or LogType.Assert) WriteError(message);
            WriteRaw(logTrace.Length > 0 ? message + "\n" + logTrace : message);
        }

        /// ONI_LOG_ERRORS=FILE: the first line of each logged error and assertion also goes to FILE
        /// (scripts/qa.sh's log signatures). The log writes such a message as a plain line, as
        /// Unity's player does, so it alone does not tell one from a Debug.Log's.
        static readonly StreamWriter errors = OpenErrors();

        static StreamWriter OpenErrors()
        {
            var path = Environment.GetEnvironmentVariable("ONI_LOG_ERRORS");
            if (string.IsNullOrEmpty(path)) return null;
            try { return new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true }; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        static void WriteError(string message)
        {
            int end = message.IndexOf('\n');
            var line = (end < 0 ? message : message.Substring(0, end)).TrimEnd('\r');
            lock (errors) errors.WriteLine(line);
        }

        /// This thread is inside the log callbacks.
        [ThreadStatic] static bool inCallback;

        /// The managed stack of the caller in Unity's format ("Type:Method(ArgTypes)" per line), the
        /// engine's frames included, with their source lines. For engine diagnostics.
        public static string CaptureStackTrace() => LogTraces.Calls(new StackTrace(1, true));
    }
}

namespace OniMoono.Engine.Logging
{
    /// Unity's Logger: filters by type and forwards to a replaceable handler.
    internal sealed class UnityLogger : ILogger
    {
        public UnityLogger(ILogHandler handler)
        {
            logHandler = handler;
            logEnabled = true;
            filterLogType = LogType.Log;
        }

        public ILogHandler logHandler { get; set; }
        public bool logEnabled { get; set; }
        public LogType filterLogType { get; set; }

        /// Exceptions always pass; otherwise a type passes if it is at least as severe as the filter
        /// (Error is the most severe, Log the least).
        public bool IsLogTypeAllowed(LogType logType)
        {
            if (!logEnabled) return false;
            if (logType == LogType.Exception) return true;
            if (filterLogType != LogType.Exception) return logType <= filterLogType;
            return false;
        }

        static string GetString(object message)
        {
            if (message == null) return "Null";
            return message is IFormattable f ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : message.ToString();
        }

        public void Log(LogType logType, object message)
        {
            if (IsLogTypeAllowed(logType)) logHandler.LogFormat(logType, null, "{0}", GetString(message));
        }

        public void Log(LogType logType, object message, UnityEngine.Object context)
        {
            if (IsLogTypeAllowed(logType)) logHandler.LogFormat(logType, context, "{0}", GetString(message));
        }

        public void Log(LogType logType, string tag, object message)
        {
            if (IsLogTypeAllowed(logType)) logHandler.LogFormat(logType, null, "{0}: {1}", tag, GetString(message));
        }

        public void Log(LogType logType, string tag, object message, UnityEngine.Object context)
        {
            if (IsLogTypeAllowed(logType)) logHandler.LogFormat(logType, context, "{0}: {1}", tag, GetString(message));
        }

        public void Log(object message) => Log(LogType.Log, message);
        public void Log(string tag, object message) => Log(LogType.Log, tag, message);
        public void Log(string tag, object message, UnityEngine.Object context) => Log(LogType.Log, tag, message, context);
        public void LogWarning(string tag, object message) => Log(LogType.Warning, tag, message);
        public void LogWarning(string tag, object message, UnityEngine.Object context) => Log(LogType.Warning, tag, message, context);
        public void LogError(string tag, object message) => Log(LogType.Error, tag, message);
        public void LogError(string tag, object message, UnityEngine.Object context) => Log(LogType.Error, tag, message, context);

        public void LogFormat(LogType logType, string format, params object[] args)
        {
            if (IsLogTypeAllowed(logType)) logHandler.LogFormat(logType, null, format, args);
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            if (IsLogTypeAllowed(logType)) logHandler.LogFormat(logType, context, format, args);
        }

        // Not inlined: LogTraces reads their frames.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void LogException(Exception exception) => LogException(exception, null);

        // Nor is the handler inlined here (no profile-guided optimization): the game's handler keeps
        // its frame, as on Mono.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        public void LogException(Exception exception, UnityEngine.Object context)
        {
            if (logEnabled) logHandler.LogException(exception, context);
        }
    }

    /// Unity's default handler: formats and hands the message to the engine log.
    internal sealed class DefaultLogHandler : ILogHandler
    {
        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            // A release player writes no stack trace for a message of any type, although
            // Application.GetStackTraceLogType says ScriptOnly for each, and its callbacks get an
            // empty one (measured: UnityProbe's logging scenario).
            string message = args == null || args.Length == 0 ? format : string.Format(format, args);
            EngineLog.Message(logType, message, "");
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // its caller's frame is the trace's first
        public void LogException(Exception exception, UnityEngine.Object context)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            var report = LogTraces.Exception(exception, new StackTrace(1, true));
            EngineLog.Message(LogType.Exception, report.Condition, report.LogTrace, report.CallbackTrace);
        }
    }
}
