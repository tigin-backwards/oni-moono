// Smoke test of OniMoono.Desktop's Direct3D 11 declarations (src/OniMoono.Desktop/D3d11/), on
// Windows. For each DXGI adapter, a device at feature level 11_0 or above (flags BGRA_SUPPORT, as
// the original's):
// - its feature level, creation flags, threading and all D3D11_OPTIONS answers, and a few
//   formats' support;
// - then the same with D3D11_CREATE_DEVICE_DEBUG, and through the debug layer's interfaces: the
//   messages stored at creation; one error provoked on purpose (a buffer of ByteWidth 0, which
//   CreateBuffer refuses with E_INVALIDARG) and its message read with GetMessage; the live-object
//   report (ID3D11Debug::ReportLiveDeviceObjects) and its messages;
// - every object released, the count each Release returns printed (IUnknown::Release returns the
//   new count, "for test purposes"), and the last Release of the device, the adapter and the
//   factory required to return 0.
// Exits 1 when any of that fails. Compare with docs/research/d3d11-captures.md §4.
//   tools/D3d11Smoke/run.sh
using OniMoono.Desktop.D3d11;
using static OniMoono.Desktop.D3d11.D3D_FEATURE_LEVEL;
using static OniMoono.Desktop.D3d11.D3D11_CREATE_DEVICE_FLAG;

static unsafe class Program
{
    static int failures;

    static void Fail(string what)
    {
        Console.WriteLine($"  FAIL {what}");
        failures++;
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) Fail($"{what}: 0x{hr:X8}");
    }

    /// Releases `p`'s reference and returns the count Release gives.
    static uint Release<T>(ref T* p) where T : unmanaged
    {
        var o = (IUnknown*)p;
        p = null;
        return o->lpVtbl->Release(o);
    }

    static string Level(D3D_FEATURE_LEVEL l) => l switch
    {
        D3D_FEATURE_LEVEL_11_1 => "11_1",
        D3D_FEATURE_LEVEL_11_0 => "11_0",
        _ => $"0x{(int)l:x}",
    };

    static int Main()
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("D3d11Smoke: Windows only"); return 2; }
        IDXGIFactory1* factory = null;
        var iid = IDXGIFactory1.IID;
        Check(DXGI.CreateDXGIFactory2(0, &iid, (void**)&factory), "CreateDXGIFactory2");
        if (factory == null) return 1;
        for (uint i = 0; ; i++)
        {
            IDXGIAdapter1* adapter = null;
            int hr = factory->lpVtbl->EnumAdapters1(factory, i, &adapter);
            if (hr == D3D11.DXGI_ERROR_NOT_FOUND) break;
            Check(hr, "EnumAdapters1");
            if (hr < 0) break;
            OniMoono.Desktop.Gl.Dxgi.AdapterDesc1 desc;
            Check(adapter->lpVtbl->GetDesc1(adapter, &desc), "GetDesc1");
            Console.WriteLine($"adapter {i}: {desc.Name} vendor 0x{desc.VendorId:x4} device 0x{desc.DeviceId:x4} luid {desc.Luid:x16} flags {desc.Flags}" +
                ((desc.Flags & OniMoono.Desktop.Gl.Dxgi.AdapterFlagSoftware) != 0 ? " (software)" : ""));
            Device(adapter, D3D11_CREATE_DEVICE_BGRA_SUPPORT);
            Device(adapter, D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_DEBUG);
            uint left = Release(ref adapter);
            Console.WriteLine($" adapter Release: {left}");
            if (left != 0) Fail($"adapter {i}'s last Release returned {left}");
        }
        uint factoryLeft = Release(ref factory);
        Console.WriteLine($"factory Release: {factoryLeft}");
        if (factoryLeft != 0) Fail($"the factory's last Release returned {factoryLeft}");
        Console.WriteLine(failures == 0 ? "D3d11Smoke: ok" : $"D3d11Smoke: {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    /// The queue's messages, read as ID3D11InfoQueue::GetMessage's documentation says: an index
    /// below GetNumStoredMessagesAllowedByRetrievalFilter, the size asked for first (pMessage
    /// null), then the message (learn.microsoft.com/windows/win32/api/d3d11sdklayers/nf-d3d11sdklayers-id3d11infoqueue-getmessage).
    static List<(D3D11_MESSAGE_SEVERITY Severity, int Id, string Text)> Messages(ID3D11InfoQueue* queue)
    {
        var list = new List<(D3D11_MESSAGE_SEVERITY, int, string)>();
        ulong n = queue->lpVtbl->GetNumStoredMessagesAllowedByRetrievalFilter(queue);
        for (ulong m = 0; m < n; m++)
        {
            nuint length = 0;
            Check(queue->lpVtbl->GetMessage(queue, m, null, &length), "GetMessage (size)");
            if (length < (nuint)sizeof(D3D11_MESSAGE)) { Fail($"GetMessage gave {length} bytes, less than a D3D11_MESSAGE"); continue; }
            var buffer = new byte[(int)length];
            fixed (byte* b = buffer)
            {
                var message = (D3D11_MESSAGE*)b;
                Check(queue->lpVtbl->GetMessage(queue, m, message, &length), "GetMessage");
                // DescriptionByteLength counts the terminating zero.
                if (message->pDescription == null || message->DescriptionByteLength == 0) { Fail("a message without a description"); continue; }
                list.Add((message->Severity, message->ID, new string(message->pDescription, 0, (int)message->DescriptionByteLength - 1)));
            }
        }
        queue->lpVtbl->ClearStoredMessages(queue);
        return list;
    }

    static void Print(string what, List<(D3D11_MESSAGE_SEVERITY Severity, int Id, string Text)> messages)
    {
        Console.WriteLine($"  {what}: {messages.Count} messages");
        foreach (var (severity, id, text) in messages.Take(6)) Console.WriteLine($"   {severity} id {id}: {text}");
    }

    static void Device(IDXGIAdapter1* adapter, D3D11_CREATE_DEVICE_FLAG flags)
    {
        var levels = stackalloc D3D_FEATURE_LEVEL[] { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;
        D3D_FEATURE_LEVEL level = 0;
        int hr = D3D11.D3D11CreateDevice((IDXGIAdapter*)adapter, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero, flags, levels, 2,
            D3D11.D3D11_SDK_VERSION, &device, &level, &context);
        Console.WriteLine($" flags {flags}: D3D11CreateDevice 0x{hr:X8} level {(hr >= 0 ? Level(level) : "-")}");
        if (hr < 0) { failures++; return; }
        Console.WriteLine($"  GetFeatureLevel {Level(device->lpVtbl->GetFeatureLevel(device))} GetCreationFlags 0x{device->lpVtbl->GetCreationFlags(device):x}");
        D3D11_FEATURE_DATA_THREADING threading;
        Check(device->lpVtbl->CheckFeatureSupport(device, D3D11_FEATURE.D3D11_FEATURE_THREADING, &threading, (uint)sizeof(D3D11_FEATURE_DATA_THREADING)), "THREADING");
        Console.WriteLine($"  THREADING DriverConcurrentCreates {threading.DriverConcurrentCreates} DriverCommandLists {threading.DriverCommandLists}");
        D3D11_FEATURE_DATA_D3D11_OPTIONS o;
        Check(device->lpVtbl->CheckFeatureSupport(device, D3D11_FEATURE.D3D11_FEATURE_D3D11_OPTIONS, &o, (uint)sizeof(D3D11_FEATURE_DATA_D3D11_OPTIONS)), "D3D11_OPTIONS");
        Console.WriteLine($"  OPTIONS OutputMergerLogicOp {o.OutputMergerLogicOp} UAVOnlyRenderingForcedSampleCount {o.UAVOnlyRenderingForcedSampleCount} " +
            $"DiscardAPIsSeenByDriver {o.DiscardAPIsSeenByDriver} FlagsForUpdateAndCopySeenByDriver {o.FlagsForUpdateAndCopySeenByDriver} ClearView {o.ClearView} " +
            $"CopyWithOverlap {o.CopyWithOverlap} ConstantBufferPartialUpdate {o.ConstantBufferPartialUpdate} ConstantBufferOffsetting {o.ConstantBufferOffsetting} " +
            $"MapNoOverwriteOnDynamicConstantBuffer {o.MapNoOverwriteOnDynamicConstantBuffer} MapNoOverwriteOnDynamicBufferSRV {o.MapNoOverwriteOnDynamicBufferSRV} " +
            $"MultisampleRTVWithForcedSampleCountOne {o.MultisampleRTVWithForcedSampleCountOne} SAD4ShaderInstructions {o.SAD4ShaderInstructions} " +
            $"ExtendedDoublesShaderInstructions {o.ExtendedDoublesShaderInstructions} ExtendedResourceSharing {o.ExtendedResourceSharing}");
        var formats = new[] { DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT, DXGI_FORMAT.DXGI_FORMAT_R32G8X24_TYPELESS, DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS,
            DXGI_FORMAT.DXGI_FORMAT_D24_UNORM_S8_UINT, DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT };
        var support = new List<string>();
        foreach (var f in formats)
        {
            D3D11_FORMAT_SUPPORT s = 0;
            Check(device->lpVtbl->CheckFormatSupport(device, f, &s), $"CheckFormatSupport {f}");
            support.Add($"{(int)f} 0x{(int)s:x}");
        }
        Console.WriteLine($"  CheckFormatSupport {string.Join(", ", support)}");
        ID3D11InfoQueue* queue = null;
        ID3D11Debug* debug = null;
        if ((flags & D3D11_CREATE_DEVICE_DEBUG) != 0)
        {
            Check(Com.QueryInterface(device, ID3D11InfoQueue.IID, (void**)&queue), "QueryInterface ID3D11InfoQueue");
            Check(Com.QueryInterface(device, ID3D11Debug.IID, (void**)&debug), "QueryInterface ID3D11Debug");
            if (queue != null)
            {
                Print("ID3D11InfoQueue after creation", Messages(queue));
                // A buffer of no bytes: CreateBuffer returns E_INVALIDARG, and the debug layer says why.
                var bad = new D3D11_BUFFER_DESC { ByteWidth = 0, Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER };
                ID3D11Buffer* buffer = null;
                hr = device->lpVtbl->CreateBuffer(device, &bad, null, &buffer);
                Console.WriteLine($"  CreateBuffer(ByteWidth 0): 0x{hr:X8}");
                if (hr != D3D11.E_INVALIDARG) Fail($"CreateBuffer(ByteWidth 0) returned 0x{hr:X8}, not E_INVALIDARG");
                if (buffer != null) Com.Release(ref buffer);
                var provoked = Messages(queue);
                Print("after CreateBuffer(ByteWidth 0)", provoked);
                if (!provoked.Any(m => m.Severity == D3D11_MESSAGE_SEVERITY.D3D11_MESSAGE_SEVERITY_ERROR)) Fail("no error message for CreateBuffer(ByteWidth 0)");
            }
        }
        Console.WriteLine($"  context Release: {Release(ref context)}");
        if (debug != null)
        {
            Check(debug->lpVtbl->ReportLiveDeviceObjects(debug, D3D11_RLDO_FLAGS.D3D11_RLDO_DETAIL), "ReportLiveDeviceObjects");
            if (queue != null) Print("after ReportLiveDeviceObjects", Messages(queue));
            Console.WriteLine($"  ID3D11Debug Release: {Release(ref debug)}");
        }
        if (queue != null) Console.WriteLine($"  ID3D11InfoQueue Release: {Release(ref queue)}");
        uint left = Release(ref device);
        Console.WriteLine($"  device Release: {left}");
        if (left != 0) Fail($"the device's last Release returned {left}");
    }
}
