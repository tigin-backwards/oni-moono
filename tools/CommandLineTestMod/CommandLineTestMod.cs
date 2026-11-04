using System;
using System.IO;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace CommandLineTestMod
{
    /// Logs Environment.GetCommandLineArgs() as the game's and mods' code sees it (#53): the
    /// executable's file name (the first entry; its folder differs between installs), then each
    /// entry after it, then all of those on one line, which tests/e2e/issue-53-command-line.yaml
    /// matches in both players' logs.
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            string[] args = Environment.GetCommandLineArgs();
            Debug.Log($"CommandLineTest: {args.Length} entries");
            Debug.Log($"CommandLineTest: executable {(args.Length > 0 ? Path.GetFileName(args[0]) : "(none)")}");
            Debug.Log($"CommandLineTest: entry 0 full {(args.Length > 0 ? args[0] : "(none)")}");
            for (int i = 1; i < args.Length; i++)
                Debug.Log($"CommandLineTest: entry {i} [{args[i]}]");
            Debug.Log("CommandLineTest: after the executable: " + string.Join(" ", args, Math.Min(1, args.Length), Math.Max(0, args.Length - 1)));
        }
    }
}
