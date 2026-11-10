#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace OniMoono.Host.Launch
{
    /// The Linux desktop's dialogs for a start without a terminal (the launcher, a file manager):
    /// what StandaloneLaunch tells the player and the install chooser, through the first of
    /// zenity, kdialog and notify-send that runs (docs/INSTALL-MODES.md). None in test runs
    /// (StandaloneLaunch's `test`, or ONI_HOST_NO_DIALOG=1 as the Windows host reads it), nor
    /// without a display. Tests replace Env, Run and the terminal flags.
    internal sealed class DesktopDialog
    {
        public const string NoDialogVariable = "ONI_HOST_NO_DIALOG";
        const string Title = "Moono";

        public Func<string, string?> Env = Environment.GetEnvironmentVariable;
        /// Runs a program with its arguments: its exit code and standard output, or null when it
        /// could not be started (not installed).
        public Func<string, IReadOnlyList<string>, (int Exit, string Output)?> Run = RunProcess;
        public bool ErrorIsTerminal = !Console.IsErrorRedirected;
        public bool InputIsTerminal = !Console.IsInputRedirected;

        /// No dialog at all: a test run, or ONI_HOST_NO_DIALOG=1.
        public static bool Quiet(Func<string, string?> env, bool test) => test || env(NoDialogVariable) == "1";

        /// Dialogs can be shown: not quiet, and a display (X11 or Wayland) to show them on.
        public bool Available(bool test) =>
            !Quiet(Env, test) && (Env("DISPLAY") is { Length: > 0 } || Env("WAYLAND_DISPLAY") is { Length: > 0 });

        /// A message as a dialog when stderr, where it is written anyway, is not a terminal the
        /// player sees: the tool that showed it, or null (none ran, or none was wanted).
        public string? Tell(string message, bool error, bool test)
        {
            if (ErrorIsTerminal || !Available(test)) return null;
            var markup = Escape(message);
            (string Tool, string[] Args)[] tools =
            [
                ("zenity", [error ? "--error" : "--info", "--title=" + Title, "--text=" + markup, "--width=420"]),
                ("kdialog", ["--title", Title, error ? "--error" : "--msgbox", message]),
                ("notify-send", ["--app-name=" + Title, "--urgency=" + (error ? "critical" : "normal"),
                    "--icon=" + (error ? "dialog-error" : "dialog-information"), Title, markup]),
            ];
            foreach (var (tool, args) in tools)
                if (Shown(Run(tool, args), tool)) return tool;
            return null;
        }

        /// Asks which of `items` to use, when no terminal can (stdin is not one): Asked false when
        /// no chooser ran (the caller takes the first), else the index chosen, null when the
        /// player cancelled.
        public (bool Asked, int? Index) Choose(string text, IReadOnlyList<string> items, bool test)
        {
            if (InputIsTerminal || !Available(test)) return (false, null);
            var zenity = new List<string> { "--list", "--title=" + Title, "--text=" + Escape(text), "--column=#", "--column=Install",
                "--hide-column=1", "--print-column=1", "--width=640", "--height=320" };
            for (int i = 0; i < items.Count; i++) { zenity.Add(i.ToString()); zenity.Add(items[i]); }
            var kdialog = new List<string> { "--title", Title, "--menu", text };
            for (int i = 0; i < items.Count; i++) { kdialog.Add(i.ToString()); kdialog.Add(items[i]); }
            foreach (var (tool, args) in new[] { ("zenity", zenity), ("kdialog", kdialog) })
            {
                var result = Run(tool, args);
                if (!Shown(result, tool)) continue;
                var (exit, output) = result!.Value;
                if (exit != 0) return (true, null);
                return (true, int.TryParse(output.Trim().Split('|')[0], out var n) && n >= 0 && n < items.Count ? n : null);
            }
            return (false, null);
        }

        /// The tool ran and the player saw it: 0 (OK, chosen; notify-send: sent) or 1 (closed,
        /// cancelled). Other codes (zenity's 255, kdialog's 254, notify-send's 1 with no
        /// notification server) mean it failed: the next one is tried.
        static bool Shown((int Exit, string Output)? result, string tool) =>
            result is { } r && (r.Exit == 0 || (r.Exit == 1 && tool != "notify-send"));

        /// zenity's and notifications' text is Pango markup.
        static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        static (int, string)? RunProcess(string file, IReadOnlyList<string> args)
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) start.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(start);
                if (p == null) return null;
                p.ErrorDataReceived += (_, _) => { };
                p.BeginErrorReadLine();
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, output);
            }
            catch (Win32Exception) { return null; }
        }
    }
}
