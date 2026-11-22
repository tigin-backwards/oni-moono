// Smoke test of OniMoono.Desktop's Direct3D 11 declarations (src/OniMoono.Desktop/D3d11/), on
// Windows: for each DXGI adapter, a device at feature level 11_0 or above (flags BGRA_SUPPORT,
// as the original's), its feature level, creation flags, threading and D3D11_OPTIONS answers and
// a few formats' support, then the same with D3D11_CREATE_DEVICE_DEBUG and the debug layer's
// ID3D11InfoQueue and ID3D11Debug; everything released. Prints one block per adapter and exits 1
// when a call the test needs fails. Compare with docs/research/d3d11-captures.md §4.
//   tools/D3d11Smoke/run.sh
using OniMoono.Desktop.D3d11;
using static OniMoono.Desktop.D3d11.D3D_FEATURE_LEVEL;
using static OniMoono.Desktop.D3d11.D3D11_CREATE_DEVICE_FLAG;

static unsafe class Program
{
    static int failures;

    static void Check(int hr, string what)
    {
        if (hr < 0) { Console.WriteLine($"  FAIL {what}: 0x{hr:X8}"); failures++; }
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
            Com.Release(ref adapter);
        }
        Com.Release(ref factory);
        Console.WriteLine(failures == 0 ? "D3d11Smoke: ok" : $"D3d11Smoke: {failures} failures");
        return failures == 0 ? 0 : 1;
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
        Console.WriteLine($"  OPTIONS ConstantBufferOffsetting {o.ConstantBufferOffsetting} ConstantBufferPartialUpdate {o.ConstantBufferPartialUpdate} " +
            $"MapNoOverwriteOnDynamicConstantBuffer {o.MapNoOverwriteOnDynamicConstantBuffer} MapNoOverwriteOnDynamicBufferSRV {o.MapNoOverwriteOnDynamicBufferSRV} " +
            $"DiscardAPIsSeenByDriver {o.DiscardAPIsSeenByDriver} FlagsForUpdateAndCopySeenByDriver {o.FlagsForUpdateAndCopySeenByDriver} " +
            $"ClearView {o.ClearView} CopyWithOverlap {o.CopyWithOverlap} OutputMergerLogicOp {o.OutputMergerLogicOp}");
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
        if ((flags & D3D11_CREATE_DEVICE_DEBUG) != 0)
        {
            ID3D11InfoQueue* queue = null;
            ID3D11Debug* debug = null;
            Check(Com.QueryInterface(device, ID3D11InfoQueue.IID, (void**)&queue), "QueryInterface ID3D11InfoQueue");
            Check(Com.QueryInterface(device, ID3D11Debug.IID, (void**)&debug), "QueryInterface ID3D11Debug");
            if (queue != null)
            {
                ulong n = queue->lpVtbl->GetNumStoredMessages(queue);
                Console.WriteLine($"  ID3D11InfoQueue: {n} stored messages");
                for (ulong m = 0; m < n; m++)
                {
                    nuint length = 0;
                    Check(queue->lpVtbl->GetMessage(queue, m, null, &length), "GetMessage size");
                    var buffer = new byte[(int)length];
                    fixed (byte* b = buffer)
                    {
                        var message = (D3D11_MESSAGE*)b;
                        Check(queue->lpVtbl->GetMessage(queue, m, message, &length), "GetMessage");
                        Console.WriteLine($"   {message->Severity} {message->ID}: {new string((sbyte*)message->pDescription, 0, (int)message->DescriptionByteLength).TrimEnd('\0')}");
                    }
                }
                queue->lpVtbl->ClearStoredMessages(queue);
            }
            Com.Release(ref queue);
            Com.Release(ref debug);
        }
        Com.Release(ref context);
        Com.Release(ref device);
    }
}
