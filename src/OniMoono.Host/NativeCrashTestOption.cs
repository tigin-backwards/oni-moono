#nullable enable
using System;

namespace OniMoono.Host
{
    /// `--test-native-crash WHERE[:FRAME]` (NativeCrashes.Test): the crashes on purpose there are.
    /// The tests compile it in.
    internal static class NativeCrashTestOption
    {
        public static readonly string[] Choices =
            ["main", "worker", "thread", "stack-overflow", "main-stack-overflow", "fastfail", "fastfail-reported", "heap", "gs", "abort", "terminate", "failfast", "managed-null"];

        public static string Known => string.Join(", ", Choices);

        public static bool IsChoice(string what) => Array.IndexOf(Choices, what) >= 0;
    }
}
