using System.Runtime.CompilerServices;

// The host creates the platform and device.
[assembly: InternalsVisibleTo("OniMoono")]
// Unit tests of the device's main-thread recording (no GL context needed).
[assembly: InternalsVisibleTo("OniMoono.UnityEngine.Tests")]
// The Direct3D 11 declarations' smoke test on Windows (tools/D3d11Smoke).
[assembly: InternalsVisibleTo("D3d11Smoke")]
