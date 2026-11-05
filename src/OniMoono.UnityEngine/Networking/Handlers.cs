using System;
using System.IO;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;

// Upload and download handlers as measured in the original (UnityProbe's apigaps "web", against
// a loopback server logging each raw request).
namespace UnityEngine.Networking
{
    public partial class UploadHandler
    {
        internal byte[] m_Data;
        string m_ContentType = "";
        bool m_Disposed;
        internal volatile WebRequestNative m_Request;

        internal UploadHandler() { }

        /// A copy of the body; null when it is empty or the handler disposed.
        public byte[] data => m_Data == null || m_Data.Length == 0 ? null : (byte[])m_Data.Clone();

        /// "" until set. Sent as the body's Content-Type unless the request sets that header;
        /// "" sends application/octet-stream. Reading it after Dispose throws.
        public string contentType
        {
            get
            {
                if (m_Disposed) throw new NullReferenceException();
                return m_ContentType;
            }
            set
            {
                if (m_Disposed) throw new NullReferenceException();
                m_ContentType = value ?? "";
            }
        }

        /// 0 until sent (1 for an empty body), then the share sent.
        public float progress
        {
            get
            {
                if (m_Data == null || m_Data.Length == 0) return 1f;
                var r = m_Request;
                return r == null ? 0f : r.UploadProgress;
            }
        }

        public void Dispose()
        {
            m_Disposed = true;
            m_Data = null;
        }
    }

    public sealed partial class UploadHandlerRaw
    {
        public UploadHandlerRaw(byte[] data) => m_Data = data == null ? null : (byte[])data.Clone();
    }

    /// The response body in memory.
    public sealed partial class DownloadHandlerBuffer
    {
        readonly object gate = new();
        MemoryStream body = new();
        string contentType;

        public DownloadHandlerBuffer() { }

        internal override byte[] ReceivedBytes()
        {
            lock (gate) return body?.ToArray();
        }

        internal override string ContentType
        {
            get { lock (gate) return contentType; }
        }

        internal override void OnBegin(string responseContentType)
        {
            lock (gate) contentType = responseContentType;
        }

        internal override void OnReceive(byte[] buffer, int count)
        {
            lock (gate) body?.Write(buffer, 0, count);
        }

        internal override void ReleaseData()
        {
            lock (gate) body = null;
        }

        /// The request's body as text (its download handler must be a DownloadHandlerBuffer).
        public static string GetContent(UnityWebRequest www) => ((DownloadHandlerBuffer)www.downloadHandler).text;
    }

    /// The response body written to a file. The file is made (with its folders) when the
    /// handler is, emptied unless appending; an error response's body is written too, and the
    /// file is deleted after a failed request when removeFileOnAbort is set.
    public sealed partial class DownloadHandlerFile
    {
        readonly object gate = new();
        readonly string path;
        FileStream file;

        public DownloadHandlerFile(string path) : this(path, false) { }

        public DownloadHandlerFile(string path, bool append)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path), "Path cannot be null or empty");
            this.path = path;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            file = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }

        public bool removeFileOnAbort { get; set; }

        protected override byte[] GetData() => throw new NotSupportedException("Raw data access is not supported");

        protected override string GetText() => throw new NotSupportedException("String access is not supported");

        internal override void OnReceive(byte[] buffer, int count)
        {
            lock (gate) file?.Write(buffer, 0, count);
        }

        internal override bool OnEnd(bool succeeded)
        {
            Close();
            if (!succeeded && removeFileOnAbort)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return base.OnEnd(succeeded);
        }

        // A protocol error's body is kept; only the request's end decides about the file.
        internal override void ReleaseData() => Close();

        void Close()
        {
            lock (gate)
            {
                file?.Dispose();
                file = null;
            }
        }
    }

    /// The response body decoded as a PNG or JPEG into a Texture2D with mipmaps, made on the
    /// main thread when first asked for; bytes that are no image fail the request.
    public sealed partial class DownloadHandlerTexture
    {
        readonly object gate = new();
        readonly bool readable;
        MemoryStream body = new();
        Texture2D decoded;
        bool made;

        public DownloadHandlerTexture() : this(true) { }

        public DownloadHandlerTexture(bool readable) => this.readable = readable;

        internal override byte[] ReceivedBytes()
        {
            lock (gate) return body?.ToArray();
        }

        internal override void OnReceive(byte[] buffer, int count)
        {
            lock (gate) body?.Write(buffer, 0, count);
        }

        internal override bool OnEnd(bool succeeded)
        {
            if (succeeded && !ImageConversion.LooksLikeImage(ReceivedBytes()))
            {
                m_Error = "Failed to create texture from downloaded bytes";
                m_Done = true;
                return false;
            }
            return base.OnEnd(succeeded);
        }

        internal override void ReleaseData()
        {
            lock (gate) body = null;
        }

        public Texture2D texture
        {
            get
            {
                if (!isDone) throw new InvalidOperationException("Texture has not yet finished downloading");
                if (made) return decoded;
                made = true;
                if (!string.IsNullOrEmpty(m_Error)) return null;
                var t = new Texture2D(2, 2);
                if (!t.LoadImage(ReceivedBytes(), !readable))
                {
                    Object.Destroy(t);
                    return null;
                }
                decoded = t;
                return t;
            }
        }

        public static Texture2D GetContent(UnityWebRequest www) => ((DownloadHandlerTexture)www.downloadHandler).texture;
    }

    public static partial class UnityWebRequestTexture
    {
        public static UnityWebRequest GetTexture(string uri) => GetTexture(uri, false);

        public static UnityWebRequest GetTexture(string uri, bool nonReadable)
            => new UnityWebRequest(uri, "GET", new DownloadHandlerTexture(!nonReadable), null);

        public static UnityWebRequest GetTexture(Uri uri) => GetTexture(uri, false);

        public static UnityWebRequest GetTexture(Uri uri, bool nonReadable)
            => new UnityWebRequest(uri, "GET", new DownloadHandlerTexture(!nonReadable), null);
    }
}
