using System;
using System.Collections.Generic;
using System.IO;
using OniMoono.Engine;
using OniMoono.Engine.Platform;

namespace UnityEngine
{
    /// Values kept between runs, in the store Unity's player keeps them in on each platform, so
    /// the original and ours read and write the same values (PlayerPrefsStores): on Windows the
    /// registry key HKCU\Software\<company>\<product>, elsewhere the `prefs` XML file in the
    /// persistent data folder. The store also holds the player's own entries (screen settings and
    /// the like), which are kept as read. Setting a key with another type replaces it; reading a
    /// key as another type gives the default. Changes are written by Save and at quit.
    public partial class PlayerPrefs
    {
        static readonly object gate = new();
        static PrefsStore store;
        static Dictionary<string, PrefsEntry> entries;
        // Entry order: as read, new keys after.
        static List<string> order;
        // What Save writes: the keys set and deleted since the last save, and whether DeleteAll ran.
        static HashSet<string> changed;
        static HashSet<string> deleted;
        static bool cleared;

        static Dictionary<string, PrefsEntry> Entries()
        {
            if (entries != null) return entries;
            entries = new Dictionary<string, PrefsEntry>(StringComparer.Ordinal);
            order = new List<string>();
            changed = new HashSet<string>(StringComparer.Ordinal);
            deleted = new HashSet<string>(StringComparer.Ordinal);
            cleared = false;
            store ??= PrefsStore.ForConfig();
            store.Load(entries, order);
            return entries;
        }

        static void Set(string key, PrefsEntry entry)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            lock (gate)
            {
                var e = Entries();
                if (!e.ContainsKey(key)) order.Add(key);
                e[key] = entry;
                changed.Add(key);
                deleted.Remove(key);
            }
        }

        static object Get(string key, PrefsKind kind)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            lock (gate)
                return Entries().TryGetValue(key, out var entry) && entry.Kind == kind ? entry.Value : null;
        }

        public static void SetInt(string key, int value) => Set(key, new PrefsEntry(PrefsKind.Int, value));

        public static int GetInt(string key) => GetInt(key, 0);

        public static int GetInt(string key, int defaultValue) => Get(key, PrefsKind.Int) is int v ? v : defaultValue;

        public static void SetFloat(string key, float value) => Set(key, new PrefsEntry(PrefsKind.Float, value));

        public static float GetFloat(string key) => GetFloat(key, 0f);

        public static float GetFloat(string key, float defaultValue) => Get(key, PrefsKind.Float) is float v ? v : defaultValue;

        public static void SetString(string key, string value) => Set(key, new PrefsEntry(PrefsKind.String, value ?? ""));

        public static string GetString(string key) => GetString(key, "");

        public static string GetString(string key, string defaultValue) => Get(key, PrefsKind.String) is string v ? v : defaultValue;

        public static bool HasKey(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            lock (gate) return Entries().ContainsKey(key);
        }

        public static void DeleteKey(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            lock (gate)
                if (Entries().Remove(key))
                {
                    order.Remove(key);
                    changed.Remove(key);
                    deleted.Add(key);
                }
        }

        public static void DeleteAll()
        {
            lock (gate)
            {
                Entries().Clear();
                order.Clear();
                changed.Clear();
                deleted.Clear();
                cleared = true;
            }
        }

        /// Writes the changes to the store (Unity also saves on quit).
        public static void Save()
        {
            lock (gate)
            {
                Entries();
                SaveLocked();
            }
        }

        static void SaveLocked()
        {
            store.Save(entries, order, changed, deleted, cleared);
            changed.Clear();
            deleted.Clear();
            cleared = false;
        }

        /// Forgets what was read; the store is read again at the next access (tests, a changed
        /// persistent data folder or registry key). USE (tests): the store to read, else the
        /// configured one.
        internal static void Reload(PrefsStore use = null)
        {
            lock (gate)
            {
                entries = null;
                order = null;
                store = use;
            }
        }

        /// The player's quit: unsaved changes are written, as Unity writes them.
        internal static void SaveOnQuit()
        {
            lock (gate)
            {
                if (entries == null || (changed.Count == 0 && deleted.Count == 0 && !cleared)) return;
                try
                {
                    SaveLocked();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    EngineLog.Engine($"PlayerPrefs: not saved at quit ({e.Message})");
                }
            }
        }
    }
}
