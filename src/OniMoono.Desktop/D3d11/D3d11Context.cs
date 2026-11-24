using System;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;

namespace OniMoono.Desktop.D3d11
{
    /// Why the Direct3D 11 renderer cannot start here (the reason goes to the log).
    internal sealed class D3d11UnavailableException(string message) : Exception(message);

    /// The DXGI factory, the chosen adapter and the Direct3D 11 device with its immediate context.
    /// - The adapter: `--gpu default` takes the factory's first (EnumAdapters1 index 0, the adapter
    ///   of the desktop's primary output); `discrete` and `integrated` ask DXGI for the high
    ///   performance or minimum power GPU (IDXGIFactory6::EnumAdapterByGpuPreference). Software
    ///   adapters are refused. The plan's GPU choice rule (done criterion 5) may change this.
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

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new D3d11UnavailableException($"{what} failed ({Dxgi.Hr(hr)})");
        }

        public D3d11Context(string gpu)
        {
            try
            {
                Open(gpu);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        void Open(string gpu)
        {
            if (!OperatingSystem.IsWindows()) throw new D3d11UnavailableException("Direct3D 11 is Windows only");
            IDXGIFactory2* factory;
            var iid = IDXGIFactory2.IID;
            Check(DXGI.CreateDXGIFactory2(0, &iid, (void**)&factory), "CreateDXGIFactory2");
            Factory = factory;
            IDXGIAdapter1* adapter = ChooseAdapter(gpu);
            try
            {
                Dxgi.AdapterDesc1 desc;
                Check(adapter->lpVtbl->GetDesc1(adapter, &desc), "IDXGIAdapter1::GetDesc1");
                Adapter = desc;
                if ((desc.Flags & Dxgi.AdapterFlagSoftware) != 0) throw new D3d11UnavailableException($"{desc.Name} is a software adapter");
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
        /// DrainDebugMessages). Before this the layer's own default denied about 2,600 messages
        /// in a run that draws nothing (j0008854), unread (d3d11-plan.md, M3 review fixes,
        /// finding 2).
        static void SetFilters(ID3D11InfoQueue* queue)
        {
            EngineLog.Engine($"d3d11 debug layer: storage filter at start ({queue->lpVtbl->GetStorageFilterStackSize(queue)} on the stack): {DescribeFilter(queue, storage: true)}");
            EngineLog.Engine($"d3d11 debug layer: retrieval filter at start ({queue->lpVtbl->GetRetrievalFilterStackSize(queue)} on the stack): {DescribeFilter(queue, storage: false)}");
            Check(queue->lpVtbl->PushEmptyStorageFilter(queue), "ID3D11InfoQueue::PushEmptyStorageFilter");
            Check(queue->lpVtbl->PushEmptyRetrievalFilter(queue), "ID3D11InfoQueue::PushEmptyRetrievalFilter");
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
