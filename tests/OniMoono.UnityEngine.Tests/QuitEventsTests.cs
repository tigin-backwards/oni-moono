using System;
using System.Collections.Generic;
using OniMoono.Engine;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// Application's quit events, as the original player runs them (UnityProbe's quitevents,
    /// 2026-10-26): Quit returns at once; at the end of that frame, after WaitForEndOfFrame, the
    /// wantsToQuit handlers are asked in turn, a refusal cancelling the quit and the handlers after
    /// it not asked, a handler's exception logged and counted as agreeing; once the quit goes
    /// ahead, OnApplicationQuit, then quitting, a handler's exception ending it; unloading never.
    /// Better Save subscribes to quitting, and its OnLoad failed without it.
    public class QuitEventsTests : EngineTest
    {
        static readonly List<string> calls = new();

        public class QuitMessage : MonoBehaviour
        {
            void OnApplicationQuit() => calls.Add("OnApplicationQuit");
        }

        System.Collections.IEnumerator EndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            calls.Add("WaitForEndOfFrame");
        }

        [Fact]
        public void QuitEventsRunInTheOriginalsOrder()
        {
            calls.Clear();
            int asked = 0;
            Func<bool> first = () =>
            {
                asked++;
                calls.Add("wantsToQuit 1 request " + asked);
                if (asked == 2) throw new InvalidOperationException("wantsToQuit 1");
                return asked > 1;
            };
            Func<bool> second = () => { calls.Add("wantsToQuit 2 request " + asked); return true; };
            Action quitting1 = () => calls.Add("quitting 1");
            Action quitting2 = () => { calls.Add("quitting 2"); throw new InvalidOperationException("quitting 2"); };
            Action quitting3 = () => calls.Add("quitting 3");
            Action unloading = () => calls.Add("unloading");
            var logged = new List<string>();
            Application.LogCallback log = (condition, _, type) => { if (type == LogType.Exception) logged.Add(condition); };
            var holder = new GameObject("q");
            holder.AddComponent<QuitMessage>();
            var runner = holder.AddComponent<QuitMessage>();
            Application.wantsToQuit += first;
            Application.wantsToQuit += second;
            Application.quitting += quitting1;
            Application.quitting += quitting2;
            Application.quitting += quitting3;
            Application.unloading += unloading;
            Application.logMessageReceived += log;
            try
            {
                runner.StartCoroutine(EndOfFrame());
                Application.Quit();
                Assert.Empty(calls);
                Frame();
                Assert.Equal(new[] { "WaitForEndOfFrame", "wantsToQuit 1 request 1" }, calls);
                Assert.False(PlayerLoop.QuitRequested);
                Frame();
                Assert.Equal(2, calls.Count);

                calls.Clear();
                Application.Quit();
                Frame();
                Assert.Equal(new[] { "wantsToQuit 1 request 2", "wantsToQuit 2 request 2" }, calls);
                Assert.True(PlayerLoop.QuitRequested);
                Assert.Equal(new[] { "InvalidOperationException: wantsToQuit 1" }, logged);

                calls.Clear();
                PlayerLoop.SendApplicationQuit();
                Assert.Equal(new[] { "OnApplicationQuit", "OnApplicationQuit", "quitting 1", "quitting 2" }, calls);
                Assert.Equal("InvalidOperationException: quitting 2", logged[^1]);
            }
            finally
            {
                Application.logMessageReceived -= log;
                Application.wantsToQuit -= first;
                Application.wantsToQuit -= second;
                Application.quitting -= quitting1;
                Application.quitting -= quitting2;
                Application.quitting -= quitting3;
                Application.unloading -= unloading;
                PlayerLoop.ForgetQuit();
            }
        }

        [Fact]
        public void AQuitKeepsTheFirstError()
        {
            try
            {
                PlayerLoop.RequestQuit(1, "lost");
                PlayerLoop.RequestQuit(0);
                PlayerLoop.RequestQuit(1, "again");
                Assert.Equal("lost", PlayerLoop.QuitError);
                Assert.Equal(1, PlayerLoop.ExitCode);
                PlayerLoop.ForgetQuit();
                Assert.Null(PlayerLoop.QuitError);
                PlayerLoop.RequestQuit(0);
                Assert.Null(PlayerLoop.QuitError);
            }
            finally { PlayerLoop.ForgetQuit(); }
        }
    }
}
