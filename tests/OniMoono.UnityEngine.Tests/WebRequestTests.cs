using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Xunit;

namespace OniMoono.Tests
{
    public class WebRequestTests : EngineTest
    {
        /// A loopback HTTP server answering each request with handler(context).
        sealed class Server : IDisposable
        {
            readonly HttpListener listener = new();
            public readonly string Url;
            public string LastBody;
            public string LastContentType;
            public NameValueCollectionView LastHeaders;

            public sealed class NameValueCollectionView(System.Collections.Specialized.NameValueCollection c)
            {
                public string this[string name] => c[name];
            }

            public Server(Action<HttpListenerContext> handler)
            {
                int port = FreePort();
                Url = $"http://127.0.0.1:{port}/";
                listener.Prefixes.Add(Url);
                listener.Start();
                Task.Run(async () =>
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await listener.GetContextAsync(); }
                        catch { return; }
                        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) LastBody = reader.ReadToEnd();
                        LastContentType = ctx.Request.ContentType;
                        LastHeaders = new NameValueCollectionView(ctx.Request.Headers);
                        try { handler(ctx); }
                        catch { }
                    }
                });
            }

            public void Dispose() => listener.Close();
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        static void Reply(HttpListenerContext ctx, int status, string body, string contentType = "text/plain; charset=utf-8")
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.AddHeader("X-Test", "yes");
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }

        /// Runs frames until the operation completes (its callbacks run only on the main thread).
        static void Pump(UnityWebRequestAsyncOperation op)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!op.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException();
                Frame();
                Thread.Sleep(5);
            }
        }

        [Fact]
        public void GetDownloadsTextAndCompletesOnMainThread()
        {
            using var server = new Server(ctx => Reply(ctx, 200, "héllo"));
            var request = UnityWebRequest.Get(server.Url + "motd.json");
            request.timeout = 3;
            request.SetRequestHeader("Content-Type", "application/json");
            Assert.Equal(-1f, request.uploadProgress);
            var op = request.SendWebRequest();
            Assert.Same(request, op.webRequest);
            int completedOn = -1;
            op.completed += _ => completedOn = Environment.CurrentManagedThreadId;
            Pump(op);
            Assert.Equal(Environment.CurrentManagedThreadId, completedOn);
            Assert.True(request.isDone);
            Assert.Equal(UnityWebRequest.Result.Success, request.result);
            Assert.Null(request.error);
            Assert.Equal(200, request.responseCode);
            Assert.Equal("héllo", request.downloadHandler.text);
            Assert.Equal(Encoding.UTF8.GetBytes("héllo"), request.downloadHandler.data);
            Assert.Equal("yes", request.GetResponseHeader("x-test"));
            Assert.Equal("yes", request.GetResponseHeaders()["X-TEST"]);
            Assert.Equal("application/json", server.LastContentType);
            Assert.StartsWith("UnityPlayer/", server.LastHeaders["User-Agent"]);
            Assert.Equal(1f, request.uploadProgress);
            Assert.Throws<InvalidOperationException>(() => request.SendWebRequest());
            Assert.Throws<InvalidOperationException>(() => request.SetRequestHeader("A", "b"));
            request.Dispose();
            Assert.Throws<NullReferenceException>(() => request.downloadHandler.text);
        }

        /// Mod Manager's Steam Web API lookup blocks the main thread on operation.isDone with
        /// Thread.Sleep(1) and no frames (#65: the game hung until Windows ended it). In Unity the
        /// operation turns done when the transfer ends (UnityProbe's apigaps "webspin"); callbacks
        /// subscribed before run on the main thread a frame later, one subscribed once it is done
        /// at once.
        [Fact]
        public void OperationTurnsDoneWhileTheMainThreadBlocks()
        {
            using var server = new Server(ctx => Reply(ctx, 200, "{}"));
            var request = new UnityWebRequest(server.Url, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.ASCII.GetBytes("itemcount=1"));
            request.downloadHandler = new DownloadHandlerBuffer();
            var op = request.SendWebRequest();
            int completed = 0;
            op.completed += _ => completed++;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!op.isDone)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("the operation never turned done without a frame");
                Thread.Sleep(1);
            }
            Assert.True(request.isDone);
            Assert.Equal(UnityWebRequest.Result.Success, request.result);
            Assert.Equal("{}", request.downloadHandler.text);
            Assert.Equal(1f, op.progress);
            Assert.Equal(0, completed);
            int late = 0;
            op.completed += _ => late++;
            Assert.Equal(1, late);
            Frame();
            Assert.Equal(1, completed);
            Assert.Equal(1, late);
            request.Dispose();
        }

        [Fact]
        public void HttpErrorIsAProtocolError()
        {
            using var server = new Server(ctx => Reply(ctx, 404, "missing"));
            var request = UnityWebRequest.Get(server.Url);
            Pump(request.SendWebRequest());
            Assert.Equal(UnityWebRequest.Result.ProtocolError, request.result);
            Assert.Equal(404, request.responseCode);
            Assert.Equal("HTTP/1.1 404 Not Found", request.error);
            Assert.Equal("missing", request.downloadHandler.text);
        }

        [Fact]
        public void FileRequestThatCannotBeReadStillCompletes()
        {
            // A path the runtime rejects outright (a NUL character) fails the request; it does
            // not leave it in progress.
            var request = UnityWebRequest.Get("file:///tmp/oni%00brutal");
            Pump(request.SendWebRequest());
            Assert.Equal(UnityWebRequest.Result.ConnectionError, request.result);
        }

        [Fact]
        public void RefusedConnection()
        {
            var request = UnityWebRequest.Get($"http://127.0.0.1:{FreePort()}/");
            Pump(request.SendWebRequest());
            Assert.Equal(UnityWebRequest.Result.ConnectionError, request.result);
            Assert.Equal("Cannot connect to destination host", request.error);
            // 0, as in the original (UnityProbe's apigaps "web": a refused connection).
            Assert.Equal(0, request.responseCode);
            Assert.Null(request.downloadHandler.data);
            Assert.Equal("", request.downloadHandler.text);
        }

        [Fact]
        public void TimeoutAndAbort()
        {
            using var server = new Server(ctx => { Thread.Sleep(3000); Reply(ctx, 200, "late"); });
            var slow = UnityWebRequest.Get(server.Url);
            slow.timeout = 1;
            Pump(slow.SendWebRequest());
            Assert.Equal("Request timeout", slow.error);

            var aborted = UnityWebRequest.Get(server.Url);
            var op = aborted.SendWebRequest();
            aborted.Abort();
            Pump(op);
            Assert.Equal(UnityWebRequest.Result.ConnectionError, aborted.result);
            Assert.Equal("Request aborted", aborted.error);
        }

        [Fact]
        public void MultipartPost()
        {
            using var server = new Server(ctx => Reply(ctx, 200, "ok"));
            var sections = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("metadata", "{}"),
                new MultipartFormFileSection("archiveFile", new byte[] { 65, 66 }, "Archive.zip", "application/octet-stream"),
            };
            var boundary = Encoding.ASCII.GetBytes("BOUNDARY");
            var request = UnityWebRequest.Post(new Uri(server.Url + "submit"), sections, boundary);
            var op = request.SendWebRequest();
            Pump(op);
            Assert.Equal(UnityWebRequest.Result.Success, request.result);
            Assert.Equal("multipart/form-data; boundary=BOUNDARY", server.LastContentType);
            Assert.Equal(
                "\r\n--BOUNDARY\r\nContent-Disposition: form-data; name=\"metadata\"\r\nContent-Type: text/plain; encoding=utf-8\r\n\r\n{}" +
                "\r\n--BOUNDARY\r\nContent-Disposition: form-data; name=\"archiveFile\"; filename=\"Archive.zip\"\r\nContent-Type: application/octet-stream\r\n\r\nAB" +
                "\r\n--BOUNDARY--\r\n",
                server.LastBody);
            Assert.Equal(1f, request.uploadProgress);
            Assert.Equal(1f, op.progress);
            Assert.Equal(40, UnityWebRequest.GenerateBoundary().Length);
        }

        [Fact]
        public void UrlNormalization()
        {
            Assert.Equal("https://localhost/a/b", new UnityWebRequest("/a/b").url);
            Assert.Equal("https://example.com/x", new UnityWebRequest("example.com/x").url);
            Assert.Equal("POST", new UnityWebRequest("http://h/", "post").method);
        }

        // ----- Upload and download handlers, as measured in the original (UnityProbe's apigaps
        // "web", against a loopback server logging the raw requests). -----

        [Fact]
        public void PostWithStringDataUploadsItEscapedAsForm()
        {
            using var server = new Server(ctx => Reply(ctx, 200, "ok"));
            var request = UnityWebRequest.Post(server.Url, "a=1&b=two words \u00e9!'()*~");
            Assert.Equal("application/x-www-form-urlencoded", request.uploadHandler.contentType);
            Assert.IsType<DownloadHandlerBuffer>(request.downloadHandler);
            Assert.Null(request.GetRequestHeader("Content-Type"));
            Pump(request.SendWebRequest());
            Assert.Equal("a%3d1%26b%3dtwo%20words%20%c3%a9%21%27()*~", server.LastBody);
            Assert.Equal("application/x-www-form-urlencoded", server.LastContentType);
            Assert.Null(UnityWebRequest.Post(server.Url, "").uploadHandler);
            Assert.Null(UnityWebRequest.Post(server.Url, (string)null).uploadHandler);
        }

        [Fact]
        public void APostWithoutBodySendsEmptyFormData()
        {
            using var server = new Server(ctx => Reply(ctx, 200, "ok"));
            var request = new UnityWebRequest(server.Url, "POST");
            Pump(request.SendWebRequest());
            Assert.Equal("application/x-www-form-urlencoded", server.LastContentType);
            Assert.Equal("", server.LastBody);
            // Without a download handler nothing is read.
            Assert.Equal(0ul, request.downloadedBytes);
        }

        [Fact]
        public void RawUploadsSendTheirContentTypeOrOctetStream()
        {
            using var server = new Server(ctx => Reply(ctx, 200, "ok"));
            var plain = new UnityWebRequest(server.Url, "PUT", new DownloadHandlerBuffer(), new UploadHandlerRaw(Encoding.UTF8.GetBytes("put body")));
            Pump(plain.SendWebRequest());
            Assert.Equal("application/octet-stream", server.LastContentType);
            Assert.Equal("put body", server.LastBody);
            Assert.Equal(8ul, plain.uploadedBytes);
            Assert.Equal(1f, plain.uploadHandler.progress);

            var json = UnityWebRequest.Post(server.Url, "");
            json.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}")) { contentType = "application/json" };
            Pump(json.SendWebRequest());
            Assert.Equal("application/json", server.LastContentType);

            var header = UnityWebRequest.Post(server.Url, "");
            header.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("xyz")) { contentType = "application/json" };
            header.SetRequestHeader("Content-Type", "text/x-header");
            Assert.Equal("text/x-header", header.GetRequestHeader("Content-Type"));
            Pump(header.SendWebRequest());
            Assert.Equal("text/x-header", server.LastContentType);
            var e = Assert.Throws<InvalidOperationException>(() => header.uploadHandler = new UploadHandlerRaw(new byte[1]));
            Assert.Equal("UnityWebRequest has already been sent; cannot modify the upload handler", e.Message);
        }

        [Fact]
        public void UploadHandlerRawKeepsACopy()
        {
            var bytes = new byte[] { 1, 2, 3 };
            var raw = new UploadHandlerRaw(bytes);
            bytes[0] = 9;
            Assert.Equal(new byte[] { 1, 2, 3 }, raw.data);
            Assert.NotSame(raw.data, raw.data);
            Assert.Equal("", raw.contentType);
            Assert.Equal(0f, raw.progress);
            Assert.Null(new UploadHandlerRaw(new byte[0]).data);
            Assert.Equal(1f, new UploadHandlerRaw(new byte[0]).progress);
            Assert.Null(new UploadHandlerRaw(null).data);
            raw.Dispose();
            Assert.Null(raw.data);
            Assert.Throws<NullReferenceException>(() => raw.contentType);
        }

        [Fact]
        public void DownloadHandlerBufferIsEmptyAndNotDoneUntilASuccess()
        {
            var buffer = new DownloadHandlerBuffer();
            Assert.Equal("", buffer.text);
            Assert.Null(buffer.data);
            Assert.False(buffer.isDone);
            Assert.Equal("", buffer.error);
            using var server = new Server(ctx => Reply(ctx, ctx.Request.Url.AbsolutePath == "/404" ? 404 : 200, "body"));
            var ok = new UnityWebRequest(server.Url, "GET", buffer, null);
            Pump(ok.SendWebRequest());
            Assert.True(buffer.isDone);
            var missing = UnityWebRequest.Get(server.Url + "404");
            Pump(missing.SendWebRequest());
            Assert.Equal("body", missing.downloadHandler.text);
            Assert.False(missing.downloadHandler.isDone);
        }

        [Fact]
        public void DownloadHandlerFileWritesTheBody()
        {
            using var server = new Server(ctx => Reply(ctx, ctx.Request.Url.AbsolutePath == "/404" ? 404 : 200, ctx.Request.Url.AbsolutePath == "/404" ? "missing" : "hello"));
            var dir = Path.Combine(Path.GetTempPath(), "dlfile-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "a", "b.txt");
            try
            {
                var handler = new DownloadHandlerFile(path);
                Assert.True(File.Exists(path));
                Assert.False(handler.removeFileOnAbort);
                Assert.Equal("String access is not supported", Assert.Throws<NotSupportedException>(() => handler.text).Message);
                Assert.Equal("Raw data access is not supported", Assert.Throws<NotSupportedException>(() => handler.data).Message);
                var request = new UnityWebRequest(server.Url, "GET", handler, null);
                Pump(request.SendWebRequest());
                request.Dispose();
                Assert.Equal("hello", File.ReadAllText(path));

                var append = new UnityWebRequest(server.Url, "GET", new DownloadHandlerFile(path, true), null);
                Pump(append.SendWebRequest());
                append.Dispose();
                Assert.Equal("hellohello", File.ReadAllText(path));

                var kept = new UnityWebRequest(server.Url + "404", "GET", new DownloadHandlerFile(path), null);
                Pump(kept.SendWebRequest());
                kept.Dispose();
                Assert.Equal("missing", File.ReadAllText(path));

                var removed = new UnityWebRequest(server.Url + "404", "GET", new DownloadHandlerFile(path) { removeFileOnAbort = true }, null);
                Pump(removed.SendWebRequest());
                removed.Dispose();
                Assert.False(File.Exists(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void TextureDownloadsDecodeOrFailTheRequest()
        {
            var source = new Texture2D(4, 2, TextureFormat.RGBA32, false);
            var png = source.EncodeToPNG();
            using var server = new Server(ctx =>
            {
                if (ctx.Request.Url.AbsolutePath == "/png")
                {
                    ctx.Response.ContentType = "image/png";
                    ctx.Response.OutputStream.Write(png);
                    ctx.Response.Close();
                }
                else Reply(ctx, 200, "hello");
            });
            Assert.Equal("Texture has not yet finished downloading", Assert.Throws<InvalidOperationException>(() => new DownloadHandlerTexture().texture).Message);
            foreach (bool nonReadable in new[] { false, true })
            {
                var request = UnityWebRequestTexture.GetTexture(server.Url + "png", nonReadable);
                Pump(request.SendWebRequest());
                Assert.Equal(UnityWebRequest.Result.Success, request.result);
                var texture = DownloadHandlerTexture.GetContent(request);
                Assert.Same(texture, ((DownloadHandlerTexture)request.downloadHandler).texture);
                Assert.Equal((4, 2, TextureFormat.ARGB32, 3, !nonReadable), (texture.width, texture.height, texture.format, texture.mipmapCount, texture.isReadable));
            }
            var text = UnityWebRequestTexture.GetTexture(server.Url + "x");
            Pump(text.SendWebRequest());
            Assert.Equal(UnityWebRequest.Result.DataProcessingError, text.result);
            Assert.Equal("Data Processing Error, see Download Handler error", text.error);
            Assert.Equal("Failed to create texture from downloaded bytes", text.downloadHandler.error);
            Assert.True(text.downloadHandler.isDone);
            Assert.Null(((DownloadHandlerTexture)text.downloadHandler).texture);
        }
    }
}
