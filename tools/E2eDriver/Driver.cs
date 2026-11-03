using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace E2eDriver
{
    /// TEST INFRASTRUCTURE (docs/research/mod-play-testing.md §4.2): the play-tester's eyes in
    /// both players. A JSON-lines TCP server on 127.0.0.1 (port E2E_DRIVER_PORT, default 8790)
    /// whose requests are handled on the main thread, one per frame: UI queries, a UI dump, game
    /// and mod state by reflection, the camera, canvases, setup calls and random seeds. The
    /// protocol is in README.md. Only public Unity and uGUI API is used, the same in both players.
    /// Mods.json stamp: 1399985550 (the Mono string hash of the folder name E2eDriver,
    /// ARCHITECTURE D3).
    public class Mod : KMod.UserMod2
    {
        public static HarmonyLib.Harmony Harmony;

        public override void OnLoad(HarmonyLib.Harmony harmony)
        {
            Harmony = harmony;
            int port = 8790;
            var text = Environment.GetEnvironmentVariable("E2E_DRIVER_PORT");
            if (!string.IsNullOrEmpty(text) && !int.TryParse(text, out port))
            {
                Debug.LogError($"[E2eDriver] E2E_DRIVER_PORT is not a number: {text}");
                return;
            }
            var keeper = new GameObject("E2eDriver");
            UnityEngine.Object.DontDestroyOnLoad(keeper);
            keeper.AddComponent<Driver>().Listen(port);
        }
    }

    /// One request: its client, the parsed JSON, and (while it waits over frames) its state.
    sealed class Request
    {
        public Client Client;
        public JToken Id;
        public string Command;
        public JObject Args;
        public int StartFrame;
        /// Called once per frame until it returns true; set by commands that span frames.
        public Func<Request, bool> Pending;
        public object State;
    }

    sealed class Client
    {
        readonly TcpClient tcp;
        readonly Stream stream;
        readonly object writeLock = new object();

        public Client(TcpClient tcp) { this.tcp = tcp; stream = tcp.GetStream(); }

        public void Send(JObject response)
        {
            var bytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None) + "\n");
            lock (writeLock)
            {
                try { stream.Write(bytes, 0, bytes.Length); stream.Flush(); }
                catch (Exception) { Close(); }
            }
        }

        public void ReadLoop(Action<Client, string> received)
        {
            try
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                string line;
                while ((line = reader.ReadLine()) != null)
                    if (line.Trim().Length > 0) received(this, line);
            }
            catch (Exception) { }
            Close();
        }

        public void Close() { try { tcp.Close(); } catch (Exception) { } }
    }

    /// Thrown by a command for an answer with ok=false; its message is the error.
    sealed class DriverError : Exception
    {
        public DriverError(string message) : base(message) { }
    }

    public class Driver : MonoBehaviour
    {
        public const string Version = "1";
        public static int Port;

        readonly Queue<Request> queue = new Queue<Request>();
        Request current;
        TcpListener listener;

        public void Listen(int port)
        {
            Port = port;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
            }
            catch (Exception e)
            {
                Debug.LogError($"[E2eDriver] cannot listen on 127.0.0.1:{port}: {e.GetType().Name}: {e.Message}");
                return;
            }
            var thread = new Thread(AcceptLoop) { IsBackground = true, Name = "E2eDriver accept" };
            thread.Start();
            Debug.Log($"[E2eDriver] listening on 127.0.0.1:{port}");
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient tcp;
                try { tcp = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                tcp.NoDelay = true;
                var client = new Client(tcp);
                var thread = new Thread(() => client.ReadLoop(Received)) { IsBackground = true, Name = "E2eDriver client" };
                thread.Start();
            }
        }

        /// On a client's thread: parsed here, answered at once if malformed, else queued.
        void Received(Client client, string line)
        {
            JObject json;
            try { json = JObject.Parse(line); }
            catch (Exception e)
            {
                client.Send(new JObject { ["id"] = null, ["ok"] = false, ["error"] = "malformed request: " + e.Message });
                return;
            }
            var request = new Request
            {
                Client = client,
                Id = json["id"],
                Command = (string)json["cmd"],
                Args = json["args"] as JObject ?? json,
            };
            lock (queue) queue.Enqueue(request);
        }

        void Update()
        {
            KeyLog.Scan();
            MethodTimes.Frame();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Serve(); }
            finally { MethodTimes.Driver(started); }
        }

        void Serve()
        {
            // A request spanning frames holds the queue: requests are answered in order.
            if (current != null)
            {
                Step(current);
                return;
            }
            lock (queue)
            {
                if (queue.Count == 0) return;
                current = queue.Dequeue();
            }
            current.StartFrame = Time.frameCount;
            try
            {
                var result = Commands.Run(current);
                if (current.Pending == null) Answer(current, result);
            }
            catch (Exception e)
            {
                Fail(current, e);
            }
        }

        void Step(Request request)
        {
            try
            {
                if (request.Pending(request)) Answer(request, request.State as JToken);
            }
            catch (Exception e)
            {
                Fail(request, e);
            }
        }

        void Answer(Request request, JToken result)
        {
            request.Client.Send(new JObject { ["id"] = request.Id, ["ok"] = true, ["frame"] = Time.frameCount, ["result"] = result });
            current = null;
        }

        void Fail(Request request, Exception e)
        {
            while (e is System.Reflection.TargetInvocationException && e.InnerException != null) e = e.InnerException;
            bool expected = e is DriverError || e is WaitTimeout;
            var message = expected ? e.Message : e.GetType().Name + ": " + e.Message;
            var response = new JObject { ["id"] = request.Id, ["ok"] = false, ["frame"] = Time.frameCount, ["error"] = message };
            if (e is WaitTimeout timeout) response["result"] = timeout.Last;
            if (!expected) Debug.LogWarning($"[E2eDriver] {request.Command} failed: {e}");
            request.Client.Send(response);
            current = null;
        }

        void OnDestroy()
        {
            try { listener?.Stop(); } catch (Exception) { }
        }
    }

    /// A ui.wait that ran out of frames; Last is the element's state on the last frame.
    sealed class WaitTimeout : Exception
    {
        public readonly JToken Last;
        public WaitTimeout(string message, JToken last) : base(message) { Last = last; }
    }
}
