#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

[assembly: TestFramework(typeof(OniMoono.Tests.NoGameSkips))]

namespace OniMoono.Tests
{
    /// xunit's test framework, which in a run without the game (GameInstall.NoGameMode) reports a
    /// test that failed for want of it as skipped, with GameInstall's reasons: one that failed on
    /// a missing file or folder under the install (GameInstall.Dir), or on loading one of its
    /// reference assemblies, without having asked GameInstall first. Any other failure stays
    /// one. With the game, it is xunit's own.
    public sealed class NoGameSkips : XunitTestFramework
    {
        protected override ITestFrameworkExecutor CreateExecutor(Assembly assembly)
        {
            var executor = base.CreateExecutor(assembly);
            return GameInstall.NoGameMode ? new Executor(executor) : executor;
        }

        sealed class Executor(ITestFrameworkExecutor inner) : ITestFrameworkExecutor
        {
            public ValueTask RunTestCases(IReadOnlyCollection<ITestCase> testCases, IMessageSink executionMessageSink,
                ITestFrameworkExecutionOptions executionOptions, CancellationToken? cancellationToken) =>
                inner.RunTestCases(testCases, new Sink(executionMessageSink), executionOptions, cancellationToken);
        }

        /// Turns those failures into skips, and moves them from failed to skipped in the totals
        /// of the test case, method, class, collection and assembly that follow.
        sealed class Sink(IMessageSink inner) : IMessageSink
        {
            readonly Dictionary<string, int> moved = [];
            readonly Lock gate = new();

            public bool OnMessage(IMessageSinkMessage message)
            {
                lock (gate) return inner.OnMessage(Rewrite(message));
            }

            IMessageSinkMessage Rewrite(IMessageSinkMessage message)
            {
                switch (message)
                {
                    case ITestFailed failed when Reason(failed) is { } reason:
                        foreach (var id in new[] { failed.TestCaseUniqueID, failed.TestMethodUniqueID ?? "", failed.TestClassUniqueID ?? "",
                                     failed.TestCollectionUniqueID, failed.AssemblyUniqueID })
                            moved[id] = moved.GetValueOrDefault(id) + 1;
                        return new TestSkipped
                        {
                            AssemblyUniqueID = failed.AssemblyUniqueID,
                            ExecutionTime = failed.ExecutionTime,
                            FinishTime = failed.FinishTime,
                            Output = failed.Output,
                            Reason = reason,
                            TestCaseUniqueID = failed.TestCaseUniqueID,
                            TestClassUniqueID = failed.TestClassUniqueID,
                            TestCollectionUniqueID = failed.TestCollectionUniqueID,
                            TestMethodUniqueID = failed.TestMethodUniqueID,
                            TestUniqueID = failed.TestUniqueID,
                            Warnings = failed.Warnings,
                        };
                    case ITestCaseFinished f when Take(f.TestCaseUniqueID) is var n and > 0:
                        return new TestCaseFinished
                        {
                            AssemblyUniqueID = f.AssemblyUniqueID, ExecutionTime = f.ExecutionTime, FinishTime = f.FinishTime,
                            TestCaseUniqueID = f.TestCaseUniqueID, TestClassUniqueID = f.TestClassUniqueID,
                            TestCollectionUniqueID = f.TestCollectionUniqueID, TestMethodUniqueID = f.TestMethodUniqueID,
                            TestsFailed = f.TestsFailed - n, TestsNotRun = f.TestsNotRun, TestsSkipped = f.TestsSkipped + n, TestsTotal = f.TestsTotal,
                        };
                    case ITestMethodFinished f when Take(f.TestMethodUniqueID ?? "") is var n and > 0:
                        return new TestMethodFinished
                        {
                            AssemblyUniqueID = f.AssemblyUniqueID, ExecutionTime = f.ExecutionTime, FinishTime = f.FinishTime,
                            TestClassUniqueID = f.TestClassUniqueID, TestCollectionUniqueID = f.TestCollectionUniqueID,
                            TestMethodUniqueID = f.TestMethodUniqueID,
                            TestsFailed = f.TestsFailed - n, TestsNotRun = f.TestsNotRun, TestsSkipped = f.TestsSkipped + n, TestsTotal = f.TestsTotal,
                        };
                    case ITestClassFinished f when Take(f.TestClassUniqueID ?? "") is var n and > 0:
                        return new TestClassFinished
                        {
                            AssemblyUniqueID = f.AssemblyUniqueID, ExecutionTime = f.ExecutionTime, FinishTime = f.FinishTime,
                            TestClassUniqueID = f.TestClassUniqueID, TestCollectionUniqueID = f.TestCollectionUniqueID,
                            TestsFailed = f.TestsFailed - n, TestsNotRun = f.TestsNotRun, TestsSkipped = f.TestsSkipped + n, TestsTotal = f.TestsTotal,
                        };
                    case ITestCollectionFinished f when Take(f.TestCollectionUniqueID) is var n and > 0:
                        return new TestCollectionFinished
                        {
                            AssemblyUniqueID = f.AssemblyUniqueID, ExecutionTime = f.ExecutionTime, FinishTime = f.FinishTime,
                            TestCollectionUniqueID = f.TestCollectionUniqueID,
                            TestsFailed = f.TestsFailed - n, TestsNotRun = f.TestsNotRun, TestsSkipped = f.TestsSkipped + n, TestsTotal = f.TestsTotal,
                        };
                    case ITestAssemblyFinished f when Take(f.AssemblyUniqueID) is var n and > 0:
                        return new TestAssemblyFinished
                        {
                            AssemblyUniqueID = f.AssemblyUniqueID, ExecutionTime = f.ExecutionTime, FinishTime = f.FinishTime,
                            TestsFailed = f.TestsFailed - n, TestsNotRun = f.TestsNotRun, TestsSkipped = f.TestsSkipped + n, TestsTotal = f.TestsTotal,
                        };
                    default:
                        return message;
                }
            }

            int Take(string id) => moved.Remove(id, out var n) ? n : 0;
        }

        static readonly Regex s_Assembly = new(@"Could not load file or assembly '([^,']+)");

        /// GameInstall's reason for a failure for want of the game, or null: any exception of its
        /// chain a missing file or folder under the install, or a reference assembly loaded.
        static string? Reason(ITestFailed failed)
        {
            var dir = GameInstall.Dir.TrimEnd('/', '\\');
            for (int i = 0; i < failed.ExceptionTypes.Length; i++)
            {
                var type = failed.ExceptionTypes[i];
                var text = failed.Messages[i] ?? "";
                if (type == "System.BadImageFormatException" && text.Contains("reference assembl", StringComparison.OrdinalIgnoreCase))
                {
                    var name = s_Assembly.Match(text);
                    return GameInstall.NeedsAssembly + (name.Success ? name.Groups[1].Value : text) + " (a reference assembly)";
                }
                if (type is "System.IO.FileNotFoundException" or "System.IO.DirectoryNotFoundException"
                    && (text.Contains(dir + "/", StringComparison.Ordinal) || text.Contains(dir + "\\", StringComparison.Ordinal)))
                    return GameInstall.NeedsData + GameInstall.DataDirUnchecked;
            }
            return null;
        }
    }
}
