using System;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;

namespace OniMoono.Desktop.D3d11
{
    /// Why the Direct3D 11 renderer cannot start here (the reason goes to the log).
    internal sealed class D3d11UnavailableException(string message) : Exception(message);

    /// The adapter a Direct3D 11 start would use, read before the device is made (the start-up
    /// guard's key, the deny list). `Driver` is the user-mode driver's version as
    /// IDXGIAdapter::CheckInterfaceSupport gives it (D3d11Context.DriverVersion).
    internal readonly record struct D3d11GpuIdentity(uint VendorId, uint DeviceId, string Driver, string Name, bool Software);

    /// The DXGI factory, the chosen adapter and the Direct3D 11 device with its immediate context.
    /// - The adapter: `--gpu default` takes the factory's first (EnumAdapters1 index 0, the adapter
    ///   of the desktop's primary output); `discrete` and `integrated` ask DXGI for the high
    ///   performance or minimum power GPU (IDXGIFactory6::EnumAdapterByGpuPreference). A software
    ///   adapter only when `allowSoftware` (an explicit `--renderer d3d11`, as Vulkan's lavapipe).
    ///   Every adapter is logged with the one chosen (d3d11-plan.md, "M5", the GPU choice rule).
    /// - `admit`, given the chosen adapter before the device is made, returns why not to go on
    ///   (the deny list, the start-up guard) or null.
    /// - Fault hooks (D3d11Fault): `factory`, `adapter`, `device`, `debug` fail after their step.
    /// - The device: feature level 11_1 or 11_0 (the shipped programs need 11_0:
    ///   docs/research/d3d11-plan.md "Programs"), flags BGRA_SUPPORT as the original's
    ///   (d3d11-unity-probes.md, "Device FL 11_1, flags BGRA only"), plus DEBUG with Validation.
    internal sealed unsafe class D3d11Context : IDisposable
    {
        /// `--d3d11-debug`: the device is made with the debug layer (D3D11_CREATE_DEVICE_DEBUG),
        /// whose messages the device logs.
        public static bool Validation;

        public IDXGIFactory2* Factory { get; private set; }
        public ID3D11Device* Device { get; private set; }
        public ID3D11DeviceContext* Context { get; private set; }
        public ID3D11InfoQueue* InfoQueue { get; private set; }
        public D3D_FEATURE_LEVEL FeatureLevel { get; private set; }
        public Dxgi.AdapterDesc1 Adapter { get; private set; }
        public bool Tearing { get; private set; }
        public D3d11GpuIdentity Identity { get; private set; }

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new D3d11UnavailableException($"{what} failed ({Dxgi.Hr(hr)})");
        }

        public D3d11Context(string gpu, bool allowSoftware = true, Func<D3d11GpuIdentity, string> admit = null)
        {
            try
            {
                Open(gpu, allowSoftware, admit);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        void Open(string gpu, bool allowSoftware, Func<D3d11GpuIdentity, string> admit)
        {
            if (!OperatingSystem.IsWindows()) throw new D3d11UnavailableException("Direct3D 11 is Windows only");
            IDXGIFactory2* factory;
            var iid = IDXGIFactory2.IID;
            Check(DXGI.CreateDXGIFactory2(0, &iid, (void**)&factory), "CreateDXGIFactory2");
            Factory = factory;
            D3d11Fault.FailAt("factory");
            LogAdapters();
            IDXGIAdapter1* adapter = ChooseAdapter(gpu);
            try
            {
                Dxgi.AdapterDesc1 desc;
                Check(adapter->lpVtbl->GetDesc1(adapter, &desc), "IDXGIAdapter1::GetDesc1");
                Adapter = desc;
                bool software = (desc.Flags & Dxgi.AdapterFlagSoftware) != 0;
                Identity = new D3d11GpuIdentity(desc.VendorId, desc.DeviceId, DriverVersion(adapter), desc.Name, software);
                EngineLog.Engine($"d3d11: gpu option {gpu ?? "default"}: {desc.Name} (vendor 0x{desc.VendorId:x4}, device 0x{desc.DeviceId:x4}, "
                                 + $"luid {desc.Luid:x}, driver {Identity.Driver}{(software ? ", software" : "")})");
                D3d11Fault.FailAt("adapter");
                if (software && !allowSoftware) throw new D3d11UnavailableException($"{desc.Name} is a software adapter (only used with --renderer d3d11)");
                if (admit?.Invoke(Identity) is { } refused) throw new D3d11UnavailableException(refused);
                var levels = stackalloc D3D_FEATURE_LEVEL[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
                var flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | (Validation ? D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_DEBUG : 0);
                ID3D11Device* d;
                ID3D11DeviceContext* c;
                D3D_FEATURE_LEVEL level;
                // An adapter given: the driver type is UNKNOWN (D3D11CreateDevice remarks).
                Check(D3D11.D3D11CreateDevice((IDXGIAdapter*)adapter, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero, flags, levels, 2,
                    D3D11.D3D11_SDK_VERSION, &d, &level, &c), $"D3D11CreateDevice on {desc.Name}{(Validation ? " with the debug layer" : "")}");
                Device = d;
                Context = c;
                FeatureLevel = level;
                D3d11Fault.FailAt("device");
            }
            finally
            {
                Com.Release(ref adapter);
            }
            if (Validation)
            {
                ID3D11InfoQueue* queue;
                Check(Com.QueryInterface(Device, ID3D11InfoQueue.IID, (void**)&queue), "QueryInterface ID3D11InfoQueue");
                InfoQueue = queue;
                SetFilters(queue);
                D3d11Fault.FailAt("debug");
            }
            IDXGIFactory5* f5;
            if (Com.QueryInterface(Factory, IDXGIFactory5.IID, (void**)&f5) >= 0)
            {
                int allow = 0;
                Tearing = f5->lpVtbl->CheckFeatureSupport(f5, Dxgi.FeaturePresentAllowTearing, &allow, D3D11.SizeOfBool) >= 0 && allow != 0;
                Com.Release(ref f5);
            }
        }

        /// The debug layer's filters, made known: the storage and retrieval filters the layer
        /// starts with are logged (GetStorageFilter, GetRetrievalFilter: "Get the storage filter
        /// at the top of the storage-filter stack"; "If pFilter is NULL, the size of the storage
        /// filter will be output to this parameter"), then an empty filter is pushed on each
        /// stack ("An empty storage filter allows all messages to pass through",
        /// PushEmptyStorageFilter; PushEmptyRetrievalFilter likewise), so nothing is
        /// held back by a default this device did not choose (the DirectX Control Panel's
        /// settings among them). On the empty storage filter only
        /// DEVICE_DRAW_RENDERTARGETVIEW_NOT_SET is denied (the user's decision, d3d11-plan.md
        /// "M3", B2 item 7): AddStorageFilterEntries ("Add storage filters to the top of the
        /// storage-filter stack") with that ID in the DenyList ("Types of messages that you want
        /// to deny", D3D11_INFO_QUEUE_FILTER). The draws it names bind as the original's do (a
        /// pixel stage writing more targets than are bound; the warning itself says "writes of an
        /// unbound Render Target View are discarded"). So GetNumMessagesDeniedByStorageFilter
        /// counts that warning alone, and every other message is stored: INFO and MESSAGE
        /// severities are counted and logged apart, the others fail a check (D3d11Device.
        /// DrainDebugMessages). With INFO stored (every object made is one), the queue's limit
        /// filled between drains while a colony loaded and dropped messages ("new messages coming
        /// in will push old messages out"), so the limit is lifted: "-1 means no limit"
        /// (SetMessageCountLimit); the queue is cleared at every drain. Before this the layer's own default denied about 2,600 messages
        /// in a run that draws nothing (j0008854), unread (d3d11-plan.md, M3 review fixes,
        /// finding 2).
        static void SetFilters(ID3D11InfoQueue* queue)
        {
            EngineLog.Engine($"d3d11 debug layer: storage filter at start ({queue->lpVtbl->GetStorageFilterStackSize(queue)} on the stack): {DescribeFilter(queue, storage: true)}");
            EngineLog.Engine($"d3d11 debug layer: retrieval filter at start ({queue->lpVtbl->GetRetrievalFilterStackSize(queue)} on the stack): {DescribeFilter(queue, storage: false)}");
            Check(queue->lpVtbl->PushEmptyStorageFilter(queue), "ID3D11InfoQueue::PushEmptyStorageFilter");
            Check(queue->lpVtbl->PushEmptyRetrievalFilter(queue), "ID3D11InfoQueue::PushEmptyRetrievalFilter");
            ulong limit = queue->lpVtbl->GetMessageCountLimit(queue);
            Check(queue->lpVtbl->SetMessageCountLimit(queue, ulong.MaxValue), "ID3D11InfoQueue::SetMessageCountLimit");
            EngineLog.Engine($"d3d11 debug layer: message count limit {limit} at start, now none");
            int id = D3D11SdkLayers.D3D11_MESSAGE_ID_DEVICE_DRAW_RENDERTARGETVIEW_NOT_SET;
            var filter = new D3D11_INFO_QUEUE_FILTER { DenyList = new D3D11_INFO_QUEUE_FILTER_DESC { NumIDs = 1, pIDList = &id } };
            Check(queue->lpVtbl->AddStorageFilterEntries(queue, &filter), "ID3D11InfoQueue::AddStorageFilterEntries");
            EngineLog.Engine($"d3d11 debug layer: storage filter now: {DescribeFilter(queue, storage: true)}");
        }

        /// The filter at the top of a stack: its allow and deny lists.
        static string DescribeFilter(ID3D11InfoQueue* queue, bool storage)
        {
            nuint length = 0;
            int hr = storage ? queue->lpVtbl->GetStorageFilter(queue, null, &length) : queue->lpVtbl->GetRetrievalFilter(queue, null, &length);
            if (hr < 0 || length < (nuint)sizeof(D3D11_INFO_QUEUE_FILTER)) return $"not read (0x{hr:X8}, {length} bytes)";
            var bytes = new byte[(int)length];
            fixed (byte* b = bytes)
            {
                var f = (D3D11_INFO_QUEUE_FILTER*)b;
                hr = storage ? queue->lpVtbl->GetStorageFilter(queue, f, &length) : queue->lpVtbl->GetRetrievalFilter(queue, f, &length);
                if (hr < 0) return $"not read (0x{hr:X8})";
                return $"allow {{{List(f->AllowList)}}}, deny {{{List(f->DenyList)}}}";
            }

            static string List(in D3D11_INFO_QUEUE_FILTER_DESC d)
            {
                var parts = new System.Collections.Generic.List<string>();
                for (uint i = 0; i < d.NumCategories; i++) parts.Add($"category {d.pCategoryList[i]}");
                for (uint i = 0; i < d.NumSeverities; i++) parts.Add($"severity {d.pSeverityList[i]}");
                for (uint i = 0; i < d.NumIDs; i++) parts.Add($"ID {d.pIDList[i]}");
                return parts.Count == 0 ? "none" : string.Join(", ", parts);
            }
        }

        /// The user-mode driver's version: IDXGIAdapter::CheckInterfaceSupport with IDXGIDevice's
        /// IID, "which returns the version number of the Direct3D 9 UMD (user mode driver)
        /// binary"; "S_OK indicates that the interface is supported, otherwise
        /// DXGI_ERROR_UNSUPPORTED is returned" (CheckInterfaceSupport). Its four 16-bit fields,
        /// high first, are the version Windows shows for the driver (observed: 32.0.16.1088 on
        /// the RTX 3090, driver 610.88, and 32.0.21042.62 on the AMD iGPU, d3d11-captures.md §4).
        static string DriverVersion(IDXGIAdapter1* adapter)
        {
            var iid = Dxgi.IIDDevice;
            Dxgi.LargeInteger version;
            int hr = ((IDXGIAdapter*)adapter)->lpVtbl->CheckInterfaceSupport((IDXGIAdapter*)adapter, &iid, &version);
            if (hr < 0) return $"unknown ({Dxgi.Hr(hr)})";
            long v = version.QuadPart;
            return $"{(v >> 48) & 0xFFFF}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
        }

        /// Every adapter in EnumAdapters1's order ("first ... the adapter with the output on which
        /// the desktop primary is displayed", then "other adapters with outputs", then "adapters
        /// without outputs"), with its outputs, and the first of each GPU preference, for the log.
        void LogAdapters()
        {
            var lines = new System.Collections.Generic.List<string>();
            var f1 = (IDXGIFactory1*)Factory;
            for (uint i = 0; i < 16; i++)
            {
                IDXGIAdapter1* a = null;
                if (f1->lpVtbl->EnumAdapters1(f1, i, &a) < 0 || a == null) break;
                Dxgi.AdapterDesc1 desc;
                int outputs = 0;
                if (a->lpVtbl->GetDesc1(a, &desc) >= 0)
                {
                    for (uint o = 0; o < 16; o++)
                    {
                        IDXGIOutput* output = null;
                        if (((IDXGIAdapter*)a)->lpVtbl->EnumOutputs((IDXGIAdapter*)a, o, &output) < 0 || output == null) break;
                        outputs++;
                        Com.Release(ref output);
                    }
                    lines.Add($"{i} {desc.Name} (luid {desc.Luid:x}, {outputs} outputs{((desc.Flags & Dxgi.AdapterFlagSoftware) != 0 ? ", software" : "")})");
                }
                Com.Release(ref a);
            }
            IDXGIFactory6* f6;
            if (Com.QueryInterface(Factory, IDXGIFactory6.IID, (void**)&f6) >= 0)
            {
                foreach (var p in new[] { DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_MINIMUM_POWER })
                {
                    IDXGIAdapter1* a = null;
                    var iid = IDXGIAdapter1.IID;
                    Dxgi.AdapterDesc1 desc;
                    if (f6->lpVtbl->EnumAdapterByGpuPreference(f6, 0, p, &iid, (void**)&a) >= 0 && a != null && a->lpVtbl->GetDesc1(a, &desc) >= 0)
                        lines.Add($"{(p == DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE ? "high performance" : "minimum power")} first: {desc.Name} (luid {desc.Luid:x})");
                    Com.Release(ref a);
                }
                Com.Release(ref f6);
            }
            EngineLog.Engine($"d3d11: adapters: {string.Join("; ", lines)}");
        }

        IDXGIAdapter1* ChooseAdapter(string gpu)
        {
            IDXGIAdapter1* adapter = null;
            if (gpu is "discrete" or "integrated")
            {
                IDXGIFactory6* f6;
                Check(Com.QueryInterface(Factory, IDXGIFactory6.IID, (void**)&f6), "QueryInterface IDXGIFactory6 (for --gpu)");
                var preference = gpu == "discrete" ? DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE : DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_MINIMUM_POWER;
                var iid = IDXGIAdapter1.IID;
                int hr = f6->lpVtbl->EnumAdapterByGpuPreference(f6, 0, preference, &iid, (void**)&adapter);
                Com.Release(ref f6);
                Check(hr, $"EnumAdapterByGpuPreference({preference})");
                return adapter;
            }
            Check(((IDXGIFactory1*)Factory)->lpVtbl->EnumAdapters1((IDXGIFactory1*)Factory, 0, &adapter), "EnumAdapters1(0)");
            return adapter;
        }

        /// SystemInfo.graphicsDeviceVersion: the original's "Direct3D 11.0 [level 11.1]" on a
        /// level 11_1 device (measured, d3d11-unity-probes.md §1); the 11_0 form is UNVERIFIED.
        public string VersionString => $"Direct3D 11.0 [level {((int)FeatureLevel >> 12) & 0xF}.{((int)FeatureLevel >> 8) & 0xF}]";

        public void Dispose()
        {
            var q = InfoQueue;
            Com.Release(ref q);
            InfoQueue = null;
            var c = Context;
            Com.Release(ref c);
            Context = null;
            var d = Device;
            Com.Release(ref d);
            Device = null;
            var f = Factory;
            Com.Release(ref f);
            Factory = null;
        }
    }
}
