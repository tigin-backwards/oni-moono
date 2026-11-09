using System;
using System.Collections.Generic;
using System.Text;
using OniMoono.Engine;

namespace UnityEngine.Networking
{
    public partial class DownloadHandler
    {
        internal bool m_Disposed;

        internal DownloadHandler() { }

        public byte[] data => GetData();
        public string text => GetText();

        /// True once the response was taken whole: a request that succeeded (or whose
        /// processing failed); not after a protocol or connection error.
        public bool isDone => m_Done;

        /// Why the handler's processing failed ("" otherwise).
        public string error => m_Error ?? "";

        internal volatile bool m_Done;
        internal volatile string m_Error;

        // The transfer's calls (pool thread): the response's Content-Type, each chunk of the
        // body, and the end (false: a protocol or connection error). OnEnd(true) returns false
        // when processing the body fails, with m_Error set.
        internal virtual void OnBegin(string responseContentType) { }
        internal virtual void OnReceive(byte[] buffer, int count) { }
        internal virtual bool OnEnd(bool succeeded)
        {
            if (succeeded) m_Done = true;
            return true;
        }

        /// A copy of the bytes received so far; null when nothing has been received.
        protected virtual byte[] GetData()
        {
            CheckAlive();
            var bytes = ReceivedBytes();
            return bytes == null || bytes.Length == 0 ? null : bytes;
        }

        /// The received bytes decoded with the response's charset (UTF-8 when absent).
        protected virtual string GetText()
        {
            CheckAlive();
            var bytes = ReceivedBytes();
            if (bytes == null || bytes.Length == 0) return "";
            return GetTextEncoder().GetString(bytes);
        }

        /// Releases the received data; reading it afterwards throws.
        public virtual void Dispose()
        {
            m_Disposed = true;
            ReleaseData();
        }

        internal virtual void ReleaseData() { }

        /// The handler's bytes (a fresh array), or null for a handler that stores nothing.
        internal virtual byte[] ReceivedBytes() => null;

        /// The response Content-Type seen by this handler.
        internal virtual string ContentType => null;

        void CheckAlive()
        {
            if (m_Disposed) throw new NullReferenceException();
        }

        /// Unity's rule: the charset parameter of the Content-Type, quotes trimmed.
        Encoding GetTextEncoder()
        {
            string contentType = ContentType;
            if (!string.IsNullOrEmpty(contentType))
            {
                int charset = contentType.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
                if (charset > -1)
                {
                    int eq = contentType.IndexOf('=', charset);
                    if (eq > -1)
                    {
                        string name = contentType.Substring(eq + 1).Trim().Trim('\'', '"').Trim();
                        int semicolon = name.IndexOf(';');
                        if (semicolon > -1) name = name.Substring(0, semicolon);
                        try
                        {
                            return Encoding.GetEncoding(name);
                        }
                        catch (ArgumentException e)
                        {
                            Debug.LogWarning($"Unsupported encoding '{name}': {e.Message}");
                        }
                        catch (NotSupportedException e)
                        {
                            Debug.LogWarning($"Unsupported encoding '{name}': {e.Message}");
                        }
                    }
                }
            }
            return Encoding.UTF8;
        }
    }

    public partial class UnityWebRequestAsyncOperation
    {
        public UnityWebRequestAsyncOperation() { }

        public UnityWebRequest webRequest { get; internal set; }

        internal WebRequestNative m_Request;

        // Done when the transfer ends, on whatever thread reads it; completed still runs on the
        // main thread (WebRequests.Update).
        internal override bool WorkDone => m_Request != null && m_Request.State == WebRequestNative.Done;
    }

    public partial class UnityWebRequest
    {
        public const string kHttpVerbGET = "GET";
        public const string kHttpVerbHEAD = "HEAD";
        public const string kHttpVerbPOST = "POST";
        public const string kHttpVerbPUT = "PUT";
        public const string kHttpVerbCREATE = "CREATE";
        public const string kHttpVerbDELETE = "DELETE";

        // The native request (null once disposed).
        internal WebRequestNative m_Native;
        internal DownloadHandler m_DownloadHandler;
        internal Uri m_Uri;

        public bool disposeDownloadHandlerOnDispose { get; set; } = true;
        public bool disposeUploadHandlerOnDispose { get; set; } = true;
        public bool disposeCertificateHandlerOnDispose { get; set; } = true;

        public UnityWebRequest() => m_Native = new WebRequestNative();

        public UnityWebRequest(string url) : this() => this.url = url;

        public UnityWebRequest(Uri uri) : this() => this.uri = uri;

        public UnityWebRequest(string url, string method) : this()
        {
            this.url = url;
            this.method = method;
        }

        public UnityWebRequest(Uri uri, string method) : this()
        {
            this.uri = uri;
            this.method = method;
        }

        public UnityWebRequest(string url, string method, DownloadHandler downloadHandler, UploadHandler uploadHandler) : this(url, method)
        {
            this.downloadHandler = downloadHandler;
            this.uploadHandler = uploadHandler;
        }

        public UnityWebRequest(Uri uri, string method, DownloadHandler downloadHandler, UploadHandler uploadHandler) : this(uri, method)
        {
            this.downloadHandler = downloadHandler;
            this.uploadHandler = uploadHandler;
        }

        internal UploadHandler m_UploadHandler;

        /// The body sent: its data, and its contentType unless a Content-Type header is set
        /// (none: application/octet-stream on the wire).
        public UploadHandler uploadHandler
        {
            get => m_UploadHandler;
            set
            {
                if (!isModifiable) throw new InvalidOperationException("UnityWebRequest has already been sent; cannot modify the upload handler");
                m_UploadHandler = value;
            }
        }

        /// False: requests are never sent chunked (the original's default).
        public bool chunkedTransfer => false;

        /// True, the original's default (curl's Expect: 100-continue for large bodies).
        public bool useHttpContinue => true;

        /// A request header the script set, or null.
        public string GetRequestHeader(string name) => Native.GetRequestHeader(name);

        /// Throws Unity's NullReferenceException for a disposed request.
        WebRequestNative Native => m_Native ?? throw new NullReferenceException();

        public string method
        {
            get => Native.Method;
            set
            {
                if (string.IsNullOrEmpty(value)) throw new ArgumentException("Cannot set a UnityWebRequest's method to an empty or null string");
                if (!isModifiable) throw new InvalidOperationException("UnityWebRequest has already been sent and its request method can no longer be altered");
                Native.Method = value.ToUpper();
            }
        }

        /// Null while in progress or on success; "HTTP/1.1 &lt;code&gt; &lt;reason&gt;" for a
        /// protocol error; otherwise the connection error's description.
        public string error
        {
            get
            {
                switch (result)
                {
                    case Result.InProgress:
                    case Result.Success:
                        return null;
                    case Result.ProtocolError:
                        return $"HTTP/1.1 {responseCode} {WebRequestNative.HttpStatusString(responseCode)}";
                    default:
                        return WebRequestNative.ErrorString(Native.Error);
                }
            }
        }

        /// Relative URLs resolve against https://localhost/, as in a standalone player.
        public string url
        {
            get => Native.Url;
            set
            {
                if (!isModifiable) throw new InvalidOperationException("UnityWebRequest has already been sent and its URL cannot be altered");
                Native.Url = WebRequestUrls.MakeInitialUrl(value, "https://localhost/");
            }
        }

        public Uri uri
        {
            get => new Uri(Native.Url);
            set
            {
                if (!value.IsAbsoluteUri) throw new ArgumentException("URI must be absolute");
                if (!isModifiable) throw new InvalidOperationException("UnityWebRequest has already been sent and its URL cannot be altered");
                Native.Url = WebRequestUrls.MakeUriString(value, value.OriginalString, prependProtocol: false);
                m_Uri = value;
            }
        }

        /// The HTTP status of the last response; -1 until a response arrives. Local file
        /// requests report 0.
        public long responseCode => Native.ResponseCode;

        /// -1 before the request is sent; then bytes sent / body size (1 once the server
        /// responds to a request without a body).
        public float uploadProgress
        {
            get
            {
                var n = Native;
                if (!n.IsExecuting && !isDone) return -1f;
                return n.UploadProgress;
            }
        }

        /// -1 before the request is sent; then bytes received / Content-Length (0 while the
        /// length is unknown, 1 when done).
        public float downloadProgress
        {
            get
            {
                var n = Native;
                if (!n.IsExecuting && !isDone) return -1f;
                return n.DownloadProgress;
            }
        }

        public ulong uploadedBytes => (ulong)Native.UploadedBytes;
        public ulong downloadedBytes => (ulong)Native.DownloadedBytes;

        public bool isModifiable => Native.State == WebRequestNative.NotSent;

        public bool isDone => result != Result.InProgress;

        public Result result => Native.Result;

        public DownloadHandler downloadHandler
        {
            get => m_DownloadHandler;
            set
            {
                if (!isModifiable) throw new InvalidOperationException("UnityWebRequest has already been sent; cannot modify the download handler");
                m_DownloadHandler = value;
            }
        }

        /// Whole-request timeout in seconds; 0 means none.
        public int timeout
        {
            get => Native.TimeoutMsec / 1000;
            set
            {
                if (!isModifiable) throw new InvalidOperationException("UnityWebRequest has already been sent; cannot modify the timeout");
                Native.TimeoutMsec = Math.Max(value, 0) * 1000;
            }
        }

        public void SetRequestHeader(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Cannot set a Request Header with a null or empty name");
            if (value == null) throw new ArgumentException("Cannot set a Request header with a null");
            if (!isModifiable) throw new InvalidOperationException("UnityWebRequest has already been sent and its request headers cannot be altered");
            var err = Native.SetRequestHeader(name, value);
            if (err != WebRequestError.OK) throw new InvalidOperationException(WebRequestNative.ErrorString(err));
        }

        /// The value of a response header (names are case-insensitive), or null.
        public string GetResponseHeader(string name) => Native.GetResponseHeader(name);

        /// All response headers, or null when no response has been received.
        public Dictionary<string, string> GetResponseHeaders()
        {
            var headers = Native.ResponseHeaders;
            if (headers == null || headers.Count == 0) return null;
            return new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        }

        /// Starts the request on a background thread. The returned operation's isDone turns when
        /// the request finishes; it completes (its completed callbacks run) on the main thread
        /// during the frame after.
        public UnityWebRequestAsyncOperation SendWebRequest()
        {
            Threads.AssertMainThread("SendWebRequest");
            var op = Native.Begin(this);
            op.webRequest = this;
            return op;
        }

        /// Halts an in-progress request (result ConnectionError, "Request aborted"). A request
        /// that has not been sent ends the same way; a finished one is unaffected.
        public void Abort() => m_Native?.Abort();

        public void Dispose()
        {
            if (disposeDownloadHandlerOnDispose) m_DownloadHandler?.Dispose();
            if (disposeUploadHandlerOnDispose) m_UploadHandler?.Dispose();
            m_Native?.Abort();
            m_Native = null;
            GC.SuppressFinalize(this);
        }

        public static UnityWebRequest Get(string uri) => WithBuffer(new UnityWebRequest(uri, "GET"));

        public static UnityWebRequest Get(Uri uri) => WithBuffer(new UnityWebRequest(uri, "GET"));

        public static UnityWebRequest Delete(string uri) => new UnityWebRequest(uri, "DELETE");

        public static UnityWebRequest Delete(Uri uri) => new UnityWebRequest(uri, "DELETE");

        public static UnityWebRequest Head(string uri) => new UnityWebRequest(uri, "HEAD");

        public static UnityWebRequest Head(Uri uri) => new UnityWebRequest(uri, "HEAD");

        /// postData, URL-escaped, as form data (an upload handler with content type
        /// application/x-www-form-urlencoded); none for "" or null.
        public static UnityWebRequest Post(string uri, string postData)
        {
            var request = new UnityWebRequest(uri, "POST");
            SetupPostForm(request, postData);
            return request;
        }

        public static UnityWebRequest Post(Uri uri, string postData)
        {
            var request = new UnityWebRequest(uri, "POST");
            SetupPostForm(request, postData);
            return request;
        }

        static void SetupPostForm(UnityWebRequest request, string postData)
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            if (string.IsNullOrEmpty(postData)) return;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(WebRequestNative.EscapeForm(postData))) { contentType = "application/x-www-form-urlencoded" };
        }

        public static UnityWebRequest Post(string uri, List<IMultipartFormSection> multipartFormSections)
            => Post(uri, multipartFormSections, GenerateBoundary());

        public static UnityWebRequest Post(Uri uri, List<IMultipartFormSection> multipartFormSections)
            => Post(uri, multipartFormSections, GenerateBoundary());

        public static UnityWebRequest Post(string uri, List<IMultipartFormSection> multipartFormSections, byte[] boundary)
        {
            var request = new UnityWebRequest(uri, "POST");
            SetupPost(request, multipartFormSections, boundary);
            return request;
        }

        public static UnityWebRequest Post(Uri uri, List<IMultipartFormSection> multipartFormSections, byte[] boundary)
        {
            var request = new UnityWebRequest(uri, "POST");
            SetupPost(request, multipartFormSections, boundary);
            return request;
        }

        /// A buffer download handler; the body (when there are sections) is uploaded raw with
        /// content type "multipart/form-data; boundary=&lt;boundary&gt;".
        static void SetupPost(UnityWebRequest request, List<IMultipartFormSection> multipartFormSections, byte[] boundary)
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            byte[] body = null;
            if (multipartFormSections != null && multipartFormSections.Count != 0) body = SerializeFormSections(multipartFormSections, boundary);
            if (body != null)
                request.Native.SetUpload(body, "multipart/form-data; boundary=" + Encoding.UTF8.GetString(boundary, 0, boundary.Length));
        }

        static UnityWebRequest WithBuffer(UnityWebRequest request)
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            return request;
        }

        /// Each section: CRLF "--" boundary CRLF, Content-Disposition (name, filename when
        /// present), Content-Type when present, CRLF, data; then CRLF "--" boundary "--" CRLF.
        public static byte[] SerializeFormSections(List<IMultipartFormSection> multipartFormSections, byte[] boundary)
        {
            if (multipartFormSections == null || multipartFormSections.Count == 0) return null;
            byte[] crlf = Encoding.UTF8.GetBytes("\r\n");
            byte[] dashes = Encoding.ASCII.GetBytes("--");
            int capacity = 0;
            foreach (var section in multipartFormSections) capacity += 64 + section.sectionData.Length;
            var bytes = new List<byte>(capacity);
            foreach (var section in multipartFormSections)
            {
                string header = "Content-Disposition: form-data";
                string sectionName = section.sectionName;
                string fileName = section.fileName;
                if (!string.IsNullOrEmpty(sectionName)) header += "; name=\"" + sectionName + "\"";
                if (!string.IsNullOrEmpty(fileName)) header += "; filename=\"" + fileName + "\"";
                header += "\r\n";
                string contentType = section.contentType;
                if (!string.IsNullOrEmpty(contentType)) header += "Content-Type: " + contentType + "\r\n";
                bytes.AddRange(crlf);
                bytes.AddRange(dashes);
                bytes.AddRange(boundary);
                bytes.AddRange(crlf);
                bytes.AddRange(Encoding.UTF8.GetBytes(header));
                bytes.AddRange(crlf);
                bytes.AddRange(section.sectionData);
            }
            bytes.AddRange(crlf);
            bytes.AddRange(dashes);
            bytes.AddRange(boundary);
            bytes.AddRange(dashes);
            bytes.AddRange(crlf);
            return bytes.ToArray();
        }

        /// 40 random characters from 0-9, A-Z, a-z (UnityEngine.Random).
        public static byte[] GenerateBoundary()
        {
            var boundary = new byte[40];
            for (int i = 0; i < 40; i++)
            {
                int c = Random.Range(48, 110);
                if (c > 57) c += 7;
                if (c > 90) c += 6;
                boundary[i] = (byte)c;
            }
            return boundary;
        }
    }
}
