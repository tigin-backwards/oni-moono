using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

// TEST TOOLING: AllocTrace TRACE.nettrace OUT.tsv — an allocation trace's samples, one a line:
// time (ms from the trace's start), bytes the sample stands for, kind (Small, Large, Pinned), type,
// OS thread id, and the managed stack innermost first as MODULE!METHOD joined by " <- ". The trace
// is dotnet-trace's with the runtime provider's GC keyword at verbose level
// (Microsoft-Windows-DotNETRuntime:0x1:5): an AllocationTick about every 100 KB a thread hands out,
// with its stack. scripts/alloc-compare.py split reads the file.
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: AllocTrace TRACE.nettrace OUT.tsv");
    return 2;
}
string etlx = TraceLog.CreateFromEventPipeDataFile(args[0]);
using var log = new TraceLog(etlx);
using var output = new StreamWriter(args[1], false, new UTF8Encoding(false));
var source = log.Events.GetSource();
long samples = 0;
var frames = new List<string>();
source.Clr.GCAllocationTick += (GCAllocationTickTraceData data) =>
{
    frames.Clear();
    for (var stack = data.CallStack(); stack != null && frames.Count < 64; stack = stack.Caller)
    {
        var address = stack.CodeAddress;
        string method = address.FullMethodName;
        if (string.IsNullOrEmpty(method)) method = "?";
        frames.Add((address.ModuleName ?? "?") + "!" + method.Replace('\t', ' '));
    }
    output.Write(data.TimeStampRelativeMSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    output.Write('\t');
    output.Write(data.AllocationAmount64);
    output.Write('\t');
    output.Write(data.AllocationKind);
    output.Write('\t');
    output.Write((data.TypeName ?? "?").Replace('\t', ' '));
    output.Write('\t');
    output.Write(data.ThreadID);
    output.Write('\t');
    output.Write(string.Join(" <- ", frames));
    output.Write('\n');
    samples++;
};
source.Process();
Console.WriteLine($"{samples} samples to {args[1]}");
try { File.Delete(etlx); } catch (IOException) { }
return 0;
