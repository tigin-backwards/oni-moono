#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace OniMoono.Host.Install
{
    /// The shortcut the first install from a source folder makes, to the entry point: on Windows
    /// Moono.lnk in the Start menu's Programs and on the desktop, on Linux oni-moono.desktop in
    /// the applications folder (XDG_DATA_HOME or ~/.local/share). Test runs:
    /// ONI_MOONO_SHORTCUT_DIRS (folders separated as PATH's are) in place of those.
    internal static class Shortcuts
    {
        public const string DirsVariable = "ONI_MOONO_SHORTCUT_DIRS";
        public const string Name = "Moono";

        public static IReadOnlyList<string> Dirs()
        {
            if (Environment.GetEnvironmentVariable(DirsVariable) is { Length: > 0 } test)
                return test.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (OperatingSystem.IsWindows())
                return [Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)];
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return [Path.Combine(data, "applications")];
        }

        /// True when every shortcut was made.
        public static bool Create(VersionedInstall install, List<string> log)
        {
            var entry = Entry.PathIn(install.Root);
            bool all = true;
            foreach (var dir in Dirs())
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    Directory.CreateDirectory(dir);
                    string path;
                    if (OperatingSystem.IsWindows())
                    {
                        path = Path.Combine(dir, Name + ".lnk");
                        WriteLink(path, entry, install.Root);
                    }
                    else
                    {
                        path = Path.Combine(dir, "oni-moono.desktop");
                        File.WriteAllText(path, Desktop(entry));
                    }
                    log.Add($"install: shortcut {path}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or InvalidCastException)
                {
                    log.Add($"install: no shortcut in {dir}: {e.Message}");
                    all = false;
                }
            }
            return all;
        }

        /// A desktop entry starting `entry` (quoted as the specification asks).
        public static string Desktop(string entry) =>
            "[Desktop Entry]\nType=Application\nName=Moono\nComment=Oxygen Not Included on Moono\n"
            + $"Exec=\"{entry.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$")}\"\n"
            + "Terminal=false\nCategories=Game;\n";

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        static void WriteLink(string path, string target, string workingDir)
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription("Oxygen Not Included on Moono");
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(path, true);
            Marshal.FinalReleaseComObject(link);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr data, int flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int max, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(IntPtr window, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
