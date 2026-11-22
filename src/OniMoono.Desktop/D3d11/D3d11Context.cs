using System;
using OniMoono.Desktop.Gl;

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
            }
            IDXGIFactory5* f5;
            if (Com.QueryInterface(Factory, IDXGIFactory5.IID, (void**)&f5) >= 0)
            {
                int allow = 0;
                Tearing = f5->lpVtbl->CheckFeatureSupport(f5, Dxgi.FeaturePresentAllowTearing, &allow, sizeof(int)) >= 0 && allow != 0;
                Com.Release(ref f5);
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
