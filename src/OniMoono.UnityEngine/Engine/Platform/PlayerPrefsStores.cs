using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;

namespace OniMoono.Engine.Platform
{
    internal enum PrefsKind { Int, Float, String }

    /// One PlayerPrefs value: an int, float or string, or null when the store held one that could
    /// not be read (kept as it is, read as the default). Raw: the file entry's text as read, written
    /// back unchanged.
    internal readonly record struct PrefsEntry(PrefsKind Kind, object Value, string Raw = null);

    /// Where PlayerPrefs live (UnityEngine.PlayerPrefs), chosen by the host: EngineConfig's
    /// PlayerPrefsRegistryKey on Windows, else the `prefs` file in the persistent data folder.
    internal abstract class PrefsStore
    {
        public abstract void Load(Dictionary<string, PrefsEntry> entries, List<string> order);

        /// Writes the store: CHANGED keys set, DELETED keys removed, CLEARED when DeleteAll ran.
        public abstract void Save(Dictionary<string, PrefsEntry> entries, List<string> order, HashSet<string> changed, HashSet<string> deleted, bool cleared);

        public static PrefsStore ForConfig()
        {
            if (EngineConfig.PlayerPrefsRegistryKey is { } key && OperatingSystem.IsWindows()) return new RegistryPrefsStore(key);
            return new FilePrefsStore(Path.Combine(EngineConfig.PersistentDataPath, "prefs"));
        }

        /// The key under HKEY_CURRENT_USER for the Windows player's PlayerPrefs: Unity's,
        /// Software\COMPANY\PRODUCT; for a run with a run home (tests), a key of that run home's
        /// alone, Software\OniMoonoTests\<the run home's full path>\COMPANY\PRODUCT, so a test never
        /// reads or writes the user's game key. runq removes its jobs' keys with their folders.
        public static string RegistryKeyFor(string company, string product, string runHomeFullPath)
        {
            var unity = $@"{Component(company)}\{Component(product)}";
            if (runHomeFullPath == null) return $@"Software\{unity}";
            var parts = runHomeFullPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var home = string.Join('\\', Array.FindAll(parts, p => p != "?" && p != "."));
            if (home.Length == 0) throw new ArgumentException($"run home {runHomeFullPath} names no folder");
            return $@"{TestRoot}\{home}\{unity}";
        }

        public const string TestRoot = @"Software\OniMoonoTests";

        // A company or product name is one key name: backslashes would nest it.
        static string Component(string name) => name.Replace('\\', '_');
    }

    /// Unity's Linux player's prefs file (measured in the original): an XML file of
    /// `<pref name="…" type="int|float|string">` entries, strings as base64 UTF-8, floats with six
    /// significant digits as C's %g writes them. The whole file is written at each save.
    internal sealed class FilePrefsStore : PrefsStore
    {
        readonly string path;

        public FilePrefsStore(string path) => this.path = path;

        public override void Load(Dictionary<string, PrefsEntry> entries, List<string> order)
        {
            if (!File.Exists(path)) return;
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                if (doc.DocumentElement == null) return;
                foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                {
                    if (node is not XmlElement { Name: "pref" } pref) continue;
                    var name = pref.GetAttribute("name");
                    PrefsKind? kind = pref.GetAttribute("type") switch { "int" => PrefsKind.Int, "float" => PrefsKind.Float, "string" => PrefsKind.String, _ => null };
                    if (kind == null) continue;
                    if (!entries.ContainsKey(name)) order.Add(name);
                    var text = pref.InnerText;
                    entries[name] = new PrefsEntry(kind.Value, Parse(kind.Value, text), text);
                }
            }
            catch (XmlException e)
            {
                EngineLog.Engine($"PlayerPrefs: {path} could not be read ({e.Message}); starting empty");
            }
        }

        static object Parse(PrefsKind kind, string text)
        {
            switch (kind)
            {
                case PrefsKind.Int:
                    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null;
                case PrefsKind.Float:
                    return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : null;
                default:
                    try { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
                    catch (FormatException) { return null; }
            }
        }

        public static string Format(PrefsEntry entry) => entry.Raw ?? entry.Kind switch
        {
            PrefsKind.Int => ((int)entry.Value).ToString(CultureInfo.InvariantCulture),
            // Six significant digits, as C's %g writes them (1.5, 0.333333, 1e+07).
            PrefsKind.Float => ((float)entry.Value).ToString("G6", CultureInfo.InvariantCulture).Replace("E", "e"),
            _ => Convert.ToBase64String(Encoding.UTF8.GetBytes((string)entry.Value)),
        };

        public override void Save(Dictionary<string, PrefsEntry> entries, List<string> order, HashSet<string> changed, HashSet<string> deleted, bool cleared)
        {
            var text = new StringBuilder("<unity_prefs version_major=\"1\" version_minor=\"1\">\n");
            foreach (var name in order)
            {
                var entry = entries[name];
                var type = entry.Kind switch { PrefsKind.Int => "int", PrefsKind.Float => "float", _ => "string" };
                text.Append("\t<pref name=\"").Append(Escape(name)).Append("\" type=\"").Append(type).Append("\">")
                    .Append(Escape(Format(entry))).Append("</pref>\n");
            }
            text.Append("</unity_prefs>");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? "";
    }

    /// Unity's Windows player's PlayerPrefs (measured in the original): values of the key
    /// HKCU\Software\COMPANY\PRODUCT, each named `NAME_h<hash>` (Hash), ints as a REG_DWORD,
    /// floats as a REG_DWORD of 8 bytes holding the value as a double, strings as a REG_BINARY of
    /// their UTF-8 bytes and a NUL. Values of other names are not PlayerPrefs and are left alone.
    /// A save writes only what changed, so values another process wrote in between stay.
    internal sealed class RegistryPrefsStore : PrefsStore
    {
        public const uint RegBinary = 3, RegDword = 4, RegQword = 11;

        readonly string key;
        readonly Func<string, bool, IKey> open;

        /// OPEN (tests): the key at a path, created when writing, null when reading one that does
        /// not exist; by default the registry's, under HKEY_CURRENT_USER.
        public RegistryPrefsStore(string key, Func<string, bool, IKey> open = null)
        {
            this.key = key;
            this.open = open ?? OpenWin32;
        }

        public string Describe() => @"HKCU\" + key;

        /// The value-name suffix's hash: djb2 with xor (h = h * 33 ^ byte, from 5381) over the
        /// name's UTF-8 bytes, 32 bits.
        public static uint Hash(string name)
        {
            uint h = 5381;
            foreach (var b in Encoding.UTF8.GetBytes(name)) h = (h * 33) ^ b;
            return h;
        }

        public static string ValueName(string name) => name + "_h" + Hash(name).ToString(CultureInfo.InvariantCulture);

        /// The PlayerPrefs name of a registry value name, when it is one (its suffix is its hash).
        public static string NameOf(string valueName)
        {
            int at = valueName.LastIndexOf("_h", StringComparison.Ordinal);
            if (at < 0 || !uint.TryParse(valueName.AsSpan(at + 2), NumberStyles.None, CultureInfo.InvariantCulture, out uint hash)) return null;
            var name = valueName.Substring(0, at);
            return Hash(name) == hash && valueName == ValueName(name) ? name : null;
        }

        public static (uint Type, byte[] Data) Encode(PrefsEntry entry)
        {
            switch (entry.Kind)
            {
                case PrefsKind.Int:
                    return (RegDword, BitConverter.GetBytes((int)entry.Value));
                case PrefsKind.Float:
                    return (RegDword, BitConverter.GetBytes((double)(float)entry.Value));
                default:
                    var text = Encoding.UTF8.GetBytes((string)entry.Value);
                    var data = new byte[text.Length + 1];
                    text.CopyTo(data, 0);
                    return (RegBinary, data);
            }
        }

        /// A value as PlayerPrefs reads it, or null when it is none of the three.
        public static PrefsEntry? Decode(uint type, ReadOnlySpan<byte> data)
        {
            if (type == RegDword && data.Length == 4) return new PrefsEntry(PrefsKind.Int, BitConverter.ToInt32(data));
            if ((type == RegDword || type == RegQword) && data.Length == 8) return new PrefsEntry(PrefsKind.Float, (float)BitConverter.ToDouble(data));
            if (type == RegBinary)
            {
                int end = data.IndexOf((byte)0);
                return new PrefsEntry(PrefsKind.String, Encoding.UTF8.GetString(end >= 0 ? data[..end] : data));
            }
            return null;
        }

        public override void Load(Dictionary<string, PrefsEntry> entries, List<string> order)
        {
            using var k = open(key, false);
            if (k == null) return;
            foreach (var (valueName, type, data) in k.Values())
            {
                if (NameOf(valueName) is not { } name) continue;
                var entry = Decode(type, data) ?? new PrefsEntry(PrefsKind.String, null);
                if (!entries.ContainsKey(name)) order.Add(name);
                entries[name] = entry;
            }
        }

        public override void Save(Dictionary<string, PrefsEntry> entries, List<string> order, HashSet<string> changed, HashSet<string> deleted, bool cleared)
        {
            if (changed.Count == 0 && deleted.Count == 0 && !cleared) return;
            using var k = open(key, true);
            if (cleared)
                foreach (var (valueName, _, _) in k.Values())
                    if (NameOf(valueName) is { } name && !entries.ContainsKey(name)) k.Delete(valueName);
            foreach (var name in deleted) k.Delete(ValueName(name));
            foreach (var name in changed)
            {
                if (!entries.TryGetValue(name, out var entry) || entry.Value == null) continue;
                var (type, data) = Encode(entry);
                k.Set(ValueName(name), type, data);
            }
        }

        /// A registry key's values, as RegistryPrefsStore reads and writes them.
        internal interface IKey : IDisposable
        {
            List<(string Name, uint Type, byte[] Data)> Values();
            void Set(string valueName, uint type, byte[] data);
            void Delete(string valueName);
        }

        static IKey OpenWin32(string path, bool write)
            => OperatingSystem.IsWindows() ? Registry.Open(path, write) : throw new PlatformNotSupportedException("PlayerPrefs: the registry is Windows' alone");

        /// The few registry calls PlayerPrefs needs, with value types and sizes as they are (an
        /// 8-byte REG_DWORD, which Microsoft.Win32.RegistryKey cannot write).
        [SupportedOSPlatform("windows")]
        sealed class Registry : IKey
        {
            static readonly IntPtr CurrentUser = unchecked((IntPtr)(int)0x80000001);
            const int KeyRead = 0x20019, KeyWrite = 0x20006, ErrorFileNotFound = 2, ErrorNoMoreItems = 259, ErrorMoreData = 234;
            IntPtr handle;

            Registry(IntPtr handle) => this.handle = handle;

            /// The key, created when WRITE; null when reading one that does not exist.
            public static Registry Open(string path, bool write)
            {
                int err = write
                    ? RegCreateKeyExW(CurrentUser, path, 0, null, 0, KeyRead | KeyWrite, IntPtr.Zero, out var h, out _)
                    : RegOpenKeyExW(CurrentUser, path, 0, KeyRead, out h);
                if (!write && err == ErrorFileNotFound) return null;
                if (err != 0) throw new IOException($@"HKCU\{path} could not be opened (error {err})");
                return new Registry(h);
            }

            public List<(string Name, uint Type, byte[] Data)> Values()
            {
                var values = new List<(string, uint, byte[])>();
                var name = new char[16384];
                var data = new byte[4096];
                for (uint i = 0; ; i++)
                {
                    uint nameLength = (uint)name.Length, dataLength = (uint)data.Length;
                    int err = RegEnumValueW(handle, i, name, ref nameLength, IntPtr.Zero, out uint type, data, ref dataLength);
                    if (err == ErrorMoreData)
                    {
                        data = new byte[Math.Max(dataLength, (uint)data.Length * 2)];
                        i--;
                        continue;
                    }
                    if (err == ErrorNoMoreItems) break;
                    if (err != 0) throw new IOException($"registry values could not be read (error {err})");
                    values.Add((new string(name, 0, (int)nameLength), type, data.AsSpan(0, (int)dataLength).ToArray()));
                }
                return values;
            }

            public void Set(string valueName, uint type, byte[] data)
            {
                int err = RegSetValueExW(handle, valueName, 0, type, data, (uint)data.Length);
                if (err != 0) throw new IOException($"{valueName} could not be written (error {err})");
            }

            public void Delete(string valueName)
            {
                int err = RegDeleteValueW(handle, valueName);
                if (err != 0 && err != ErrorFileNotFound) throw new IOException($"{valueName} could not be deleted (error {err})");
            }

            public void Dispose()
            {
                if (handle != IntPtr.Zero) RegCloseKey(handle);
                handle = IntPtr.Zero;
            }

            [DllImport("advapi32", CharSet = CharSet.Unicode)]
            static extern int RegOpenKeyExW(IntPtr key, string subKey, uint options, int access, out IntPtr result);

            [DllImport("advapi32", CharSet = CharSet.Unicode)]
            static extern int RegCreateKeyExW(IntPtr key, string subKey, uint reserved, string cls, uint options, int access, IntPtr security, out IntPtr result, out uint disposition);

            [DllImport("advapi32", CharSet = CharSet.Unicode)]
            static extern int RegEnumValueW(IntPtr key, uint index, char[] name, ref uint nameLength, IntPtr reserved, out uint type, byte[] data, ref uint dataLength);

            [DllImport("advapi32", CharSet = CharSet.Unicode)]
            static extern int RegSetValueExW(IntPtr key, string valueName, uint reserved, uint type, byte[] data, uint dataLength);

            [DllImport("advapi32", CharSet = CharSet.Unicode)]
            static extern int RegDeleteValueW(IntPtr key, string valueName);

            [DllImport("advapi32")]
            static extern int RegCloseKey(IntPtr key);
        }
    }
}
