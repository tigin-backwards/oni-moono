using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;

namespace OniMoono.Engine
{
    /// Directory listings in the order Unity's Mono gives them on Linux and macOS (ARCHITECTURE
    /// D30). Mono's class library enumerates folders with corefx's FileSystemEnumerator, as .NET
    /// does, but Unity's libmono-native (external/corefx-bugfix, pal_io.c) reads a whole folder on
    /// the first SystemNative_ReadDirR and sorts its entries with strcmp before handing them out:
    /// a folder's entries come in the byte order of their UTF-8 names, which is Unicode code point
    /// order. .NET hands them out in readdir order. A recursive listing walks breadth first in
    /// both (a queue of subfolders, each queued as its entry is read), so in Mono's the subfolders
    /// are visited in that sorted order too. Measured in the original (tools/UnityProbe,
    /// `PROBE_SCENARIO=dirorder`). Windows is left alone: Mono and .NET both use FindFirstFileEx
    /// there, and NTFS gives its own order.
    ///
    /// Each listing here is .NET's own (same entries, same paths, same exceptions, created as eagerly),
    /// only reordered: a folder's results are read in full, sorted and then handed out; a
    /// recursive one lists each folder alone (its FileSystemEnumerable told not to recurse but to
    /// report the subfolders it would enter), sorted, and queues the sorted subfolders.
    internal static class MonoDirectoryOrder
    {
        /// Whether listings are reordered: not on Windows.
        internal static readonly bool Sorts = !OperatingSystem.IsWindows();

        /// strcmp of the names' UTF-8 bytes: code point order. UTF-16 code unit order agrees but
        /// for a surrogate (a code point above U+FFFF) against U+E000..U+FFFF.
        internal static int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        {
            int common = a.CommonPrefixLength(b);
            if (common == a.Length || common == b.Length) return a.Length - b.Length;
            int x = a[common], y = b[common];
            if (x >= 0xD800 && y >= 0xD800)
            {
                x = x >= 0xE000 ? x - 0x800 : x + 0x2000;
                y = y >= 0xE000 ? y - 0x800 : y + 0x2000;
            }
            return x - y;
        }

        /// Paths of one folder share everything before the name.
        static readonly Comparison<string> ByPath = (a, b) => Compare(a, b);
        static readonly Comparison<FileSystemInfo> ByName = (a, b) => Compare(a.Name, b.Name);

        static Comparison<T> Comparer<T>() => typeof(T) == typeof(string) ? (Comparison<T>)(object)ByPath
            : (a, b) => ByName((FileSystemInfo)(object)a, (FileSystemInfo)(object)b);

        /// One folder's results (an array a Get* call returned), sorted in place.
        internal static T[] Sorted<T>(T[] entries)
        {
            if (Sorts && entries.Length > 1) Array.Sort(entries, Comparer<T>());
            return entries;
        }

        /// One folder's results (an Enumerate* call's, already created so that it has thrown what
        /// it throws at the call), read in full and sorted when enumerated.
        internal static IEnumerable<T> SortedLazily<T>(IEnumerable<T> entries) => Sorts ? ReadSorted(entries) : entries;

        static IEnumerable<T> ReadSorted<T>(IEnumerable<T> entries)
        {
            var list = new List<T>(entries);
            if (list.Count > 1) list.Sort(Comparer<T>());
            foreach (var entry in list) yield return entry;
        }

        /// A recursive listing in Mono's order. `first` is .NET's recursive listing of the root,
        /// created; `next(folder, expression)` creates the same listing of a subfolder (its path as
        /// the root's listing reports it) for the search pattern without a folder part. Null
        /// when .NET's listing is not one this can split by folder: it is then returned as it is.
        internal static IEnumerable<T> Breadth<T>(IEnumerable<T> first, string searchPattern, Func<string, string, IEnumerable<T>> next, bool infos)
        {
            if (!Sorts || first is not FileSystemEnumerable<T> root) return null;
            // The part of the pattern .NET applies in each folder (FileSystemEnumerableFactory.
            // NormalizeInputs moves a folder part into the root).
            var folder = Path.GetDirectoryName(searchPattern.AsSpan());
            string expression = folder.Length != 0 ? searchPattern.Substring(folder.Length + 1) : searchPattern;
            return Walk(root, expression, next, infos);
        }

        static IEnumerable<T> Walk<T>(FileSystemEnumerable<T> root, string expression, Func<string, string, IEnumerable<T>> next, bool infos)
        {
            var comparer = Comparer<T>();
            var pending = new Queue<string>();
            var subfolders = new List<string>();
            FileSystemEnumerable<T>.FindPredicate report = infos
                ? (ref FileSystemEntry entry) => { subfolders.Add(entry.ToFullPath()); return false; }
            : (ref FileSystemEntry entry) => { subfolders.Add(entry.ToSpecifiedFullPath()); return false; };
            var level = root;
            while (true)
            {
                subfolders.Clear();
                level.ShouldRecursePredicate = report;
                var entries = new List<T>(level);
                if (entries.Count > 1) entries.Sort(comparer);
                if (subfolders.Count > 1) subfolders.Sort(ByPath);
                foreach (var subfolder in subfolders) pending.Enqueue(subfolder);
                foreach (var entry in entries) yield return entry;
                level = null;
                while (level == null && pending.Count > 0)
                {
                    try
                    {
                        // .NET's own walk skips a subfolder gone since its parent was read.
                        level = next(pending.Dequeue(), expression) as FileSystemEnumerable<T>
                                ?? throw new InvalidOperationException("a folder's listing is not a FileSystemEnumerable");
                    }
                    catch (DirectoryNotFoundException)
                    {
                    }
                }
                if (level == null) yield break;
            }
        }
    }
}
