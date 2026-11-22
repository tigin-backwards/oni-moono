using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace OniMoono.Desktop.Gl
{
    /// Where one program build's time went, in Stopwatch ticks (GL thread).
    /// - Prepare: ours, before the driver: decoding the variant, the source edits, the extension
    ///   lines.
    /// - Vertex, Fragment, Geometry: the driver's compile of a stage, up to its status.
    /// - Link: attaching and glLinkProgram, until it returns.
    /// - LinkStatus: the first status read, where a driver that links later has to finish.
    /// - Reflect: attribute locations, uniforms and blocks.
    /// Cpu is the GL thread's CPU time over the build in the same ticks (-1: not known): far less
    /// than Total means the thread waited, for a core or for a driver thread.
    internal struct ProgramBuildSplit
    {
        public long Prepare, Vertex, Fragment, Geometry, Link, LinkStatus, Reflect, Total, Cpu;

        public void Add(in ProgramBuildSplit o)
        {
            Prepare += o.Prepare; Vertex += o.Vertex; Fragment += o.Fragment; Geometry += o.Geometry;
            Link += o.Link; LinkStatus += o.LinkStatus; Reflect += o.Reflect; Total += o.Total;
            Cpu = Cpu < 0 || o.Cpu < 0 ? -1 : Cpu + o.Cpu;
        }

        static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F0", CultureInfo.InvariantCulture);

        /// "preparation 1 ms, vertex compile 2 ms, …"; a geometry stage only when there was one.
        public readonly string Describe() =>
            $"preparation {Ms(Prepare)} ms, vertex compile {Ms(Vertex)} ms, fragment compile {Ms(Fragment)} ms, "
            + (Geometry > 0 ? $"geometry compile {Ms(Geometry)} ms, " : "")
            + $"link {Ms(Link)} ms, link status {Ms(LinkStatus)} ms, reflection {Ms(Reflect)} ms";

        public readonly string CpuText() => Cpu < 0 ? "CPU time not known" : $"{Ms(Cpu)} ms of CPU";
    }

    /// The builds of one kind (the warm-up's, or those at first use) since the last line about
    /// them: their count, the sum of their splits and the slowest few by name. GL thread only.
    internal sealed class ProgramBuildLog
    {
        public const int Slowest = 3;

        public int Count { get; private set; }
        ProgramBuildSplit sum;
        readonly List<(string Name, int Blob, ProgramBuildSplit Split)> slowest = new();

        public ProgramBuildSplit Sum => sum;

        public void Add(string shader, int blob, in ProgramBuildSplit split)
        {
            Count++;
            sum.Add(split);
            if (slowest.Count == Slowest && split.Total <= slowest[^1].Split.Total) return;
            int at = slowest.Count;
            while (at > 0 && slowest[at - 1].Split.Total < split.Total) at--;
            slowest.Insert(at, (shader, blob, split));
            if (slowest.Count > Slowest) slowest.RemoveAt(Slowest);
        }

        public void Reset()
        {
            Count = 0;
            sum = default;
            slowest.Clear();
        }

        /// "preparation … reflection 3 ms; slowest: A variant 2 9 ms (preparation 0, compile 7, link 2), B …".
        public string Describe()
        {
            var text = new StringBuilder(sum.Describe());
            for (int i = 0; i < slowest.Count; i++)
            {
                var (name, blob, s) = slowest[i];
                text.Append(i == 0 ? "; slowest: " : ", ").Append(name.Trim()).Append(" variant ").Append(blob).Append(' ').Append(Ms(s.Total)).Append(" ms (preparation ")
                    .Append(Ms(s.Prepare)).Append(", compile ").Append(Ms(s.Vertex + s.Fragment + s.Geometry)).Append(", link ").Append(Ms(s.Link + s.LinkStatus)).Append(')');
            }
            return text.ToString();
        }

        internal static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F0", CultureInfo.InvariantCulture);
    }

    /// The calling thread's CPU time in Stopwatch ticks; -1 where it is not read.
    internal static class ThreadCpuTime
    {
        [OniMoono.Desktop.D3d11.NotNative("an OpenGL window, context or timing call (opengl32, gdi32, dwmapi, kernel32), outside the D3D11 binding: not declared to the check")]
        [DllImport("kernel32")] static extern IntPtr GetCurrentThread();
        [OniMoono.Desktop.D3d11.NotNative("an OpenGL window, context or timing call (opengl32, gdi32, dwmapi, kernel32), outside the D3D11 binding: not declared to the check")]
        [DllImport("kernel32")] static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        [OniMoono.Desktop.D3d11.NotNative("a Linux library (libX11, libXrandr, libxkbcommon, libc), not in the Windows SDK")]
        struct Timespec
        {
            public long Seconds, Nanoseconds;
        }

        [OniMoono.Desktop.D3d11.NotNative("a Linux library (libX11, libXrandr, libxkbcommon, libc), not in the Windows SDK")]
        [DllImport("libc", EntryPoint = "clock_gettime")] static extern int ClockGetTime(int clock, out Timespec time);
        const int ClockThreadCpuTime = 3;

        static readonly bool windows = OperatingSystem.IsWindows(), linux = OperatingSystem.IsLinux();

        public static long Now
        {
            get
            {
                // 100 ns units on Windows, counted in clock ticks of about 16 ms: a short span
                // reads 0 or a whole tick, and only sums over many spans mean something.
                if (windows) return GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? (long)((kernel + user) * (Stopwatch.Frequency / 1e7)) : -1;
                if (linux) return ClockGetTime(ClockThreadCpuTime, out var t) == 0 ? (long)((t.Seconds * 1e9 + t.Nanoseconds) * (Stopwatch.Frequency / 1e9)) : -1;
                return -1;
            }
        }
    }
}
