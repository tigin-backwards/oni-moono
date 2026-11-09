using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace OniMoono.Engine
{
    /// Unity's UnityWebRequestError codes (native enum, same order).
    internal enum WebRequestError
    {
        OK, OKCached, Unknown, SDKError, UnsupportedProtocol, MalformattedUrl, CannotResolveProxy, CannotResolveHost,
        CannotConnectToHost, AccessDenied, GenericHttpError, WriteError, ReadError, OutOfMemory, Timeout, HTTPPostError,
        SSLCannotConnect, Aborted, TooManyRedirects, ReceivedNoData, SSLNotSupported, FailedToSendData, FailedToReceiveData,
        SSLCertificateError, SSLCipherNotAvailable, SSLCACertError, UnrecognizedContentEncoding, LoginFailed, SSLShutdownFailed,
        RedirectLimitInvalid, InvalidRedirect, CannotModifyRequest, HeaderNameContainsInvalidCharacters,
        HeaderValueContainsInvalidCharacters, CannotOverrideSystemHeaders, AlreadySent, InvalidMethod, NotImplemented,
        NoInternetConnection, DataProcessingError, InsecureConnectionNotAllowed,
    }

    /// The engine side of a UnityWebRequest: its settings, and the transfer run on a pool thread
    /// with HttpClient. Results are published as they happen (isDone may turn true on any
    /// thread's read, the async operation's too); the async operation completes on the main thread
    /// in EarlyUpdate.
    internal sealed class WebRequestNative
    {
        public const int NotSent = 0, Executing = 1, Done = 2;

        public string Url = "";
        public string Method = "GET";
        public int TimeoutMsec;
        readonly List<KeyValuePair<string, string>> requestHeaders = new();
        byte[] uploadData;
        string uploadContentType;

        int state;
        UnityWebRequest.Result result; // published by the write of state = Done
        public WebRequestError Error { get; private set; }
        long responseCode = -1;
        public Dictionary<string, string> ResponseHeaders;
        long uploaded, uploadTotal, downloaded, downloadTotal = -1;
        volatile bool responded;

        readonly CancellationTokenSource cancel = new();
        volatile bool aborted;
        UnityWebRequestAsyncOperation operation;

        public int State => Volatile.Read(ref state);
        public bool IsExecuting => State == Executing;
        public UnityWebRequest.Result Result => State == Done ? result : UnityWebRequest.Result.InProgress;
        public long ResponseCode => Interlocked.Read(ref responseCode);
        public long UploadedBytes => Interlocked.Read(ref uploaded);
        public long DownloadedBytes => Interlocked.Read(ref downloaded);

        public float UploadProgress
        {
            get
            {
                long total = Interlocked.Read(ref uploadTotal);
                if (total <= 0) return responded || State == Done ? 1f : 0f;
                return Math.Min(1f, (float)Interlocked.Read(ref uploaded) / total);
            }
        }

        public float DownloadProgress
        {
            get
            {
                if (State == Done) return 1f;
                long total = Interlocked.Read(ref downloadTotal);
                return total > 0 ? Math.Min(1f, (float)Interlocked.Read(ref downloaded) / total) : 0f;
            }
        }

        public void SetUpload(byte[] data, string contentType)
        {
            uploadData = data;
            uploadContentType = contentType;
        }

        // ---- Headers ----

        /// Headers Unity sets itself and refuses to take from scripts.
        static readonly HashSet<string> systemHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "accept-charset", "access-control-request-headers", "access-control-request-method", "connection", "date", "dnt",
            "expect", "host", "keep-alive", "origin", "referer", "te", "trailer", "transfer-encoding", "upgrade", "via",
            "content-length", "x-unity-version",
        };

        public WebRequestError SetRequestHeader(string name, string value)
        {
            foreach (char c in name)
                if (c <= ' ' || c >= 0x7F || "()<>@,;:\\\"/[]?={}".IndexOf(c) >= 0) return WebRequestError.HeaderNameContainsInvalidCharacters;
            foreach (char c in value)
                if (c == '\r' || c == '\n' || c == '\0') return WebRequestError.HeaderValueContainsInvalidCharacters;
            if (systemHeaders.Contains(name)) return WebRequestError.CannotOverrideSystemHeaders;
            for (int i = 0; i < requestHeaders.Count; i++)
                if (string.Equals(requestHeaders[i].Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    requestHeaders[i] = new(name, value);
                    return WebRequestError.OK;
                }
            requestHeaders.Add(new(name, value));
            return WebRequestError.OK;
        }

        /// UnityWebRequest.Post(uri, string)'s escape of form data: UTF-8, every byte but ASCII
        /// letters, digits and ( ) * - . _ ~ as %xx in lower-case hex (measured in the original:
        /// UnityProbe's apigaps "web", every printable ASCII character).
        public static string EscapeForm(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var sb = new StringBuilder(bytes.Length * 3);
            foreach (byte b in bytes)
            {
                if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                    || b is (byte)'(' or (byte)')' or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
                    sb.Append((char)b);
                else sb.Append('%').Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        /// A header the script set, or null.
        public string GetRequestHeader(string name)
        {
            foreach (var (key, value) in requestHeaders)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
            return null;
        }

        public string GetResponseHeader(string name)
        {
            var headers = ResponseHeaders;
            return headers != null && name != null && headers.TryGetValue(name, out var v) ? v : null;
        }

        // ---- Lifetime ----

        public UnityWebRequestAsyncOperation Begin(UnityWebRequest owner)
        {
            lock (cancel)
            {
                if (state != NotSent) throw new InvalidOperationException(ErrorString(WebRequestError.AlreadySent));
                Volatile.Write(ref state, Executing);
            }
            operation = new UnityWebRequestAsyncOperation { m_Request = this };
            WebRequests.Track(this, operation);
            var handler = owner.m_DownloadHandler;
            if (owner.m_UploadHandler != null)
            {
                var ct = owner.m_UploadHandler.contentType;
                SetUpload(owner.m_UploadHandler.m_Data ?? Array.Empty<byte>(), string.IsNullOrEmpty(ct) ? "application/octet-stream" : ct);
                owner.m_UploadHandler.m_Request = this;
            }
            Task.Run(() => RunAsync(handler));
            return operation;
        }

        public void Abort()
        {
            aborted = true;
            lock (cancel)
            {
                if (state == NotSent)
                {
                    Publish(UnityWebRequest.Result.ConnectionError, WebRequestError.Aborted);
                    return;
                }
            }
            cancel.Cancel();
        }

        // Result and error become visible together with state Done.
        void Publish(UnityWebRequest.Result r, WebRequestError error)
        {
            result = r;
            Error = error;
            Volatile.Write(ref state, Done);
        }

        void Finish(UnityWebRequest.Result r, WebRequestError error)
        {
            lock (cancel) Publish(r, error);
            WebRequests.Finished(this);
        }

        // ---- Transfer ----

        async Task RunAsync(DownloadHandler handler)
        {
            if (aborted)
            {
                Finish(UnityWebRequest.Result.ConnectionError, WebRequestError.Aborted);
                return;
            }
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri))
            {
                Finish(UnityWebRequest.Result.ConnectionError, WebRequestError.MalformattedUrl);
                return;
            }
            if (uri.IsFile)
            {
                RunFile(uri, handler);
                return;
            }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                Finish(UnityWebRequest.Result.ConnectionError, WebRequestError.UnsupportedProtocol);
                return;
            }

            if (TimeoutMsec > 0) cancel.CancelAfter(TimeoutMsec);
            var token = cancel.Token;
            bool sending = true;
            try
            {
                using var request = BuildRequest(uri);
                using var response = await WebRequests.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                sending = false;
                responded = true;
                ResponseHeaders = CollectHeaders(response);
                Interlocked.Exchange(ref responseCode, (long)response.StatusCode);
                long? length = response.Content.Headers.ContentLength;
                if (length.HasValue) Interlocked.Exchange(ref downloadTotal, length.Value);
                handler?.OnBegin(response.Content.Headers.ContentType?.ToString());
                // Without a download handler the body is not read: downloadedBytes stays 0.
                if (handler != null && !string.Equals(Method, "HEAD", StringComparison.Ordinal))
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var buffer = new byte[16384];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        handler?.OnReceive(buffer, read);
                        Interlocked.Add(ref downloaded, read);
                    }
                }
                if ((int)response.StatusCode >= 400)
                {
                    handler?.OnEnd(false);
                    Finish(UnityWebRequest.Result.ProtocolError, WebRequestError.OK);
                }
                else Complete(handler);
            }
            catch (OperationCanceledException)
            {
                Fail(handler, aborted ? WebRequestError.Aborted : WebRequestError.Timeout);
            }
            catch (HttpRequestException e)
            {
                Fail(handler, MapError(e, sending));
            }
            catch (IOException)
            {
                Fail(handler, sending ? WebRequestError.FailedToSendData : WebRequestError.FailedToReceiveData);
            }
            catch (Exception)
            {
                Fail(handler, WebRequestError.Unknown);
            }
        }

        /// A connection error: responseCode 0 when no response came (as the original's curl
        /// backend reports it).
        void Fail(DownloadHandler handler, WebRequestError error)
        {
            if (!responded) Interlocked.Exchange(ref responseCode, 0);
            handler?.OnEnd(false);
            Finish(UnityWebRequest.Result.ConnectionError, error);
        }

        /// A response the download handler took whole: its processing (a texture's decode) can
        /// still fail the request (DataProcessingError, the handler's error saying why).
        void Complete(DownloadHandler handler)
        {
            if (handler != null && !handler.OnEnd(true))
            {
                Finish(UnityWebRequest.Result.DataProcessingError, WebRequestError.DataProcessingError);
                return;
            }
            Finish(UnityWebRequest.Result.Success, WebRequestError.OK);
        }

        /// Default headers as Unity's curl backend sends them, then the script's headers. The
        /// body's Content-Type is the script's Content-Type header when set, else the upload's.
        HttpRequestMessage BuildRequest(Uri uri)
        {
            var request = new HttpRequestMessage(new HttpMethod(Method), uri);
            var version = EngineConfig.UnityVersion;
            request.Headers.TryAddWithoutValidation("User-Agent", $"UnityPlayer/{version} (UnityWebRequest/1.0, libcurl/8.10.1-DEV)");
            request.Headers.TryAddWithoutValidation("X-Unity-Version", version);
            // As the original's curl backend sends them (UnityProbe's apigaps "web"); the
            // handler still decompresses what comes back.
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "deflate, gzip");

            string contentType = uploadContentType;
            var contentHeaders = new List<KeyValuePair<string, string>>();
            foreach (var (name, value) in requestHeaders)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
                else if (IsContentHeader(name)) contentHeaders.Add(new(name, value));
                else
                {
                    request.Headers.Remove(name);
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            // A POST without a body still sends one, empty, as form data (as the original's
            // curl backend does: UnityProbe's apigaps "web").
            if (uploadData == null && contentType == null && string.Equals(Method, "POST", StringComparison.Ordinal))
                contentType = "application/x-www-form-urlencoded";
            if (uploadData != null || contentType != null || contentHeaders.Count > 0)
            {
                var body = uploadData ?? Array.Empty<byte>();
                Interlocked.Exchange(ref uploadTotal, body.Length);
                var content = new ProgressContent(body, this);
                if (contentType != null) content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                foreach (var (name, value) in contentHeaders) content.Headers.TryAddWithoutValidation(name, value);
                request.Content = content;
            }
            return request;
        }

        static bool IsContentHeader(string name) => name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(name, "Expires", StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(name, "Last-Modified", StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(name, "Allow", StringComparison.OrdinalIgnoreCase);

        /// Response and content headers; a repeated header's values are joined with ", ".
        static Dictionary<string, string> CollectHeaders(HttpResponseMessage response)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(HttpHeaders source)
            {
                foreach (var h in source)
                {
                    var value = string.Join(", ", h.Value);
                    headers[h.Key] = headers.TryGetValue(h.Key, out var existing) ? existing + ", " + value : value;
                }
            }
            Add(response.Headers);
            Add(response.Content.Headers);
            return headers;
        }

        static WebRequestError MapError(HttpRequestException e, bool sending) => e.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => WebRequestError.CannotResolveHost,
            HttpRequestError.ConnectionError => WebRequestError.CannotConnectToHost,
            HttpRequestError.ProxyTunnelError => WebRequestError.CannotResolveProxy,
            HttpRequestError.SecureConnectionError => WebRequestError.SSLCannotConnect,
            HttpRequestError.ResponseEnded => sending ? WebRequestError.ReceivedNoData : WebRequestError.FailedToReceiveData,
            HttpRequestError.ConfigurationLimitExceeded => WebRequestError.TooManyRedirects,
            _ => sending ? WebRequestError.FailedToSendData : WebRequestError.FailedToReceiveData,
        };

        /// file:// URLs are read directly; the response code is 0 and a missing or unreadable
        /// file is "Cannot connect to destination host", as with Unity's curl backend.
        void RunFile(Uri uri, DownloadHandler handler)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(uri.LocalPath);
            }
            // Any path the file cannot be read from (missing, unreadable, or one the runtime
            // rejects, such as one with a NUL character).
            catch (Exception)
            {
                Fail(handler, WebRequestError.CannotConnectToHost);
                return;
            }
            Interlocked.Exchange(ref responseCode, 0);
            Interlocked.Exchange(ref downloadTotal, bytes.Length);
            responded = true;
            handler?.OnBegin(null);
            handler?.OnReceive(bytes, bytes.Length);
            Interlocked.Exchange(ref downloaded, bytes.Length);
            Complete(handler);
        }

        /// A request body that counts bytes as they are written to the connection.
        sealed class ProgressContent(byte[] data, WebRequestNative owner) : HttpContent
        {
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken cancellationToken)
            {
                const int chunk = 16384;
                for (int offset = 0; offset < data.Length; offset += chunk)
                {
                    int count = Math.Min(chunk, data.Length - offset);
                    await stream.WriteAsync(data.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref owner.uploaded, count);
                }
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
                => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override bool TryComputeLength(out long length)
            {
                length = data.Length;
                return true;
            }
        }

        // ---- Strings ----

        /// Unity's native error descriptions (UnityPlayer).
        public static string ErrorString(WebRequestError error) => error switch
        {
            WebRequestError.OK or WebRequestError.OKCached => "No Error",
            WebRequestError.Unknown => "Unknown Error",
            WebRequestError.SDKError => "Backend Initialization Error",
            WebRequestError.UnsupportedProtocol => "Unsupported Protocol",
            WebRequestError.MalformattedUrl => "Malformed URL",
            WebRequestError.CannotResolveProxy => "Cannot resolve proxy",
            WebRequestError.CannotResolveHost => "Cannot resolve destination host",
            WebRequestError.CannotConnectToHost => "Cannot connect to destination host",
            WebRequestError.AccessDenied => "Access denied",
            WebRequestError.GenericHttpError => "Generic/unknown HTTP error",
            WebRequestError.WriteError => "Unable to write data",
            WebRequestError.ReadError => "Unable to read data",
            WebRequestError.OutOfMemory => "Out of memory",
            WebRequestError.Timeout => "Request timeout",
            WebRequestError.HTTPPostError => "Error during HTTP POST transmission",
            WebRequestError.SSLCannotConnect => "Unable to complete SSL connection",
            WebRequestError.Aborted => "Request aborted",
            WebRequestError.TooManyRedirects => "Redirect limit exceeded",
            WebRequestError.ReceivedNoData => "Received no data in response",
            WebRequestError.SSLNotSupported => "Destination host does not support SSL",
            WebRequestError.FailedToSendData => "Failed to transmit data",
            WebRequestError.FailedToReceiveData => "Failed to receive data",
            WebRequestError.SSLCertificateError => "Destination host has an erroneous SSL certificate",
            WebRequestError.SSLCipherNotAvailable => "Unable to load SSL Cipher for verification",
            WebRequestError.SSLCACertError => "SSL CA certificate error",
            WebRequestError.UnrecognizedContentEncoding => "Unrecognized content-encoding",
            WebRequestError.LoginFailed => "Login failed",
            WebRequestError.SSLShutdownFailed => "SSL shutdown failed",
            WebRequestError.RedirectLimitInvalid => "Redirect limit is invalid",
            WebRequestError.InvalidRedirect => "Encountered invalid redirect (missing Location header?)",
            WebRequestError.CannotModifyRequest => "Cannot modify request at this time",
            WebRequestError.HeaderNameContainsInvalidCharacters => "Header name contains invalid characters",
            WebRequestError.HeaderValueContainsInvalidCharacters => "Header value contains invalid characters",
            WebRequestError.CannotOverrideSystemHeaders => "Cannot override system-specified headers",
            WebRequestError.AlreadySent => "UnityWebRequest has already been sent; cannot begin sending the request again",
            WebRequestError.InvalidMethod => "Invalid HTTP Method",
            WebRequestError.NotImplemented => "Not implemented",
            WebRequestError.NoInternetConnection => "No Internet Connection",
            WebRequestError.DataProcessingError => "Data Processing Error, see Download Handler error",
            WebRequestError.InsecureConnectionNotAllowed => "Insecure connection not allowed",
            _ => "Unknown Error",
        };

        /// Unity's reason phrases (UnityPlayer's table); codes it does not know get
        /// "Unknown HTTP status".
        public static string HttpStatusString(long code) => code switch
        {
            #pragma warning disable format // a hand-laid table
            100 => "Continue", 101 => "Switching Protocols", 102 => "Processing",
            200 => "OK", 201 => "Created", 202 => "Accepted", 203 => "Non-Authoritative Information", 204 => "No Content",
            205 => "Reset Content", 206 => "Partial Content", 207 => "Multi-Status", 208 => "Already Reported", 226 => "IM Used",
            300 => "Multiple Choices", 301 => "Moved Permanently", 302 => "Found", 303 => "See Other", 304 => "Not Modified",
            305 => "Use Proxy", 307 => "Temporary Redirect", 308 => "Permanent Redirect",
            400 => "Bad Request", 401 => "Unauthorized", 402 => "Payment Required", 403 => "Forbidden", 404 => "Not Found",
            405 => "Method Not Allowed", 406 => "Not Acceptable", 407 => "Proxy Authentication Required", 408 => "Request Timeout",
            409 => "Conflict", 410 => "Gone", 411 => "Length Required", 412 => "Precondition Failed", 413 => "Payload Too Large",
            414 => "URI Too Long", 415 => "Unsupported Media Type", 416 => "Range Not Satisfiable", 417 => "Expectation Failed",
            418 => "I'm a teapot", 421 => "Misdirected Request", 422 => "Unprocessable Entity", 423 => "Locked",
            424 => "Failed Dependency", 426 => "Upgrade Required", 428 => "Precondition Required", 429 => "Too Many Requests",
            431 => "Request Header Fields Too Large", 451 => "Unavailable For Legal Reasons",
            500 => "Internal Server Error", 501 => "Not Implemented", 502 => "Bad Gateway", 503 => "Service Unavailable",
            504 => "Gateway Timeout", 505 => "HTTP Version Not Supported", 506 => "Variant Also Negotiates",
            507 => "Insufficient Storage", 508 => "Loop Detected", 510 => "Not Extended", 511 => "Network Authentication Required",
            #pragma warning restore format
            _ => "Unknown HTTP status",
        };
    }

    /// The shared HTTP client and the main-thread completion of request operations.
    internal static class WebRequests
    {
        /// One connection pool for all requests, like Unity's curl multi handle: redirects
        /// followed up to Unity's default limit of 32, gzip/deflate decoded, cookies kept.
        public static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 32,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        static readonly Dictionary<WebRequestNative, UnityWebRequestAsyncOperation> active = new();
        static readonly ConcurrentQueue<WebRequestNative> finished = new();
        static bool hooked;

        public static void Track(WebRequestNative request, UnityWebRequestAsyncOperation op)
        {
            if (!hooked)
            {
                PlayerLoop.EarlyUpdate += Update;
                hooked = true;
            }
            active[request] = op;
        }

        public static void Finished(WebRequestNative request) => finished.Enqueue(request);

        /// Main thread: operation progress follows the download; finished requests complete
        /// their operations (in finishing order).
        static void Update()
        {
            while (finished.TryDequeue(out var request))
            {
                if (!active.Remove(request, out var op)) continue;
                op.Complete();
            }
            foreach (var (request, op) in active) op.SetProgress(request.DownloadProgress);
        }
    }

    /// Unity's URL normalization (WebRequestUtils).
    internal static class WebRequestUrls
    {
        static readonly Regex domainRegex = new("^\\s*\\w+(?:\\.\\w+)+(\\/.*)?$");

        public static string MakeInitialUrl(string targetUrl, string localUrl)
        {
            if (string.IsNullOrEmpty(targetUrl)) return "";
            bool prependProtocol = false;
            var local = new Uri(localUrl);
            Uri target = null;
            if (targetUrl[0] == '/')
            {
                target = new Uri(local, targetUrl);
                prependProtocol = true;
            }
            if (target == null && domainRegex.IsMatch(targetUrl))
            {
                targetUrl = local.Scheme + "://" + targetUrl;
                prependProtocol = true;
            }
            FormatException failure = null;
            try
            {
                if (target == null && targetUrl[0] != '.') target = new Uri(targetUrl);
            }
            catch (FormatException e)
            {
                failure = e;
            }
            if (target == null)
            {
                try
                {
                    target = new Uri(local, targetUrl);
                    prependProtocol = true;
                }
                catch (FormatException)
                {
                    throw failure;
                }
            }
            return MakeUriString(target, targetUrl, prependProtocol);
        }

        public static string MakeUriString(Uri targetUri, string targetUrl, bool prependProtocol)
        {
            if (targetUri.IsFile)
            {
                if (!targetUri.IsLoopback) return targetUri.OriginalString;
                string path = targetUri.AbsolutePath;
                string original = targetUri.OriginalString;
                if (path.Contains('%'))
                {
                    if (path.Contains('+') && !original.StartsWith("file:", StringComparison.Ordinal)) return "file://" + original;
                    path = UrlDecode(path);
                }
                if (path.Length > 0 && path[0] != '/') path = "/" + path;
                return "file://" + path;
            }
            string scheme = targetUri.Scheme;
            if (!prependProtocol && targetUrl.Length >= scheme.Length + 2 && targetUrl[scheme.Length + 1] != '/')
            {
                var sb = new StringBuilder(scheme, targetUrl.Length);
                sb.Append(':');
                if (scheme == "jar")
                {
                    string path = targetUri.AbsolutePath;
                    if (path.Contains('%')) path = UrlDecode(path);
                    if (path.StartsWith("file:/", StringComparison.Ordinal) && path.Length > 6 && path[6] != '/')
                    {
                        sb.Append("file://");
                        sb.Append(path.Substring(5));
                    }
                    else sb.Append(path);
                    return sb.ToString();
                }
                sb.Append(targetUri.PathAndQuery);
                sb.Append(targetUri.Fragment);
                return sb.ToString();
            }
            if (targetUrl.Contains('%')) return targetUri.OriginalString;
            return targetUri.AbsoluteUri;
        }

        /// WWWTranscoder.URLDecode: '+' is a space, "%XX" a byte (only when two more bytes follow
        /// the '%'; Unity's hex parser also maps 'G'..'K' and yields '?' for other characters).
        static string UrlDecode(string encoded)
        {
            var input = Encoding.UTF8.GetBytes(encoded);
            using var output = new MemoryStream(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '+') output.WriteByte(32);
                else if (input[i] == '%' && i + 2 < input.Length)
                {
                    i++;
                    output.WriteByte(Hex2Byte(input, i++));
                }
                else output.WriteByte(input[i]);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }

        static byte Hex2Byte(byte[] b, int offset)
        {
            byte value = 0;
            for (int i = offset; i < offset + 2; i++)
            {
                value *= 16;
                int n = b[i];
                if (n >= 48 && n <= 57) n -= 48;
                else if (n >= 65 && n <= 75) n -= 55;
                else if (n >= 97 && n <= 102) n -= 87;
                if (n > 15) return 63;
                value += (byte)n;
            }
            return value;
        }
    }
}
