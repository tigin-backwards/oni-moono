# BRUTAL framework API reference (for the ONI Unity-layer reimplementation)

Researched 2026-10-15 against:
- the BRUTAL source clone at `cache/BRUTAL` (commit `3c3391d`, "Update CONTRIBUTING.md");
- the published NuGet packages (2026.10.x), downloaded and inspected in `/tmp/claude-1000/brutal-nupkgs`;
- smoke tests run on WSL2 with WSLg, Mesa lavapipe and the .NET 10 SDK (10.0.402).

The source repo has **no .csproj, .sln or build scripts** ("Project files ... are coming"). Only the NuGet packages are consumable. Code under `*/Generated/*.gen.cs` is generator output; the generators themselves are not in the repo.

Legend: **(verified)** = compiled or run here; **(unverified)** = read from source only and not exercised. Everything else was read directly from source.

## Top gotchas (read first)
1. **Package ids are cased `Ahwoo.BRUTAL.*`**, all net10.0-only. Natives ship only for linux-x64, win-x64 and osx-arm64.
2. **Linux: Brutal.Vulkan cannot find `libvulkan.so.1` by itself.** It loads `"vulkan"`, which only matches `libvulkan.so` (a dev symlink). Add an `AssemblyLoadContext.Default.ResolvingUnmanagedDll` hook (section 9) **(verified)**.
3. **Each BRUTAL assembly installs its own `DllImportResolver`** in a static ctor. Do not call `NativeLibrary.SetDllImportResolver` on BRUTAL assemblies.
4. **Slang: `ISessionExtensions.CreateCompositeComponentType(ReadOnlySpan<IComponentType>, ...)` segfaults.** Use the raw-pointer workaround (section 3.2) **(verified)**. You must also set `StructureSize` on `SessionDesc` and `TargetDesc` yourself.
5. **GLFW `OnChar` truncates codepoints to UTF-16 code units** (`(char)point`) (section 1.3).
6. **Analyzers from Brutal.Core/Strings/Numerics apply to consumer code even transitively.** BRUTALCORE0009 and 0010 are build errors (section 10).
7. **`float4x4` is row-major with row vectors (System.Numerics semantics).** The projection is [0,1] depth with no Vulkan Y-flip (section 8).
8. **There is no CPU BC1/BC3/BC7 decoder.** BC6H/BC7 DDS fails through `TextureLoader`; upload BC payloads as `VK_FORMAT_BC*` (section 4).
9. **Brutal.FreeType has no C# API.** It is natives only. Fonts go through ImGui.FreeType or Sdl.Ttf (SDF supported), or through your own P/Invokes (section 5).
10. **Brutal.Fmod binds FMOD 2.03.08 and ships no natives.** It is in a different namespace from the game's `FMOD` wrapper, so there is no conflict; simply don't reference it (section 7).

## Contents
1. Windowing & input: Glfw (+ Sdl)
2. Vulkan, Vulkan.Abstractions, Vulkan.Vma
3. Shader compilation: ShaderC, Slang
4. Textures: Texture, Texture.Abstractions, Stb, Ktx, Gli
5. Fonts: FreeType, Sdl.Ttf
6. ImGui and backends
7. FMOD
8. Numerics, Core & utilities (Interop, Memory, Collections, Strings, Concurrency, Logging)
9. Native binary distribution
10. Project settings, analyzers, architecture guidance
11. Smoke tests
## 1. Windowing & input: Brutal.Glfw (primary), Brutal.Sdl (alternative)

### 1.1 Package / assembly facts
- NuGet id `Ahwoo.BRUTAL.Glfw` 2026.10.4, assembly `Brutal.Glfw.dll`, namespace **`Brutal.GlfwApi`** (platform-native helpers in `Brutal.GlfwApi.X11`, `.Win32`, `.Wayland`, `.Cocoa`, `.Egl`, `.OSMesa`).
- Binds **GLFW 3.5.1** (verified at runtime: `Glfw.VersionString` = `"3.5.1 Wayland X11 GLX Null EGL OSMesa monotonic shared"`). This is a GLFW 3.5 build with extras not in upstream 3.4: `glfwSetScrollDetailCallback` (`GlfwScrollFlags.Precise|Begin|End|Momentum`), trackpad zoom/rotate callbacks, `GlfwInputMode.UnlimitedMouseButtons`, `GlfwCursorMode.Captured`, `GlfwInitHint.WaylandLibdecor`.
- Native lib is loaded in the `Glfw` static ctor: `NativeLibrary.Load(OperatingSystem.IsWindows() ? "glfw3" : "glfw", typeof(Glfw).Assembly, DllImportSearchPath.AssemblyDirectory)` and installed as the assembly's `DllImportResolver`. Shipped: `runtimes/linux-x64/native/libglfw.so{,.3,.3.5}`, `runtimes/win-x64/native/glfw3.dll`, `osx-arm64`. (So the app output dir must contain the native - the standard NuGet runtimes/ copy handles that for RID-specific or portable builds.)
- Every wrapper method calls `Log.Trace()` (category `Glfw.Log = LogCategory.Make("Glfw")`) before the P/Invoke; it was silent at default log level in the smoke test.
- Two API layers: the idiomatic managed layer (`Glfw.*` statics + `GlfwWindow`/`GlfwMonitor`/`GlfwCursor`) and the raw layer `Glfw.PInvoke.*` (public, 1:1 with `glfw*` C functions, e.g. `Glfw.PInvoke.GetCursorPos(window, out double x, out double y)`, `Glfw.PInvoke.SetWindowShouldClose(...)`, `Glfw.PInvoke.GetFramebufferSize(...)`). Use the raw layer for anything the managed layer caches or omits.
- Uses C# 14 `extension(GlfwWindow window) { ... }` blocks for native-handle accessors -> consumer needs C# 14 (default for net10.0).

### 1.2 Init / lifetime / errors
```csharp
public static event GlfwErrorCallback? ErrorCallback;           // delegate void GlfwErrorCallback(GlfwErrorCode inError, string inDescription)
public static bool Init();                                        // idempotent; installs monitor+joystick callbacks
public static void Terminate();
public static void SetInitHint(GlfwInitHint hint, int value);     // Platform, JoystickHatButtons, CocoaChdirResources, CocoaMenubar, WaylandLibdecor
public static GlfwErrorCode GetError(out string? description);
public static void InitAllocator(GlfwAllocateUnmanagedCallback allocate, GlfwReallocateUnmanagedCallback reallocate, GlfwDeallocateUnmanagedCallback deallocate, void* user = null);
public static VersionInfo Version { get; }                        // struct { int Major, Minor, Revision }
public static string VersionString { get; }
public static GlfwPlatform GetPlatform();  public static bool IsPlatformSupported(GlfwPlatform platform);
public static double Time { get; set; }   public static ulong GetTimerValue();  public static ulong GetTimerFrequency();
```
- Constants are in `Glfw.Constants` (imported via `using static Brutal.GlfwApi.Glfw.Constants;`): `GLFW_TRUE/FALSE`, `GLFW_NO_API = 0`, `GLFW_DONT_CARE = -1`, `GLFW_PLATFORM_X11 = 0x00060004`, `GLFW_PLATFORM_WAYLAND = 0x00060003`, `GLFW_PLATFORM_WIN32 = 0x00060001`, `GLFW_PLATFORM_NULL = 0x00060005`, etc.
- **Exceptions thrown inside any GLFW callback are caught and aggregated**; they are rethrown (single `Exception` or `AggregateException`) from `Glfw.PollEvents()` only. `WaitEvents()/WaitEventsTimeout()` do NOT call `CheckExceptions()` - exceptions raised during those surface at the next `PollEvents()`.
- Platform choice: on WSLg GLFW picks **Wayland** by default (required instance exts `VK_KHR_surface, VK_KHR_wayland_surface`). Under Wayland, `GlfwWindow` construction triggers a `FeatureUnavailable: Wayland: The platform does not provide the window position` error-callback (harmless; window `Refresh()` queries position). Force X11 with `Glfw.SetInitHint(GlfwInitHint.Platform, GLFW_PLATFORM_X11)` before `Init()` (verified: exts become `VK_KHR_surface, VK_KHR_xcb_surface`). With no DISPLAY/WAYLAND_DISPLAY, `Init()` returns false with `PlatformUnavailable`.

### 1.3 Window creation and state
```csharp
public static void DefaultWindowHints();
public static void WindowHint(GlfwWindowHint hint, int value);
public static void WindowHint(GlfwWindowHint hint, string value);   // X11ClassName etc.
public static GlfwWindow CreateWindow(in GlfwWindow.CreateInfo info); // throws InvalidOperationException("Failed to create window: {code} ({desc})")

public sealed unsafe partial class GlfwWindow : IDisposable, INullable {
  public struct CreateInfo { public required string Title; public required int2 Size; public GlfwMonitor Monitor; /* non-null => fullscreen */ public GlfwWindow? Share; }
  public nint NativeHandle { get; }                    // GLFWwindow*
  public string Title { get; set; }
  public int2 Position { get; set; }  public int X, Y { get; set; }
  public int2 Size { get; set; }      public int Width, Height { get; set; }   // screen coords
  public int2 FramebufferSize { get; }                 // pixels -> use for swapchain extent
  public float AspectRatio { get; set; }
  public float2 ContentScale { get; }                  // DPI scale
  public double2 CursorPos { get; set; } public double CursorX, CursorY { get; set; }
  public float Opacity { get; set; }
  public GlfwMonitor Monitor { get; set; }             // set null monitor => windowed, non-null => fullscreen (GLFW_DONT_CARE refresh)
  public bool IsMaximised, IsIconified, IsFocused, IsFullscreen, IsWindowed { get; }
  public bool ShouldClose { get; set; }
  public void* UserPointer { get; set; }
  public bool Visible { get; set; }
  public void SetFullscreen(GlfwMonitor monitor, int2 size, int refreshRate);
  public void SetWindowed(int2 pos, int2 size);
  public void SetIcon(int count, GlfwImage image);
  public void SetSizeLimits(int2 minSize, int2 maxSize);
  public int  GetAttribute(GlfwWindowAttribute attribute);  public int GetAttribute(GlfwContextAttribute attribute);
  public void SetAttribute(GlfwWindowAttribute attrib, int value);
  public void GetWindowFrameSize(out int left, out int top, out int right, out int bottom);
  public void Iconify(); Restore(); Maximize(); Focus(); RequestAttention();
  public int  GetInputMode(GlfwInputMode mode);  public void SetInputMode(GlfwInputMode mode, int value);
  public GlfwKeyAction    GetKey(GlfwKey key);                 // polling
  public GlfwButtonAction GetMouseButton(GlfwMouseButton button);
  public void SetCursor(GlfwCursor cursor);
  public void MakeContextCurrent(); public void SwapBuffers();  // GL only
  public void WaitForWindow();   // loops WaitEvents() until FramebufferSize > 0 (minimised-window handling)
  public void CenterToMonitor(GlfwMonitor monitor);
  public void Refresh();         // re-reads all cached state from GLFW
  public void Dispose();         // glfwDestroyWindow + frees pinned callback GCHandles
}
```
- **State is cached**: properties return fields updated by the window's own GLFW callbacks (size, pos, fb size, content scale, cursor pos, focus, iconify, maximise, close) or by `Refresh()`. Setters call GLFW, and getter values update when the corresponding callback fires during `PollEvents()`.
- `GlfwWindowHint` members: `Focused, Resizable, Visible, Decorated, AutoIconify, Floating, Maximized, CenterCursor, TransparentFramebuffer, FocusOnShow, MousePassthrough, PositionX, PositionY, ScaleFramebuffer, RedBits..AlphaBits, DepthBits, StencilBits, Samples, SrgbCapable, RefreshRate, Doublebuffer, ClientApi, ContextVersionMajor/Minor, ..., ScaleToMonitor, CocoaRetinaFramebuffer, X11ClassName, X11InstanceName, OpenGlProfile ...`. For Vulkan: `Glfw.WindowHint(GlfwWindowHint.ClientApi, GLFW_NO_API /*0*/)`.
- All event hooks are C# events on `GlfwWindow` (the class always registers every native callback itself; you just subscribe):
```csharp
event GlfwKeyCallback OnKey;                 // (GlfwWindow w, GlfwKey key, int scancode, GlfwKeyAction action /*Release,Press,Repeat*/, GlfwModifier mods)
event GlfwCharCallback OnChar;               // (GlfwWindow w, uint codepoint)
event GlfwCharModsCallback OnCharMods;       // (GlfwWindow w, uint codepoint, GlfwModifier mods)
event GlfwMouseButtonCallback OnMouseButton; // (w, GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
event GlfwCursorPosCallback OnCursorPos;     // (w, double2 pos)
event GlfwCursorEnterCallback OnCursorEnter; // (w, bool entered)
event GlfwScrollCallback OnScroll;           // (w, double2 offset)
event GlfwScrollDetailCallback OnScrollDetail; // (w, double2 offset, GlfwScrollFlags flags)
event GlfwTrackpadZoomCallback OnTrackpadZoom; event GlfwTrackpadRotateCallback OnTrackpadRotate; // (w, double)
event GlfwDropCallback OnDrop;               // (w, int count, string[] paths)
event GlfwFramebufferSizeCallback OnFramebufferSize; // (w, int2 size)
event GlfwWindowSizeCallback OnSize; GlfwWindowPosCallback OnPosition; // (w, int2)
event GlfwWindowContentScaleCallback OnContentScale; // (w, float2 scale)
event GlfwWindowFocusCallback OnFocus;       // (w, GlfwFocusEvent)
event GlfwWindowIconifyCallback OnIconify; GlfwWindowMaximizeCallback OnMaximize;
event GlfwWindowCloseCallback OnClose; GlfwWindowRefreshCallback OnRefresh;
```
- **Gotcha (OnChar/OnCharMods)**: the internal handler does `OnChar?.Invoke(this, (char)point)` - the codepoint is truncated to 16 bits before being widened back to `uint`. Non-BMP characters (emoji, some CJK ext.) arrive corrupted. Workaround: register your own callback via `Glfw.PInvoke.SetCharCallback(window, yourUnmanagedDelegate)` (replaces the wrapper's; keep the delegate rooted) - nothing else in the binding depends on the wrapper's handler, which only raises `OnChar`. Moono does this (`GlfwPlatform.OnCharNative`).
- `GlfwKey` names: `Unknown, Space, Apostrophe, Comma, Minus, Period, Slash, Number0..Number9, Semicolon, Equal, A..Z, LeftBracket, Backslash, RightBracket, GraveAccent, World1, World2, Escape, Enter, Tab, Backspace, Insert, Delete, Right, Left, Down, Up, PageUp, PageDown, Home, End, CapsLock, ScrollLock, NumLock, PrintScreen, Pause, F1..F25, Kp0..Kp9, KpDecimal, KpDivide, KpMultiply, KpSubtract, KpAdd, KpEnter, KpEqual, LeftShift, LeftControl, LeftAlt, LeftSuper, RightShift, RightControl, RightAlt, RightSuper, Menu, Last`. `GlfwModifier`: `Shift, Control, Alt, Super, CapsLock, NumLock` (flags).
- Key names / layouts: `Glfw.GetKeyName(GlfwKey key, int scanCode)`, `Glfw.GetKeyScanCode(int key)`.

### 1.4 Cursor & input modes
```csharp
public static GlfwCursor CreateCursor(GlfwStandardCursor shape); // Arrow, IBeam, Crosshair, PointingHand, ResizeEw, ResizeNs, ResizeNwse, ResizeNesw, ResizeAll, NotAllowed, HResize, VResize, Hand
public static GlfwCursor CreateCursor(in GlfwCursor.CreateInfo info); // CreateInfo { GlfwImage Image; int2 Hot; }; GlfwImage { int Width, Height; byte* Pixels /*RGBA8*/ }
public unsafe struct GlfwCursor : IDisposable, INullable { nint NativeHandle; void Dispose(); bool IsNull(); }
window.SetCursor(cursor);
window.SetInputMode(GlfwInputMode.Cursor, (int)GlfwCursorMode.Disabled); // Normal, Hidden, Disabled, Captured
window.SetInputMode(GlfwInputMode.RawMouseMotion, GLFW_TRUE); // check Glfw.RawMotionSupported()
// GlfwInputMode: Cursor, StickyKeys, StickyMouseButtons, LockKeyMods, RawMouseMotion, UnlimitedMouseButtons
```

### 1.5 Monitors, video modes, DPI, fullscreen
```csharp
public static GlfwMonitor PrimaryMonitor { get; }
public static ReadOnlySpan<GlfwMonitor> GetMonitors();          // span over GLFW-owned array; valid until monitor config changes
public static event GlfwMonitorCallback? MonitorCallback;       // (GlfwMonitor monitor, GlfwMonitorEvent eventType)
public readonly unsafe partial struct GlfwMonitor : INullable {
  public static readonly GlfwMonitor Null;
  nint NativeHandle; bool IsNull();
  string? Name; void* UserPointer;
  int2 Position; int2 PhysicalSize /*mm*/; float2 ContentScale;
  ReadOnlySpan<GlfwVidMode> VideoModes; GlfwVidMode VideoMode /*current*/; int2 Size /*current mode WxH*/;
  void GetMonitorWorkArea(out int2 pos, out int2 size);
  void SetGamma(float gamma); GlfwGammaRampView GammaRamp { get; set; }
}
public struct GlfwVidMode { int Width, Height, RedBits, GreenBits, BlueBits, RefreshRate; }
```
Fullscreen: `window.SetFullscreen(monitor, monitor.Size, monitor.VideoMode.RefreshRate)`; back: `window.SetWindowed(pos, size)`. Borderless-windowed: `Glfw.WindowHint(GlfwWindowHint.Decorated, 0)` + size = monitor work area. DPI: `window.ContentScale` + `OnContentScale`; `GlfwWindowHint.ScaleToMonitor` / `ScaleFramebuffer` control auto-scaling. Always size the swapchain from `window.FramebufferSize` (pixels), not `Size` (screen coords).

### 1.6 Vulkan integration
```csharp
public static bool  VulkanSupported();
public static byte** GetRequiredInstanceExtensions(out uint count);       // raw UTF-8 strings, GLFW-owned
public static nint  GetInstanceProcAddress(nint instance, string procName);
public static bool  GetPhysicalDevicePresentationSupport(nint instance, nint physicalDevice, int queueFamilyIndex);
public static int   CreateWindowSurface(nint instance, GlfwWindow window, out ulong surface); // returns VkResult as int (0 = VK_SUCCESS); null allocator
Glfw.PInvoke.InitVulkanLoader(nint vkGetInstanceProcAddr);                 // optional, before Init()
```
Handles are untyped (`nint` instance/physical device, `ulong` surface) - Brutal.Glfw does not reference Brutal.Vulkan. Bridge with `instance.VkHandle` (`IntPtr`) and wrap the `ulong` into Brutal.Vulkan's `VkSurfaceKHR` (see section 2 for the exact conversion). Verified on lavapipe: `CreateWindowSurface` -> 0 and presentation support true on queue family 0, both Wayland and X11.

### 1.7 Polling, clipboard, joystick, misc
```csharp
public static void PollEvents();            // + rethrows aggregated callback exceptions
public static void WaitEvents(); public static void WaitEventsTimeout(double timeout); public static void PostEmptyEvent();
public static string? Clipboard { get; set; }    public static void SetClipboard(string @string);   // null on FormatUnavailable
public static bool JoystickPresent(int id); ReadOnlySpan<float> GetJoystickAxes(int id); ReadOnlySpan<byte> GetJoystickButtons(int id);
public static ReadOnlySpan<GlfwJoystickHat> GetJoystickHats(int joystickId);
public static bool JoystickIsGamepad(int id); bool GetGamepadState(int id, out GlfwGamepadState state) /* fixed byte Buttons[15]; fixed float Axes[6] */;
public static bool UpdateGamepadMappings(string mappings); string? GetGamepadName(int id); event GlfwJoystickCallback? JoystickCallback;
// native handles: using Brutal.GlfwApi.X11/Win32/Wayland;  window.GetX11Window() (ulong), window.GetWin32Window() (nint HWND), window.GetWaylandWindow(); X11Extensions.GetX11Display()
```
Threading: GLFW main-thread rules apply unchanged (window creation, PollEvents, most queries on the main thread).

### 1.8 Idiomatic main loop (verified building blocks)
```csharp
using Brutal.GlfwApi; using Brutal.Numerics; using static Brutal.GlfwApi.Glfw.Constants;
Glfw.ErrorCallback += (c, d) => Console.Error.WriteLine($"GLFW {c}: {d}");
if (!Glfw.Init()) throw new Exception("GLFW init failed");
Glfw.WindowHint(GlfwWindowHint.ClientApi, GLFW_NO_API);
using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "ONI", Size = new int2(1600, 900) });
bool resized = false;
window.OnFramebufferSize += (_, s) => resized = true;
window.OnKey += (_, key, sc, action, mods) => { if (key == GlfwKey.Escape && action == GlfwKeyAction.Press) window.ShouldClose = true; };
window.OnChar += (_, cp) => textInput.Append(char.ConvertFromUtf32((int)cp)); // see BMP truncation gotcha
window.OnScroll += (_, off) => scrollDelta += off.Y;
while (!window.ShouldClose) {
    Glfw.PollEvents();
    if (window.FramebufferSize.X == 0 || window.FramebufferSize.Y == 0) { window.WaitForWindow(); continue; } // minimised
    if (resized) { /* recreate swapchain with window.FramebufferSize */ resized = false; }
    // render
}
Glfw.Terminate();
```

### 1.9 Brutal.Sdl (alternative) - assessment
- `Ahwoo.BRUTAL.Sdl` 2026.10.2, namespace `Brutal.SdlApi`, binds **SDL3** (native `libSDL3.so.0.4.12` => SDL 3.4.x; `SDL3.dll`; linux-x64/win-x64/osx-arm64). ~1190 raw `[LibraryImport]` P/Invokes in `Sdl.PInvoke` (much broader surface than GLFW: text input/IME (`StartTextInput`, `SetTextInputArea`), clipboard, gamepads with full mapping DB, audio, SDL_GPU enums, renderer, dialogs, etc.).
- Managed layer is thinner and differently shaped: nested static classes per subsystem, e.g. `Sdl.Init.Initialize(SdlInitFlags flags)` / `Sdl.Init.Quit()`, `Sdl.Video.CreateWindow(string title, int w, int h, SdlWindowFlags flags)` (throws `SdlException.FromError(...)` on failure), `Sdl.Events.PollEvent(out SdlEvent ev)`, `Sdl.Events.AddEventWatch(SdlEventWatch cb)`, `Sdl.Vulkan.GetInstanceExtensions() -> string[]`, `Sdl.Vulkan.CreateSurface(SdlWindow window, nint instance) -> nint`, `Sdl.Vulkan.DestroySurface(nint instance, nint surface)`, `Sdl.Video.GetWindowDisplayScale(...)`, `GetDisplayContentScale(...)`. Errors -> `SdlException`; callback exceptions -> `SdlCallbackException` via an internal aggregator.
- Verdict: **Brutal.Glfw has the more complete idiomatic wrapper** (object model with cached state + events, documented, used by `ImGui.Backends`); **Brutal.Sdl has the more complete raw API coverage** (IME/text-input rectangles, richer gamepad, audio). For ONI (keyboard/mouse game, Vulkan) GLFW is the path of least resistance and is what ImGui.Backends supports. SDL was not smoke-tested.
## 2. Vulkan: Brutal.Vulkan, Brutal.Vulkan.Abstractions, Brutal.Vulkan.Vma

Packages: `Ahwoo.BRUTAL.Vulkan` 2026.10.2, `Ahwoo.BRUTAL.Vulkan.Abstractions` 2026.10.2, `Ahwoo.BRUTAL.Vulkan.Vma` 2026.10.2. All target `net10.0`.

| Assembly | Namespace | Contents |
|---|---|---|
| Brutal.Vulkan | `Brutal.VulkanApi` | Generated bindings for the full registry (about 1438 structs, 56 handles, enums, `VK` constants). Hand-written `Vulkan`, `Instance`, `PhysicalDevice`, `Device`, `Queue`, `CommandBuffer` and `VkResultError` |
| Brutal.Vulkan.Abstractions | `Brutal.VulkanApi.Abstractions` | `DeviceEx` (a device that is also a simple allocator), `BufferEx`/`ImageEx`/`ImageViewEx`, mapping, `StagingPool`, `DescriptorPoolEx`/`DescriptorSetLayoutEx`, `Presets`, `VertexInput` reflection builder, partitioners (atlas/suballocators), `VulkanHelpers`, debug names, misc extensions |
| Brutal.Vulkan.Vma | `Brutal.VulkanApi.Vma` | VMA bindings (VMA "3.4.0-development", built from upstream v3.3.0-39-ge722e57 per LICENSES). `VmaAllocator` implements the Abstractions `IBufferAllocator`/`IImageAllocator` |

There is **no swapchain, surface, frame-loop or render-graph helper** anywhere. You write swapchain and present code against the raw bindings. `FrameResources`/`SyncObjects` are only plain bags of handles. The ImGui Vulkan backend (`Brutal.ImGui.Backends/RendererBackends/VulkanBackend*.cs`) is the only in-tree example of swapchain and pipeline code, and it is worth reading as a reference.

### 2.1 Native loading (Brutal.Vulkan/ImportResolver.cs)

The static ctor of `Brutal.VulkanApi.Vulkan` runs on first touch of any `Vulkan.*` member:
- Windows: `NativeLibrary.Load("vulkan-1", asm, DllImportSearchPath.System32)`
- Linux: `NativeLibrary.Load("vulkan", asm, DllImportSearchPath.LegacyBehavior)`
- macOS: loads the package's `VulkanEx` (marked as a hack in the source; it needs the SDK)
- It then calls `NativeLibrary.SetDllImportResolver(typeof(Vulkan).Assembly, ...)`, mapping `"vulkan"` to that handle. It reads `vkGetInstanceProcAddr` via `GetExport` and exposes it as `public static VkGetInstanceProcAddr Vulkan.GetInstanceProcAddr`. If loading fails it throws `NullReferenceException("Failed to load Vulkan library")`, wrapped in a `TypeInitializationException`.
- **The package does not ship the Vulkan loader.** `runtimes/*/native/(lib)VulkanEx` only exports `vxDestroyImages/ImageViews/Buffers/BufferViews` batch helpers.
- **Linux gotcha:** .NET probing for `"vulkan"` tries `libvulkan.so`/`vulkan.so`, **not** `libvulkan.so.1`. Distros ship the unversioned `libvulkan.so` only in the `-dev` package. On this machine only `/usr/lib/x86_64-linux-gnu/libvulkan.so.1` exists, so the load fails with `TypeInitializationException -> DllNotFoundException` (VERIFIED by the smoke test). Possible fixes:
  - (a) install `libvulkan-dev`;
  - (b) ship or symlink `libvulkan.so -> libvulkan.so.1` in the app base directory (LegacyBehavior probes the app directory);
  - (c) before touching `Vulkan`, register `AssemblyLoadContext.Default.ResolvingUnmanagedDll += (asm, name) => name == "vulkan" ? NativeLibrary.Load("libvulkan.so.1") : 0;`. VERIFIED working in the smoke test (section 11); recommended fix.
  - Do **not** call `SetDllImportResolver` on the Brutal.Vulkan assembly yourself, because BRUTAL sets it and a second set throws `InvalidOperationException`.
- Function loading:
  - `VkInstance.FunctionTable(VkInstance, VkGetInstanceProcAddr)` resolves **every** instance-level function by name at instance creation.
  - `VkDevice.FunctionTable(VkDevice, VkGetDeviceProcAddr)` resolves every device-level function, all extensions included, at `Device` construction.
  - Missing functions are stored as null function pointers. Calling one for an extension you did not enable is a native null call (crash), not an exception (by inspection).
  - Each table is unmanaged memory (`Mem.AllocAndCopy`), exposed as `ref VkDevice.FunctionTable PInvoke` (fields are `vkXxx`, each a `readonly struct` with `delegate* unmanaged[Cdecl]<...> Invoke`).
  - Loader-level P/Invokes (`[LibraryImport("vulkan")]`) exist only for `vkCreateInstance`, `vkEnumerateInstanceExtensionProperties`, `vkEnumerateInstanceLayerProperties` and `vkEnumerateInstanceVersion` (`Vulkan.PInvoke.*`).

### 2.2 Conventions (handles, structs, arrays, strings, errors)

**Handles** are `public readonly unsafe struct VkXxx : IVulkanHandle` with `nint VkHandle`, `VkObjectType VkObjectType`, `bool IsNull()`, `static VkXxx NullHandle`, and ctor `VkXxx(nint handle)`. Non-dispatchable handles are `nint`-backed too.

**Wrapper classes** carry function tables and implement `VulkanHandleWrapper<THandle, TFunctionTable>` (interface: `ref TFunctionTable PInvoke`). Each has an implicit conversion to its raw handle.
- `Instance : VulkanHandleWrapper<VkInstance, VkInstance.FunctionTable>, IDisposable` is a class. Its finalizer warns and best-effort destroys.
- `PhysicalDevice : VulkanHandleWrapper<VkPhysicalDevice, VkInstance.FunctionTable>` is a class.
- `Device : VulkanHandleWrapper<VkDevice, VkDevice.FunctionTable>, IDisposable` is a class. Its finalizer only warns. `Dispose()` calls `vkDestroyDevice`.
- `Queue : VulkanHandleWrapper<VkQueue, VkDevice.FunctionTable>` is a class. **All submit, present, wait and label calls take an internal `Lock`**, so a queue is thread-safe for submission.
- `CommandBuffer : VulkanHandleWrapper<VkCommandBuffer, VkDevice.FunctionTable>` is a **struct**. Its ctor is `CommandBuffer(Device device, VkCommandBuffer handle)`.

**API surface = extension methods.** These live in `static partial class VkInstanceExtensions` and `VkDeviceExtensions` on `this VulkanHandleWrapper<...>`. Names drop the `vk` prefix. Command-buffer commands also drop `Cmd` and are generic over `T : VulkanHandleWrapper<VkCommandBuffer, VkDevice.FunctionTable>`, for example `cmd.BeginRendering(in info)`, `cmd.Draw(...)` and `cmd.PipelineBarrier2(in dep)`. KHR/EXT duplicates keep their suffix (`BeginRenderingKHR`).

**Parameter mapping:**
- `const T*` single struct becomes `in T`. You pass a temporary: `device.CreateFence(new VkFenceCreateInfo{...}, null)`.
- Array plus count becomes `scoped ReadOnlySpan<T>`, and the count is derived. Example: `device.UpdateDescriptorSets(ReadOnlySpan<VkWriteDescriptorSet>, ReadOnlySpan<VkCopyDescriptorSet>)`; collection expressions `[...]` work.
- Single output handle is returned (`VkFence CreateFence(...)`).
- Output scalar becomes `out` (`AcquireNextImageKHR(..., out int pImageIndex)`).
- `uint32_t` maps to `int` almost everywhere: counts, indices, versions, and `VkFlags`-backed enums are `: int`.
- `VkDeviceSize` maps to `ByteSize64`, `size_t` to `ByteSize`, `VkBool32` to `Brutal.Bool32` (implicit to and from `bool`).
- Timeouts (`uint64_t`) are `nint`. Pass `-1` for UINT64_MAX.
- `VkAllocationCallbacks*` becomes `VkAllocator?`. Pass `null`.
- Two-call enumerations are split into `X_Count(...)` and `X_Write(..., ReadOnlySpan<T> dst)`. The `_Write` destination is typed `ReadOnlySpan` but is written into, so a `Span`/array converts implicitly. Examples: `GetQueueFamilyProperties_Count/_Write`, `GetSurfaceFormatsKHR_Count(surface)/_Write(surface, span)`, `GetSwapchainImagesKHR_Count(sc)/_Write(sc, span)`, `Vulkan.EnumerateInstanceExtensionProperties_Count(ReadOnlySpan<byte> layer)` (pass `[]` for none), `Vulkan.EnumerateInstanceLayerProperties_Count()/_Write(span)`.
- Several query functions have two overloads: `T Get...(...)` and `void Get...(..., ref T)`. Examples: `GetProperties()`, `GetFeatures2()`, `GetMemoryProperties()`, `GetSurfaceCapabilitiesKHR(surface)`, `GetFormatProperties(fmt)`, `GetBufferMemoryRequirements(buf)`.

**Structs** are `[StructLayout(Explicit, Size=N)] public unsafe partial struct VkXxx()`. `sType` is a **private-ish `internal readonly` field initialised by the parameterless primary ctor**, and `pNext` is `public void* Next`.
- Always create with `new VkXxx { ... }`.
- **`default(VkXxx)`, `stackalloc VkXxx[n]`, `new VkXxx[n]` and `SpanOwner.Allocate` all leave `sType = 0`.** Write `arr[i] = new VkXxx{...}` for each element.
- Field names are PascalCase with the `p`/`pp` prefix stripped: `ApplicationName` (byte*), `QueueCreateInfos` (VkDeviceQueueCreateInfo*), `ppEnabledExtensionNames` (kept as-is).
- Strings in structs are `byte*`. Use u8 literals: `"main"u8.AsPointer()` (`Brutal.Pointers.Extensions.AsPointer(this ReadOnlySpan<T>)`, valid because u8 literals are null-terminated static data).
- Fixed char arrays are `fixed byte DeviceName[256]`. Read them with `VulkanDeviceInfo.GetDeviceName(in props)` or `Utf8.GetString(Utf8.GetNullTerminatedSpan(p))`.
- Fixed struct arrays are `[InlineArray]` types, for example `VkMemoryType_32`.
- `VkClearColorValue` is a union of `float4 Float32 / int4 Int32 / uint4 Uint32` (Brutal.Numerics). `VkClearValue` has implicit conversions from `VkClearColorValue` and `VkClearDepthStencilValue`.
- Helper ctors: `VkExtent2D(int w, int h)`, `VkRect2D(VkExtent2D)`, `VkRect2D(int w, int h)`, `VkOffset2D(x, y)`, plus `VkExtent3D` and `VkOffset3D`.

**pNext chains** are built manually from locals: `var f13 = new VkPhysicalDeviceVulkan13Features{...}; info.Next = &f13;`. No chain builder exists.

**Enums** are C#-ified: `VkFormat.B8G8R8A8SRGB`, `VkFormat.R8G8B8A8UNorm`, `VkFormat.Bc1RGBAUNormBlock`, `Bc3UNormBlock`, `Bc7UNormBlock`, `VkImageLayout.PresentSrcKHR`, `VkImageViewType._2D`, `VkSampleCountFlags._1Bit`, `VkIndexType.UInt16`, `VkQueueFlags.GraphicsBit`, `VkPipelineStageFlags2.ColorAttachmentOutputBit`, `VkAccessFlags2.ColorAttachmentWriteBit`, `VkPresentModeKHR.FifoKHR`, `VkColorSpaceKHR.SRGBNonlinearKHR`, `VkCompositeAlphaFlagsKHR.OpaqueBitKHR`.

**Constants:** `VK.KHR_SWAPCHAIN_EXTENSION_NAME` (string), `VK.EXT_DEBUG_UTILS_EXTENSION_NAME`, `VK.LAYER_KHRONOS_VALIDATION`, `VK.LAYER_KHRONOS_VALIDATION_UTF8`, `VK.REMAINING_MIP_LEVELS`, `VK.REMAINING_ARRAY_LAYERS`, `VK.WHOLE_SIZE` (a `ByteSize`). API versions come from `VulkanHelpers.Api.VERSION_1_3` (enum; cast with `(int)`).

**Errors:**
- Every `VkResult`-returning command switches on the result and **throws `VkResultError : Exception` (property `VkResult Result`)** for each documented error code.
- Success codes are returned to you. `WaitForFences` returns `Success` or `Timeout`. `AcquireNextImageKHR` and `Queue.PresentKHR` return `Success` or `SuboptimalKHR`.
- Commands with a single output handle return the handle and throw on error.
- **`VK_ERROR_OUT_OF_DATE_KHR` from `AcquireNextImageKHR` or `PresentKHR` arrives as a thrown `VkResultError`, not a return value.** BRUTAL's own ImGui backend compares the return value against `ErrorOutOfDateKHR`, which is dead code. Wrap both calls in `try { } catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR) { recreate; }`.

**Internal-only core functions:** `AllocateCommandBuffers`, `FreeCommandBuffers`, `MapMemory`, `FreeDescriptorSets`, `GetDeviceQueue`, `DestroyDevice`, `QueueSubmit` and `QueuePresent` are `internal` in Brutal.Vulkan. They are exposed through the `Device`, `Queue` and `CommandBuffer` classes and through **Abstractions** extension methods. In practice you need Abstractions to allocate command buffers or map memory.

**Logging:** `Vulkan.Log` is a `LogCategory` named "Vulkan" (Brutal.Logging).

### 2.3 Core wrapper API (Brutal.Vulkan, hand-written)

```csharp
// Instance
public static Instance Vulkan.CreateInstance(Instance.CreateInfo createInfo);
public unsafe ref struct Instance.CreateInfo {          // NO defaults: both HashSets must be non-null
    public VkApplicationInfo AppInfo; public VkInstanceCreateFlags Flags;
    public HashSet<string> EnabledExtensions; public HashSet<string> EnabledLayers;
    public void* Next; public VkAllocator? Allocator; }
Instance: readonly PhysicalDevice[] PhysicalDevices (enumerated in ctor); bool IsExtensionEnabled(string);
          VkInstance Handle; ref VkInstance.FunctionTable PInvoke; void Dispose();
          PfnVkVoidFunction GetProcAddr(ReadOnlySpan<byte> name)  // ext method

// PhysicalDevice
Instance Instance; HashSet<string> ExtensionNames; bool SupportsExtension(ReadOnlySpan<byte> utf8) / (string);
int GetFirstQueueFamilyWithFlag(VkQueueFlags);   // -1 if none
int FindMemoryType(int typeFilter, VkMemoryPropertyFlags required[, VkMemoryPropertyFlags preferred]); // throws
Device CreateDevice(Device.CreateInfo);
// generated ext: GetProperties(), GetFeatures(), GetFeatures2(), GetMemoryProperties(), GetFormatProperties(VkFormat),
// GetQueueFamilyProperties_Count/_Write, Bool32 GetSurfaceSupportKHR(int family, VkSurfaceKHR),
// VkSurfaceCapabilitiesKHR GetSurfaceCapabilitiesKHR(VkSurfaceKHR), GetSurfaceFormatsKHR_Count/_Write,
// GetSurfacePresentModesKHR_Count/_Write, EnumerateDeviceExtensionProperties_Count/_Write

// Device
public ref struct Device.CreateInfo() { HashSet<string> Extensions = []; Ptr<VkPhysicalDeviceFeatures> Features = null;
    ReadOnlySpan<VkDeviceQueueCreateInfo> QueueCreateInfos = []; Ptr Next = null; VkAllocator? Allocator = null; }
Device: PhysicalDevice PhysicalDevice; HashSet<string> EnabledExtensions; Queue GetQueue(int family, int index) (cached);
        void Dispose(); VkDevice Handle; ref VkDevice.FunctionTable PInvoke;
        void WaitIdle() (vkDeviceWaitIdle, generated ext)

// Queue
int Family; int Index; Device Device;
void WaitIdle(); void Submit(Span<VkSubmitInfo>, VkFence); void Submit2(Span<VkSubmitInfo2>, VkFence);
VkResult PresentKHR(VkPresentInfoKHR); void BindSparse(ReadOnlySpan<VkBindSparseInfo>, VkFence);
void Begin/End/InsertDebugUtilsLabelEXT(...)

// VkAllocator: class VkAllocator(VkAllocationCallbacks) : IDisposable — implicit to VkAllocationCallbacks*
```

Representative generated device signatures (`DEV` = `this VulkanHandleWrapper<VkDevice, VkDevice.FunctionTable>`; `T` = command-buffer wrapper):
```csharp
VkFence CreateFence(DEV, in VkFenceCreateInfo, VkAllocator?);       void ResetFences(DEV, ReadOnlySpan<VkFence>)
VkResult WaitForFences(DEV, ReadOnlySpan<VkFence>, Bool32 waitAll, nint timeout)
VkSemaphore CreateSemaphore(DEV, in VkSemaphoreCreateInfo, VkAllocator?)
VkSwapchainKHR CreateSwapchainKHR(DEV, in VkSwapchainCreateInfoKHR, VkAllocator?)
int GetSwapchainImagesKHR_Count(DEV, VkSwapchainKHR); VkResult GetSwapchainImagesKHR_Write(DEV, VkSwapchainKHR, ReadOnlySpan<VkImage>)
VkResult AcquireNextImageKHR(DEV, VkSwapchainKHR, nint timeout, VkSemaphore, VkFence, out int imageIndex)
VkImageView CreateImageView(DEV, in VkImageViewCreateInfo, VkAllocator?)
VkCommandPool CreateCommandPool(DEV, in VkCommandPoolCreateInfo, VkAllocator?); void ResetCommandPool(DEV, VkCommandPool, flags)
VkShaderModule CreateShaderModule(DEV, in VkShaderModuleCreateInfo, VkAllocator?)   // CodeSize: ByteSize64, Code: int*
VkPipelineLayout CreatePipelineLayout(DEV, in VkPipelineLayoutCreateInfo, VkAllocator?)
VkResult CreateGraphicsPipelines(DEV, VkPipelineCache, ReadOnlySpan<VkGraphicsPipelineCreateInfo>, VkAllocator?, ReadOnlySpan<VkPipeline> outPipelines)
VkDescriptorSetLayout CreateDescriptorSetLayout(DEV, in ..., VkAllocator?); VkDescriptorPool CreateDescriptorPool(DEV, in ..., VkAllocator?)
void AllocateDescriptorSets(DEV, in VkDescriptorSetAllocateInfo, ReadOnlySpan<VkDescriptorSet> outSets)
void UpdateDescriptorSets(DEV, ReadOnlySpan<VkWriteDescriptorSet>, ReadOnlySpan<VkCopyDescriptorSet>)
VkSampler CreateSampler(DEV, in VkSamplerCreateInfo, VkAllocator?)
VkBuffer CreateBuffer(...); VkImage CreateImage(...); VkDeviceMemory AllocateMemory(DEV, in VkMemoryAllocateInfo, VkAllocator?)
void BindBufferMemory(DEV, VkBuffer, VkDeviceMemory, ByteSize64); VkMemoryRequirements GetBufferMemoryRequirements(DEV, VkBuffer)
void UnmapMemory(DEV, VkDeviceMemory); FlushMappedMemoryRanges(DEV, ReadOnlySpan<VkMappedMemoryRange>)
VkDeviceAddress GetBufferDeviceAddress(DEV, in VkBufferDeviceAddressInfo); void SetDebugUtilsObjectNameEXT(DEV, in ...)
Destroy*(DEV, handle, VkAllocator?)   // DestroyBuffer, DestroyImage, DestroyImageView, DestroyFence, ...
// command buffer (generic T)
Begin<T>(in VkCommandBufferBeginInfo); End<T>(); Reset<T>(flags)
BeginRendering<T>(in VkRenderingInfo); EndRendering<T>(); PipelineBarrier2<T>(in VkDependencyInfo)
BindPipeline<T>(VkPipelineBindPoint, VkPipeline)
BindDescriptorSets<T>(bindPoint, VkPipelineLayout, int firstSet, ReadOnlySpan<VkDescriptorSet>, Span<ByteSize32> dynOffsets)
PushConstants<T>(VkPipelineLayout, VkShaderStageFlags, int offset, ReadOnlySpan<byte>)
PushDescriptorSet<T>(bindPoint, layout, int set, ReadOnlySpan<VkWriteDescriptorSet>)  // 1.4 core; KHR variant takes (count, ptr)
BindVertexBuffers<T>(int firstBinding, ReadOnlySpan<VkBuffer>, ReadOnlySpan<ByteSize64>); BindIndexBuffer<T>(VkBuffer, ByteSize64, VkIndexType)
SetViewport<T>(int first, ReadOnlySpan<VkViewport>); SetScissor<T>(int first, ReadOnlySpan<VkRect2D>)
SetCullMode<T>(...); SetPrimitiveTopology<T>(...)   // dynamic state 1.3
Draw<T>(int vtx, int inst, int firstVtx, int firstInst); DrawIndexed<T>(int idx, int inst, int firstIdx, int vtxOffset, int firstInst)
CopyBuffer<T>(VkBuffer, VkBuffer, ReadOnlySpan<VkBufferCopy>); CopyBufferToImage<T>(VkBuffer, VkImage, VkImageLayout, ReadOnlySpan<VkBufferImageCopy>)
BlitImage2<T>(in VkBlitImageInfo2); Dispatch<T>(x, y, z); BeginRenderPass<T>(in VkRenderPassBeginInfo, VkSubpassContents)
// instance
VkDebugUtilsMessengerEXT CreateDebugUtilsMessengerEXT(INST, in VkDebugUtilsMessengerCreateInfoEXT, VkAllocator?)
void DestroySurfaceKHR(INST, VkSurfaceKHR, VkAllocator?)
```

### 2.4 Brutal.Vulkan.Abstractions: full public surface

**Device and allocation**
- `class DeviceEx : Device, IGlobalAllocator`. Create it with `physicalDevice.CreateDevice(new DeviceEx.CreateInfo{...})` (the `DeviceEx.CreateInfo` fields are identical to `Device.CreateInfo`).
  - It implements `AllocateBuffer`/`AllocateImage` as **one `vkAllocateMemory` per resource** (the naive path, fine for prototyping) and `FreeAllocation(...)`.
  - Map/unmap is ref-counted per buffer allocation. Images are not mappable.
- Interfaces:
  - `IAllocator { Device GetDevice(); VkAllocator? VkAllocator; Ptr MapMemory(in AllocationMapInfo); void UnmapMemory(in AllocationMapInfo); }`
  - `IBufferAllocator : IAllocator { BufferAllocationInfo AllocateBuffer(in string name, in VkBufferCreateInfo, in AllocationInfo.CreateInfo); void FreeAllocation(in BufferAllocationInfo); }`
  - `IImageAllocator` mirrors it with `ImageAllocationInfo`.
  - `IGlobalAllocator : IBufferAllocator, IImageAllocator`.
- `readonly record struct AllocationInfo(IAllocator Allocator, VkDeviceMemory Memory, ByteSize AllocSize, ByteSize AllocOffset, int MemoryTypeIndex, nint AllocHandle)`.
  - Nested: `record struct AllocationInfo.CreateInfo(MemoryPreference Preference, VkMemoryPropertyFlags RequiredProperties, VkMemoryPropertyFlags PreferredProperties, float Priority, Ptr Next) { VkMemoryAllocateFlags AllocateFlags {get;set;} }`.
  - `enum MemoryPreference : byte { None, PreferGpu, PreferCpu }`.
- `readonly record struct BufferAllocationInfo(VkBuffer VkBuffer, ByteSize BufferOffset, ByteSize BufferSize, VkBufferUsageFlags Usage, AllocationInfo AllocationInfo) : IMappable`.
- `struct ImageAllocationInfo { VkImage Image; VkExtent3D Extent; VkFormat Format; int MipLevels; int ArrayLayers; AllocationInfo MemAllocationInfo; }`.

**Resources**
- `readonly struct BufferEx : IDisposable, INullable, IMappable, IBindBuffer`. Create it with `allocator.CreateBuffer(new BufferEx.CreateInfo{...})` (extension on `IBufferAllocator`).
  - `BufferEx.CreateInfo` is a ref struct with `required string Name`, `BufferFlags`, `ByteSize BufferSize`, `VkBufferUsageFlags BufferUsage`, `BufferSharingMode`, `BufferQueueFamilyIndexCount/Indices`, `Ptr BufferNext`, `MemoryPreference AllocPreference`, `AllocRequiredProperties`, `AllocPreferredProperties`, `float AllocPriority`, `Ptr AllocNext` and `VkMemoryAllocateFlags AllocFlags`. It also has get/set views `BufferCreateInfo` and `AllocationInfo`.
  - Members: `VkBuffer`, `VkDeviceMemory`, `BufferSize`, `BindOffset`, `Usage`, `GetDeviceAddress()`, `AsPartition()` (implicit to `BufferPartitionInfo`), `Dispose()` (frees through its allocator) and `new BufferEx(BufferAllocationInfo)`.
- `readonly struct ImageEx : IDisposable, INullable`. Create it with `allocator.CreateImage(new ImageEx.CreateInfo{...})`.
  - `ImageEx.CreateInfo` fields: `required Name`, `ImageFlags`, `ImageType`, `ImageFormat`, `VkExtent3D ImageExtent`, `ImageMipLevels`, `ImageArrayLayers`, `VkSampleCountFlags ImageSamples`, `ImageTiling`, `ImageUsage`, `ImageSharingMode`, `ReadOnlySpan<int> ImageQueueFamilyIndices`, `ImageInitialLayout`, `ImageNext` and the `Alloc*` fields as in BufferEx.
  - Members: `VkImage`, `VkDeviceMemory`, `Extent`, `Format`, `MipLevels`, `ArrayLayers`, `MaxMipLevels`, `VkImageSubresourceRange GetFullRange(VkImageAspectFlags)`, `ImageViewEx CreateImageView(VkImageViewType, VkImageSubresourceRange, VkAllocator?)` and `Dispose()`.
- `readonly struct ImageViewEx : IDisposable, IImageView, IImageSubresourceRange` with `Image`, `VkImageView`, `Type`, `SubresourceRange`, `AsPartition()` and `Dispose()` (destroys only the view).

**Mapping**
- `MappableExtensions.Map<T>(this T) where T : IMappable` and `Map<T>(this T, IMappable.MapInfo)` return a `MappedMemory`.
- `readonly struct MappedMemory : IDisposable`:
  - Data: `Ptr MapPtr`, `MapRegion Region`, `VkDeviceMemory DeviceMemory`.
  - Access: `Span<T> AsSpan<T>()`, `Span<byte> AsSpan()`, `ref T As<T>()`, `Offset(ByteSize)` and `Offset(MapRegion)`.
  - Lifetime and coherence: `Flush()`, `Invalidate()`, `Unmap()`; `Dispose()` calls `Unmap()`.
- `struct MapRegion() { ByteSize Offset = 0; ByteSize Size = VK.WHOLE_SIZE; }`.
- `readonly struct MapInfo(VkDeviceMemory) { MapRegion Region; VkMemoryMapFlags Flags; }` (implicit from `VkDeviceMemory`).
- Raw device helpers:
  - `Ptr MapMemory(this Device, in MapInfo)`
  - `Ptr<T> MapMemory<T>(this Device, in MapInfo)`
  - `WriteMemory<T>(this Device, ReadOnlySpan<T>/Span<T> src, VkDeviceMemory dst, ByteSize offset = default, VkMemoryMapFlags = None)`, which maps, copies and unmaps.

**Upload**
- `class StagingPool : IDisposable`. Create it with `allocator.CreateStagingPool(Queue queue, int initialCmdBufCount, VkCommandBufferLevel level = Primary)`. It owns a command pool on `queue.Family`, plus fences and host-coherent `TransferSrcBit` staging buffers recycled per size.
  - `CommandBuffer NextCommandBuffer()`: you must `Begin`/`End` it yourself.
  - `BufferEx AddStagingBuffer(ByteSize)`, `AddStagingBuffer<T>(ElementCount)`, and `AddStagingBuffer<T>(Span<T> data)` (maps and copies).
  - `StagingPool Submit()` and `Submit(Span<VkSemaphore> wait, Span<VkPipelineStageFlags> waitStages, Span<VkSemaphore> signal)`.
  - `bool WaitFor(nint timeout)` and `void Wait()` recycle buffers, fences and command buffers. `Dispose()` submits and waits.

**Command-buffer extensions** (on `CommandBuffer`):
- `Begin()` (flags None), `Begin(VkCommandBufferUsageFlags)`, `Begin(usage, VkCommandBufferInheritanceInfo)` and `Reset()`.
- `CopyBuffer(src, dst, VkBufferCopy)` and `CopyBuffer(src, dst, ByteSize size)`.
- `BindVertexBuffer(int binding, VkBuffer, ByteSize offset = default)`.
- `PushConstants<T>(layout, stages, ByteSize offset, T data)` and an overload taking `ReadOnlySpan<T>`.
- Debug labels: `BeginDebugLabel(string, in float4 color = default)`, `EndDebugLabel()`, and `using var _ = cmd.DebugScope("name")`.
- Descriptor binding: `BindDescriptorSet(bindPoint, layout, int set, VkDescriptorSet, Span<ByteSize32> dynOffsets)` and `BindDescriptorSet(bindPoint, layout, int set, IDescriptorSetWrapper)`.
- `ExecuteCommands(ReadOnlySpan<CommandBuffer>)`.
- From `BindBufferExtensions` (on any `IBindBuffer`, such as `BufferEx` or a partition): `BindVertexBuffer<T>`, `BindVertexBuffers<T>`, `BindIndexBuffer<T>(cmd, buffer, VkIndexType)` and `DrawIndirect<T>`.

**Device extensions**
- `CommandBuffer AllocateCommandBuffer(this Device, VkCommandBufferAllocateInfo)` (forces count = 1) and `AllocateCommandBuffers(this Device, VkCommandBufferAllocateInfo, Span<CommandBuffer>)`.
- `FreeCommandBuffers(this DEV, VkCommandPool, ReadOnlySpan<CommandBuffer>)`.
- `VkPipelineLayout CreatePipelineLayout(this Device, ReadOnlySpan<VkDescriptorSetLayout>, ReadOnlySpan<VkPushConstantRange>, VkAllocator?)`.
- `VkPipeline CreateGraphicsPipeline(this Device, VkPipelineCache, VkGraphicsPipelineCreateInfo, VkAllocator?)` and the matching `CreateComputePipeline(...)`.
- `VkRenderPass CreateDefaultRenderPass(this Device, bool sampleColor, bool sampleDepth, VkSampleCountFlags, VkFormat color, VkFormat depth, VkFormat resolved, VkAllocator?)`, for the legacy render-pass path.
- `VkDeviceMemory AllocateMemory(this Device, PhysicalDevice, VkBuffer|VkImage, VkMemoryPropertyFlags required, VkAllocator?)`.
- `VkFence CreateFence(this Device, VkFenceCreateFlags, VkAllocator?)` and `VkResult WaitForFence(this Device, VkFence, nint timeout)`.
- `VkDeviceAddress GetBufferDeviceAddress(this Device, VkBuffer)`.
- `SetDebugName<T>(this Device, T handle, string|ReadOnlySpan<byte> name)` is **`[Conditional("DEBUG")]`**, so calls vanish in your Release builds. It is also a no-op unless `VK_EXT_debug_utils` was enabled on the instance.

**Physical device**
- `DeviceEx CreateDevice(this PhysicalDevice, DeviceEx.CreateInfo)`.
- `int FindQueueFamily(this PhysicalDevice, VkQueueFlags required, VkQueueFlags excluded = None)` prefers the family with the fewest extra flags, so dedicated transfer and compute families win. It returns -1 when no family qualifies.
- `VkFormat FindSupportedFormat(this PhysicalDevice, ReadOnlySpan<VkFormat> candidates, VkImageTiling, VkFormatFeatureFlags)` returns `Undefined` when none qualifies.
- `VulkanDeviceInfo.GetDeviceName(this PhysicalDevice)`, `GetDeviceName(in VkPhysicalDeviceProperties)`, `FormatApiVersion(int)` and `FormatDriverVersion(...)`.

**Descriptors**
- `DescriptorSetLayoutEx`: create with `device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo{ Bindings = span, Flags, Next }, VkAllocator?)`. It tracks `Dictionary<VkDescriptorType,int> Descriptors`, converts implicitly to `VkDescriptorSetLayout`, and implements `Dispose()`.
- `DescriptorPoolEx`: create with `device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo{ MaxSets, Flags, PoolSizes = span, Next }, VkAllocator?)`.
  - It tracks remaining sets and descriptors per type and throws `DescriptorPoolOutOfMemoryException` (`OutOfSets`/`OutOfDescriptors`) or `DescriptorPoolInvalidOperationException` instead of returning `VK_ERROR_OUT_OF_POOL_MEMORY`.
  - Allocation: `device.AllocateDescriptorSet(pool, layoutEx)` returns a `VkDescriptorSet`. Also `AllocateDescriptorSets(pool, layoutEx, Span<VkDescriptorSet> out)` and `AllocateDescriptorSets(pool, Span<DescriptorSetLayoutEx>, Span<VkDescriptorSet>)`.
  - Freeing: `FreeDescriptorSet(pool, set)` and `FreeDescriptorSets(pool, span)`. These need `FreeDescriptorSetBit` on the pool (standard Vulkan rule; BRUTAL validation not checked).
  - **No descriptor-write helper exists.** Fill `VkWriteDescriptorSet` yourself.
- Interfaces: `IDescriptorSetLayoutWrapper { VkDescriptorSetLayout DescriptorSetLayout }` and `IDescriptorSetWrapper : IDescriptorSetLayoutWrapper { VkDescriptorSet DescriptorSet; ReadOnlySpan<ByteSize32> DynamicOffsets => []; }`.

**Pipeline presets** (`Presets`, all `static readonly PtrOwner<T>`, which converts implicitly to `T*` and can be assigned straight into create-info pointer fields):
- `Presets.BlendState.{BlendColorAlpha, BlendNone, BlendAdditive, BlendSoftAdditive, BlendMultiplicative}` (each a `VkPipelineColorBlendStateCreateInfo` with 1 attachment).
- `Presets.BlendAttachment.{BlendNone, BlendColorAlpha, BlendColorAdditive, BlendColorSoftAdditive, BlendColorMultiplicative}`. `BlendColorAlpha` is straight alpha: color `SrcAlpha/OneMinusSrcAlpha`, alpha `One/Zero`.
- `Presets.DepthStencil.{DepthTestWrite, DepthTestNoWrite, DepthTestWriteStencil, DepthNoTestNoWriteStencil, NoDepthTest}`.
- `Presets.InputAssembly.{PointList, TriangleList, TriangleStrip, LineList, LineStrip, PatchList}`.
- `Presets.Multisample.None`.
- `Presets.Rasterization.Fill.{CullBack, CullFront, CullNone}` and `Presets.Rasterization.Line.{...}`.
- `Presets.Components.Default`.
- `Presets.VertexInput.None` (a `NativeList`).
- `Presets.Entrypoint.Main` (implicit to `byte*`).
- `Presets.ShaderStage.{Make(stage, module), Vertex(module), Fragment, Geometry, Evaluation, Control, Compute}` return `VkPipelineShaderStageCreateInfo` with entry point `"main"`.
- `Presets.Sampler.{SamplerLinearClamped, SamplerLinearRepeat, SamplerAnisotropicClamped, SamplerAnisotropicRepeat, SamplerPointClamped}` return `VkSamplerCreateInfo` values.

**Shaders**
- `static VkShaderModule ShaderModuleExtensions.FromSpirv(Device, ReadOnlySpan<byte> spirv, VkAllocator?)`. This is a static method, not an extension.
- `class ShaderStages(Device, Span<VkPipelineShaderStageCreateInfo>, VkAllocator?) : IDisposable` with `Count`, implicit `VkPipelineShaderStageCreateInfo*`, `Slice(...)`, and `Dispose()` that destroys the modules.

**Vertex input**
- `class VertexInput` builds `VkPipelineVertexInputStateCreateInfo` by reflecting blittable struct fields and converts implicitly to `VkPipelineVertexInputStateCreateInfo*`.
- Factories: `VertexInput.For<TVertex>()` (binding 0, per-vertex, locations from 0) and `VertexInput.For<TVertex,TInstance>()` (binding 1 per-instance).
- Builder methods: `AddBinding(...)`, `AddAttribute(...)`, `AddAttributes<T>(firstLocation, binding, offset)`, `AddBindingWithAttributes<T>(...)`, `Check()` and `Test()`.
- Field attributes: `[VertexFormat(VkFormat.R8G8B8A8UNorm)]` overrides a field's format, for example a packed color `uint`. `[FixedPointConversion(Behavior.Normalize|Scale|None)]` controls how small integer types map.

**Partitioners** (sub-allocation inside one buffer or image; useful for dynamic vertex rings and texture atlases)
- `BufferPartitionInfo` (`VkBuffer`, `BindOffset`, `MemoryOffset`, `Free()`).
- `LinearBufferPartitioner(BufferPartitionInfo backing, ByteSize alignment = default)` is a bump allocator (`TryCreatePartition`, `Reset`).
- `BinaryTreeBufferPartitioner` is a buddy allocator.
- `ImageViewPartitionInfo` (`GetNormalizedOffset()`/`GetNormalizedExtent()` give UV rects).
- Image partitioners: `Atlas2DPartitioner(ImageViewPartitionInfo)`, `QuadTreePartitioner(backing, maxElementSize = 0, minElementSize = 1)` and `TextureArrayPartitioner(backing)`.
- `partitioner.CreatePartition(new BufferPartitionInfo.CreateInfo(size))` or `new ImageViewPartitionInfo.CreateInfo(extent, layers)` throws `OutOfMemoryException` when full. `ImageStagingRange.FromImage/FromPartition` produce copy regions.

**Misc**
- `Subresources.Full(aspect)`, `Single(aspect, mip = 0, layer = 0)`, `Mips(...)`, `Layers(...)` and `range.ToLayers(mip)`.
- `VkFormat.IsDepthFormat()`, `IsStencilFormat()`, `IsDepthStencilFormat()` and `GetAspectMask()`.
- `ByteSize.AlignUp(alignment)` and `IsAlignedTo(...)`.
- `Span<T>.Dispose()`, `T[].Dispose()` and `IEnumerable<T>.Destroy()`.
- `FrameResources(Device)` and `SyncObjects(Device)` are POCO handle bags with `Dispose()`. `SyncObjects` has `required ImageReadySemaphore`, `RenderingFinishedSemaphore`, `Fence`, `CommandBuffer` and `Allocator`.

**VulkanHelpers (static)**
- `MaxMipLevels(w, h[, d])` and `MaxMipLevels(VkExtent3D)`.
- `enum Api { VERSION_1_0 ... VERSION_1_4 }`.
- `Validation.IsSupported`: true if `VK_LAYER_KHRONOS_validation` is present.
- `AddPlatformRequiredExtensions(HashSet<string>)`, which only does something on macOS (portability).
- `AddDebugExtensions(set)` adds **both** `VK_EXT_debug_utils` and `VK_EXT_debug_report`. debug_report is deprecated and may be missing on some loaders or ICDs, which would cause a throw (UNVERIFIED on lavapipe). Adding only `VK.EXT_DEBUG_UTILS_EXTENSION_NAME` is safer.
- `AddSurfaceExtensions(set)` (only `VK_KHR_surface`, **not** the platform one; use GLFW for that), `AddSwapchainExtensions(set)` and `AddValidationLayers(set)`.
- `GetPlatformRequiredInstanceCreateFlags()` and `AddMandatoryIfSupportedDeviceExtensions(set, pd)` (adds `VK_KHR_portability_subset`).
- `BuildDefaultDebugMessengerCreateInfo(PfnVkDebugUtilsMessengerCallbackEXT)`.
- `DebugUtilsMessengerCallbacks.{Quiet, Default, Verbose}` are `[UnmanagedCallersOnly]` callbacks logging through `Vulkan.Log`. `VerboseLoggingEnabled` toggles info and verbose output for `Default`.

### 2.5 Brutal.Vulkan.Vma

```csharp
public partial class VmaAllocator(Device device, VmaAllocatorHandle h) : IVmaAllocator, IImageAllocator, IBufferAllocator
    // Abstractions integration: AllocateBuffer(in string name, in VkBufferCreateInfo, in AllocationInfo.CreateInfo),
    // AllocateImage(...), FreeAllocation(...), MapMemory/UnmapMemory (vmaMapMemory; VMA ref-counts), GetDevice()
    // AllocationInfo.AllocHandle = VmaAllocation handle.
static class VmaDeviceExtensions {
    VmaAllocator CreateVmaAllocator(this Device, VmaAllocatorCreateInfo);   // fills Instance + vkGet*ProcAddr if unset
    VmaAllocatorHandle CreateVmaAllocatorHandle(this Device, VmaAllocatorCreateInfo);
    void DestroyVmaAllocator(this Device, VmaAllocator);                     // VmaAllocator has NO Dispose
}
struct VmaAllocatorCreateInfo { VmaAllocatorCreateFlags Flags; VkPhysicalDevice PhysicalDevice; VkDevice Device;
    ByteSize PreferredLargeHeapBlockSize; Ptr<VkAllocationCallbacks> PAllocationCallbacks; Ptr<VmaDeviceMemoryCallbacks> PDeviceMemoryCallbacks;
    Ptr<ByteSize> PHeapSizeLimit; Ptr<VmaVulkanFunctions> PVulkanFunctions; VkInstance Instance; uint VulkanApiVersion; Ptr<VkFlags> PTypeExternalMemoryHandleTypes; }
struct VmaAllocationCreateInfo { VmaAllocationCreateFlags Flags; VmaMemoryUsage Usage; VkMemoryPropertyFlags RequiredFlags, PreferredFlags;
    uint MemoryTypeBits; VmaPool Pool; Ptr PUserData; float Priority; ByteSize MinAlignment; }
struct VmaAllocationInfo { uint MemoryType; VkDeviceMemory DeviceMemory; ByteSize Offset; ByteSize Size; Ptr PMappedData; Ptr PUserData; Ptr<byte> PName; }
enum VmaMemoryUsage : uint { Unknown, GpuOnly, CpuOnly, CpuToGpu, GpuToCpu, CpuCopy, GpuLazilyAllocated, Auto, AutoPreferDevice, AutoPreferHost }
enum VmaAllocationCreateFlags : uint { DedicatedMemoryBit, NeverAllocateBit, MappedBit, ..., HostAccessSequentialWriteBit, HostAccessRandomBit, HostAccessAllowTransferInsteadBit, Strategy* }
enum VmaAllocatorCreateFlags : uint { ExternallySynchronizedBit, KhrDedicatedAllocationBit, KhrBindMemory2Bit, ExtMemoryBudgetBit, BufferDeviceAddressBit, ExtMemoryPriorityBit, KhrMaintenance4Bit, KhrMaintenance5Bit, ... }
```
Raw VMA API: a C# 14 `extension<T>(T allocator) where T : IVmaAllocator` block in `VmaAllocatorEx`, called as `vma.Xxx(...)`. All of these return a `VkResult` and **do not throw**:
```csharp
VkResult CreateBuffer(VkBufferCreateInfo, VmaAllocationCreateInfo, out VkBuffer, out VmaAllocation, out VmaAllocationInfo)
VkResult CreateImage(VkImageCreateInfo, VmaAllocationCreateInfo, out VkImage, out VmaAllocation, out VmaAllocationInfo)
void DestroyBuffer(VkBuffer, VmaAllocation); void DestroyImage(VkImage, VmaAllocation)
VkResult MapMemory(VmaAllocation, PtrPtr ppData); void UnmapMemory(VmaAllocation)
VkResult FlushAllocation(VmaAllocation, ByteSize offset, ByteSize size); VkResult InvalidateAllocation(...)
VkResult CopyMemoryToAllocation(Ptr src, VmaAllocation dst, ByteSize dstOffset, ByteSize size)
VkResult CopyAllocationToMemory(VmaAllocation, ByteSize, Ptr dst, ByteSize)
VkResult AllocateMemory(VkMemoryRequirements, VmaAllocationCreateInfo, out VmaAllocation, out VmaAllocationInfo); FreeMemory(VmaAllocation)
VkResult AllocateMemoryForBuffer/ForImage(...); BindBufferMemory/BindImageMemory(...)
void GetAllocationInfo(VmaAllocation, out VmaAllocationInfo); SetAllocationName(VmaAllocation, ReadOnlySpan<byte>)
void SetCurrentFrameIndex(uint); void GetHeapBudgets(Span<VmaBudget>); void CalculateStatistics(out VmaTotalStatistics)
CreatePool/DestroyPool, defragmentation (Begin/EndDefragmentation[Pass]), virtual blocks (CreateVirtualBlock, VirtualAllocate, VirtualFree, ...),
BuildStatsString(out Ptr<byte>, Bool32)/FreeStatsString, FindMemoryTypeIndex[ForBufferInfo|ForImageInfo]
```
Binding bugs (by inspection):
- `CreateBufferWithAlignment`, `CreateDedicatedBuffer` and `AllocateDedicatedMemory` take output params **by value**, not `out`, so their outputs are lost. Avoid them.
- `GetAllocationMemoryProperties(VmaAllocation, VkMemoryPropertyFlags)` has the same problem.

Abstractions-path behavior (`VmaAllocator.ConvertInfo`):
- `PreferGpu` maps to `Usage = AutoPreferDevice`.
- `PreferCpu` maps to `Usage = Auto` plus `HostAccessRandomBit`.
- Requiring `HostVisible` or `HostCoherent` adds `HostAccessRandomBit`.
- `MappedBit` is **never** set, so `BufferEx.Map()` calls `vmaMapMemory` each time. For persistently mapped rings, use the raw `CreateBuffer` with `MappedBit | HostAccessSequentialWriteBit` and read `info.PMappedData`.
- On failure it throws a plain `Exception("Vma Failed to create buffer")`.
- Set `VulkanApiVersion = (uint)VulkanHelpers.Api.VERSION_1_3`. If it is left at 0, VMA assumes 1.0.

### 2.6 Idiomatic snippets

Project settings: `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` is required, because every create-info uses pointers. Assumed usings: `Brutal`, `Brutal.Numerics`, `Brutal.Pointers`, `Brutal.Pointers.Extensions`, `Brutal.VulkanApi`, `Brutal.VulkanApi.Abstractions`, `Brutal.VulkanApi.Vma`, `Brutal.GlfwApi`, `System.Runtime.InteropServices`.

**Instance with GLFW extensions and validation**
```csharp
unsafe {
    var exts = new HashSet<string>();
    byte** req = Glfw.GetRequiredInstanceExtensions(out uint n);   // VK_KHR_surface + VK_KHR_xlib/wayland/win32_surface
    for (var i = 0; i < n; i++) exts.Add(Marshal.PtrToStringUTF8((nint)req[i])!);
    VulkanHelpers.AddPlatformRequiredExtensions(exts);
    var layers = new HashSet<string>();
    bool debug = VulkanHelpers.Validation.IsSupported;
    if (debug) { VulkanHelpers.AddValidationLayers(layers); exts.Add(VK.EXT_DEBUG_UTILS_EXTENSION_NAME); }

    using var instance = Vulkan.CreateInstance(new Instance.CreateInfo {
        AppInfo = new VkApplicationInfo {
            ApplicationName = "ONI"u8.AsPointer(), EngineName = "oni-brutal"u8.AsPointer(),
            ApiVersion = (int)VulkanHelpers.Api.VERSION_1_3 },
        Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
        EnabledExtensions = exts, EnabledLayers = layers,           // must not be null
    });                                                            // throws VkResultError on failure
    VkDebugUtilsMessengerEXT messenger = default;
    if (debug)
        messenger = instance.CreateDebugUtilsMessengerEXT(
            VulkanHelpers.BuildDefaultDebugMessengerCreateInfo(
                new PfnVkDebugUtilsMessengerCallbackEXT(&VulkanHelpers.DebugUtilsMessengerCallbacks.Default)),
            null);

    foreach (var pd in instance.PhysicalDevices)
        Console.WriteLine($"{pd.GetDeviceName()} {pd.GetProperties().DeviceType} api {VulkanDeviceInfo.FormatApiVersion(pd.GetProperties().ApiVersion)}");

    // Surface from GLFW (window created with GlfwWindowHint.ClientApi = 0 / GLFW_NO_API)
    if (Glfw.CreateWindowSurface(instance.Handle.VkHandle, window, out ulong rawSurface) != 0) throw new Exception("surface");
    var surface = new VkSurfaceKHR((nint)rawSurface);
    // ... later: instance.DestroySurfaceKHR(surface, null); instance.DestroyDebugUtilsMessengerEXT(messenger, null);
}
```

**Device and queue selection (Vulkan 1.3: dynamic rendering + sync2)**
```csharp
var pd = instance.PhysicalDevices.OrderByDescending(p => p.GetProperties().DeviceType == VkPhysicalDeviceType.DiscreteGpu).First();
int gfx = pd.FindQueueFamily(VkQueueFlags.GraphicsBit);
if (gfx < 0 || !pd.GetSurfaceSupportKHR(gfx, surface)) throw new Exception("no present-capable graphics queue");
float prio = 1f;
var qci = new VkDeviceQueueCreateInfo { QueueFamilyIndex = gfx, QueueCount = 1, QueuePriorities = &prio };
var f13 = new VkPhysicalDeviceVulkan13Features { DynamicRendering = true, Synchronization2 = true };
var devExts = new HashSet<string> { VK.KHR_SWAPCHAIN_EXTENSION_NAME };
VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(devExts, pd);
using DeviceEx device = pd.CreateDevice(new DeviceEx.CreateInfo {      // ext method -> DeviceEx (also an allocator)
    Extensions = devExts,
    QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(&qci, 1),
    Next = new Ptr(&f13),
});
Queue queue = device.GetQueue(gfx, 0);
```

**Swapchain**
```csharp
var caps = pd.GetSurfaceCapabilitiesKHR(surface);
Span<VkSurfaceFormatKHR> fmts = new VkSurfaceFormatKHR[pd.GetSurfaceFormatsKHR_Count(surface)];
pd.GetSurfaceFormatsKHR_Write(surface, fmts);
var fmt = fmts[0];
foreach (var f in fmts) if (f.Format == VkFormat.B8G8R8A8SRGB && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR) fmt = f;
var extent = caps.CurrentExtent.Width != -1 ? caps.CurrentExtent : new VkExtent2D(fbW, fbH);   // -1 == 0xFFFFFFFF (Wayland)
var swapchain = device.CreateSwapchainKHR(new VkSwapchainCreateInfoKHR {
    Surface = surface, MinImageCount = Math.Max(caps.MinImageCount, 2), ImageFormat = fmt.Format, ImageColorSpace = fmt.ColorSpace,
    ImageExtent = extent, ImageArrayLayers = 1,
    ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit,
    ImageSharingMode = VkSharingMode.Exclusive, PreTransform = caps.CurrentTransform,
    CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR, PresentMode = VkPresentModeKHR.FifoKHR, Clipped = true,
    OldSwapchain = oldSwapchain }, null);
var images = new VkImage[device.GetSwapchainImagesKHR_Count(swapchain)];
device.GetSwapchainImagesKHR_Write(swapchain, images);
var views = images.Select(img => device.CreateImageView(new VkImageViewCreateInfo {
    Image = img, ViewType = VkImageViewType._2D, Format = fmt.Format,
    SubresourceRange = Subresources.Full(VkImageAspectFlags.ColorBit) }, null)).ToArray();
// destroy: device.DestroyImageView(v, null); device.DestroySwapchainKHR(swapchain, null);
```

**Per-frame objects and the dynamic-rendering frame loop (sync2)**
```csharp
var pool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = gfx,
    Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit }, null);
CommandBuffer cmd = device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary });
var fence = device.CreateFence(VkFenceCreateFlags.SignaledBit, null);
var acquired = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
var renderDone = new VkSemaphore[images.Length];   // one per swapchain image (signal-before-present rule)
for (int i = 0; i < renderDone.Length; i++) renderDone[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);

// --- each frame ---
device.WaitForFence(fence, -1);
int imageIndex;
try { device.AcquireNextImageKHR(swapchain, -1, acquired, default, out imageIndex); }   // SuboptimalKHR returned, not thrown
catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR) { RecreateSwapchain(); return; }
device.ResetFences([fence]);
cmd.Reset();
cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

var toColor = new VkImageMemoryBarrier2 {
    SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit, SrcAccessMask = VkAccessFlags2.None,
    DstStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit, DstAccessMask = VkAccessFlags2.ColorAttachmentWriteBit,
    OldLayout = VkImageLayout.Undefined, NewLayout = VkImageLayout.ColorAttachmentOptimal,
    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,   // const int = ~0
    Image = images[imageIndex], SubresourceRange = Subresources.Full(VkImageAspectFlags.ColorBit) };
cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toColor });

var color = new VkRenderingAttachmentInfo {
    ImageView = views[imageIndex], ImageLayout = VkImageLayout.ColorAttachmentOptimal,
    LoadOp = VkAttachmentLoadOp.Clear, StoreOp = VkAttachmentStoreOp.Store,
    ClearValue = new VkClearColorValue { Float32 = new float4(0.1f, 0.1f, 0.1f, 1f) } };
cmd.BeginRendering(new VkRenderingInfo { RenderArea = new VkRect2D(extent), LayerCount = 1,
    ColorAttachmentCount = 1, ColorAttachments = &color });
cmd.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1 }]);
cmd.SetScissor(0, [new VkRect2D(extent)]);
cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
cmd.BindDescriptorSet(VkPipelineBindPoint.Graphics, layout, 0, set, []);
cmd.BindVertexBuffer(0, vb.VkBuffer);
cmd.Draw(vertexCount, 1, 0, 0);
cmd.EndRendering();

var toPresent = toColor with { SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
    SrcAccessMask = VkAccessFlags2.ColorAttachmentWriteBit, DstStageMask = VkPipelineStageFlags2.None, DstAccessMask = VkAccessFlags2.None,
    OldLayout = VkImageLayout.ColorAttachmentOptimal, NewLayout = VkImageLayout.PresentSrcKHR };
cmd.PipelineBarrier2(new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toPresent });
cmd.End();

var waitInfo   = new VkSemaphoreSubmitInfo { Semaphore = acquired, StageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit };
var signalInfo = new VkSemaphoreSubmitInfo { Semaphore = renderDone[imageIndex], StageMask = VkPipelineStageFlags2.AllCommandsBit };
var cbInfo     = new VkCommandBufferSubmitInfo { CommandBuffer = cmd };
queue.Submit2([new VkSubmitInfo2 { WaitSemaphoreInfoCount = 1, WaitSemaphoreInfos = &waitInfo,
    CommandBufferInfoCount = 1, CommandBufferInfos = &cbInfo, SignalSemaphoreInfoCount = 1, SignalSemaphoreInfos = &signalInfo }], fence);

var sc = swapchain; var rd = renderDone[imageIndex];
try {
    var r = queue.PresentKHR(new VkPresentInfoKHR { WaitSemaphoreCount = 1, WaitSemaphores = &rd,
        SwapchainCount = 1, Swapchains = &sc, ImageIndices = &imageIndex });
    if (r == VkResult.SuboptimalKHR) needsRecreate = true;
} catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR) { needsRecreate = true; }
```
The `with` expression on Vk structs keeps the private `sType`, because it copies the struct. Production code should keep N frames in flight; BRUTAL provides no frame ring.

**Graphics pipeline (dynamic rendering, no VkRenderPass)**
```csharp
VkShaderModule vs = ShaderModuleExtensions.FromSpirv(device, vertSpv, null), fs = ShaderModuleExtensions.FromSpirv(device, fragSpv, null);
Span<VkPipelineShaderStageCreateInfo> stages = [Presets.ShaderStage.Vertex(vs), Presets.ShaderStage.Fragment(fs)];
VkDescriptorSetLayout[] setLayouts = [setLayoutEx];                      // implicit from DescriptorSetLayoutEx
var layout = device.CreatePipelineLayout(setLayouts,
    [new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit, Offset = 0, Size = 64 }], null);
var vertexInput = VertexInput.For<MyVertex>();                         // reflects fields -> formats/offsets
var colorFmt = fmt.Format;
var renderingInfo = new VkPipelineRenderingCreateInfo { ColorAttachmentCount = 1, ColorAttachmentFormats = &colorFmt };
Span<VkDynamicState> dyn = [VkDynamicState.Viewport, VkDynamicState.Scissor];
var dynInfo = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = dyn.Length, DynamicStates = dyn.AsPointer() };
var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
VkPipeline pipeline = device.CreateGraphicsPipeline(default, new VkGraphicsPipelineCreateInfo {
    Next = &renderingInfo,
    StageCount = stages.Length, Stages = stages.AsPointer(),
    VertexInputState = vertexInput,                                    // implicit VkPipelineVertexInputStateCreateInfo*
    InputAssemblyState = Presets.InputAssembly.TriangleList,           // PtrOwner<T> -> T*
    ViewportState = &viewportState,
    RasterizationState = Presets.Rasterization.Fill.CullNone,
    MultisampleState = Presets.Multisample.None,
    DepthStencilState = Presets.DepthStencil.NoDepthTest,
    ColorBlendState = Presets.BlendState.BlendColorAlpha,              // straight alpha
    DynamicState = &dynInfo, Layout = layout }, null);
device.DestroyShaderModule(vs, null); device.DestroyShaderModule(fs, null);
```

**Descriptor sets**
```csharp
Span<VkDescriptorSetLayoutBinding> bindings = [new VkDescriptorSetLayoutBinding {
    Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit }];
var setLayoutEx = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings }, null);
Span<VkDescriptorPoolSize> sizes = [new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1024 }];
var poolEx = device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo { MaxSets = 1024,
    Flags = VkDescriptorPoolCreateFlags.FreeDescriptorSetBit, PoolSizes = sizes }, null);
VkDescriptorSet set = device.AllocateDescriptorSet(poolEx, setLayoutEx);   // throws DescriptorPoolOutOfMemoryException when exhausted
var sampler = device.CreateSampler(Presets.Sampler.SamplerLinearClamped, null);
var imgInfo = new VkDescriptorImageInfo { Sampler = sampler, ImageView = texView.VkImageView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1,
    DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &imgInfo }], []);
// bind: cmd.BindDescriptorSet(VkPipelineBindPoint.Graphics, layout, 0, set, []);
```

**VMA buffer and upload via StagingPool**
```csharp
VmaAllocator vma = device.CreateVmaAllocator(new VmaAllocatorCreateInfo {
    PhysicalDevice = pd, Device = device, VulkanApiVersion = (uint)VulkanHelpers.Api.VERSION_1_3 });
// GPU-local vertex buffer via the Abstractions path
BufferEx vb = vma.CreateBuffer(new BufferEx.CreateInfo {
    Name = "verts", BufferSize = ByteSize.Of<MyVertex>(verts.Length),
    BufferUsage = VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.TransferDstBit,
    AllocPreference = MemoryPreference.PreferGpu });
using (var staging = vma.CreateStagingPool(queue, 1)) {
    BufferEx src = staging.AddStagingBuffer<MyVertex>(verts);          // map + copy
    CommandBuffer up = staging.NextCommandBuffer();
    up.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
    up.CopyBuffer(src.VkBuffer, vb.VkBuffer, src.BufferSize);
    up.End();
    staging.Submit().Wait();
}                                                                       // Dispose frees staging buffers
// Host-visible buffer written directly (e.g. per-frame dynamic geometry)
BufferEx ub = vma.CreateBuffer(new BufferEx.CreateInfo { Name = "ubo", BufferSize = ByteSize.Of<Ubo>(),
    BufferUsage = VkBufferUsageFlags.UniformBufferBit,
    AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit });
using (var m = ub.Map()) m.As<Ubo>() = uboData;
// Persistently mapped via raw VMA
vma.CreateBuffer(new VkBufferCreateInfo { Size = (ByteSize64)(1 << 20), Usage = VkBufferUsageFlags.VertexBufferBit },
    new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto,
        Flags = VmaAllocationCreateFlags.MappedBit | VmaAllocationCreateFlags.HostAccessSequentialWriteBit },
    out VkBuffer ring, out VmaAllocation ringAlloc, out VmaAllocationInfo ringInfo);   // returns VkResult; check it
void* ringPtr = ringInfo.PMappedData;
// teardown: vb.Dispose(); ub.Dispose(); vma.DestroyBuffer(ring, ringAlloc); device.DestroyVmaAllocator(vma);
```
For texture uploads, `ImageEx`, a barrier to `TransferDstOptimal`, `cmd.CopyBufferToImage(...)` and a barrier to `ShaderReadOnlyOptimal` follow the same StagingPool pattern. BCn formats (`VkFormat.Bc1RGBAUNormBlock`, `Bc3UNormBlock`, `Bc7UNormBlock`) upload as raw block data. Check support with `pd.FindSupportedFormat([...], VkImageTiling.Optimal, VkFormatFeatureFlags.SampledImageBit)`; BC is commonly unsupported on mobile and some software ICDs (UNVERIFIED for lavapipe).

### 2.7 Unverified or caveats
- Linux loader name issue (see 2.1): not executed here.
- Calling a device or instance extension function that was not enabled hits a null function pointer: inferred from the generated FunctionTable ctor, not tested.
- The `Next = new Ptr(&f13)` pattern was chosen to avoid relying on an implicit `T*` → `void*` → `Ptr` chain. `Next = &x` works directly where the field type is `void*` (all generated structs).
- None of the snippets above were compiled. Names and signatures were copied from source, but expect minor fixes.
## 3. Shader compilation: Brutal.ShaderC and Brutal.Slang

Neither package ships MSBuild targets/tasks: there is **no build-time shader compilation integration**. Both are runtime libraries. For build-time SPIR-V you would write a small console tool (or MSBuild `Exec`) that uses these same APIs, or use `glslc`/`slangc` from the Vulkan SDK. No SPIR-V reflection library (SPIRV-Reflect/SPIRV-Cross) is shipped; **reflection is only available through Slang**. `libSPIRV-Tools-shared` ships with ShaderC (used internally by shaderc for optimisation/validation; no managed binding found).

Both verified at runtime in `/tmp/claude-1000/brutal-smoke-shaders` (section 11).

### 3.1 Brutal.ShaderC (shaderc / glslang; GLSL and HLSL -> SPIR-V)
- Package `Ahwoo.BRUTAL.ShaderC` 2026.10.2, namespace **`Brutal.ShaderCApi`**. Natives: `libshaderc_shared.so{,.1}` + `libSPIRV-Tools-shared.so` (linux-x64), `shaderc_shared.dll` + `SPIRV-Tools-shared.dll` (win-x64), osx-arm64. Loaded by the `ShaderC` static ctor via `NativeLibrary.Load("shaderc_shared", asm, DllImportSearchPath.AssemblyDirectory)`.
- Handles are `readonly struct`s wrapping native pointers, all `IDisposable`: `Compiler`, `CompileOptions`, `CompilationResult`.
```csharp
public static Compiler ShaderC.CreateCompiler();
public static CompileOptions ShaderC.CreateCompilerOptions();
public static void ShaderC.GetSpvVersion(out uint version, out uint revision);
public static bool ShaderC.TryInferShaderKind(ReadOnlySpan<char> path, out ShaderKind kind); // .vert .frag .comp .geom .tesc .tese .mesh .task .rgen .rmiss .rchit .rahit .rint .rcall
public static ShaderKind ShaderC.InferShaderKind(ReadOnlySpan<char> path);

// raw (Compiler instance methods) - return CompilationResult without throwing:
CompilationResult CompileIntoSpv(ReadOnlySpan<byte> sourceText, ShaderKind shaderKind, RefString8 inputFileName, RefString8 entryPointName, CompileOptions additionalOptions);
CompilationResult CompileIntoSpvAssembly(...same...);  CompilationResult CompileIntoPreprocessedText(...same...);
CompilationResult AssembleIntoSpv(ReadOnlySpan<byte> sourceAssembly, CompileOptions additionalOptions);

// throwing helpers (static class CompilerExtensions; dispose result on failure):
CompilationResult Compile(this in Compiler c, string source, ShaderKind kind, string inputFileName, string entryPoint = "main", CompileOptions options = default);
CompilationResult Compile(this in Compiler c, ReadOnlySpan<byte> source, ShaderKind kind, RefString8 inputFileName, RefString8 entryPoint, CompileOptions options);
CompilationResult CompileFile(this in Compiler c, string path, ShaderKind kind, string entryPoint = "main", CompileOptions options = default);
CompilationResult Preprocess(...); CompilationResult Assemble(this in Compiler c, ReadOnlySpan<byte> sourceAssembly, CompileOptions options);
CompilationResult ThrowIfFailed(this in CompilationResult result);

// CompilationResult
ByteSize64 GetLength(); byte* GetBytes(); ByteSize64 GetNumWarnings(); ByteSize64 GetNumErrors();
CompilationStatus GetCompilationStatus(); string GetErrorMessage(); void Dispose();
```
- Exceptions: `ShaderException` (base, message = shaderc error text) and subclasses `ShaderInvalidStageException, ShaderCompilationException, ShaderInternalException, ShaderNullResultObjectException, ShaderInvalidAssemblyException, ShaderValidationException, ShaderTransformationException, ShaderConfigurationException`. Verified: compile error message `bad.vert:2: error: 'x' : undeclared identifier`.
- `CompileOptions` methods: `Clone, AddMacroDefinition(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value), SetSourceLanguage(SourceLanguage /*Glsl,Hlsl*/), SetGenerateDebugInfo, SetEmitNonsemanticShaderDebugInfo, SetOptimizationLevel(OptimizationLevel /*Zero,Size,Performance*/), SetForcedVersionProfile(int, Profile), SetIncludeCallbacks(IncludeResolveFn, IncludeResultReleaseFn, void* userData), SetDefaultIncludeCallbacks() /* resolves #include relative to the requesting file via File IO, depth limit 200 */, SetSuppressWarnings, SetTargetEnv(TargetEnv /*Vulkan,Opengl,OpenglCompat*/, uint version), SetTargetSpirv(SpirvVersion /*_1_0.._1_6*/), SetWarningsAsErrors, SetLimit, SetAutoBindUniforms(Bool8), SetAutoCombinedImageSampler, SetHlslIoMapping, SetHlslOffsets, SetBindingBase(UniformKind, uint), SetBindingBaseForStage, SetPreserveBindings, SetMaxIdBound, SetAutoMapLocations, SetHlslRegisterSetAndBinding[ForStage], SetHlslFunctionality1, SetHlsl16BitTypes, SetVulkanRulesRelaxed, SetInvertY, SetNanClamp`.
- `ShaderKind`: `VertexShader, FragmentShader, ComputeShader, GeometryShader, TessControlShader, TessEvaluationShader, Glsl*/GlslDefault* variants, GlslInferFromSource, SpirvAssembly, RayGen.., TaskShader, MeshShader ...`. `EnvVersion` gives the `uint` for `SetTargetEnv`: `Vulkan_1_0 .. Vulkan_1_4`, `Opengl_4_5`.
- **Gotcha**: `OptimizationLevel` and `SourceLanguage` exist in both `Brutal.ShaderCApi` and `Brutal.SlangApi`; importing both namespaces requires qualification.
```csharp
using Brutal.ShaderCApi;
using var compiler = ShaderC.CreateCompiler();
using var opts = ShaderC.CreateCompilerOptions();
opts.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3);
opts.SetOptimizationLevel(OptimizationLevel.Performance);
opts.SetDefaultIncludeCallbacks();
using var res = compiler.Compile(glslSource, ShaderKind.FragmentShader, "sprite.frag", "main", opts); // throws ShaderException
var spirv = new ReadOnlySpan<byte>(res.GetBytes(), checked((int)res.GetLength().Value)); // copy out before res.Dispose()
```

### 3.2 Brutal.Slang (Slang compiler + reflection)
- Package `Ahwoo.BRUTAL.Slang` 2026.10.1 (~100 MB nupkg), namespace **`Brutal.SlangApi`**. Natives: Slang **2026.7** (`GetBuildTag()` = `"2026.7"`): linux-x64 `libslang.so`, `libslang-compiler.so{,.0.2026.7}`, `libslang-glslang-2026.7.so`, `libslang-glsl-module-2026.7.so`; win-x64 `slang.dll`, `slang-compiler.dll`, `slang-glslang.dll`, `slang-glsl-module.dll`.
- Bindings model: Slang's COM interfaces as **.NET source-generated COM** (`[GeneratedComInterface]`, `StrategyBasedComWrappers`) - `IGlobalSession`, `ISession`, `IModule`, `IEntryPoint`, `IComponentType`, `ISlangBlob` (alias `IBlob`), `ICompileRequest`, `IMetadata`, `ICompileResult`, ...; `[PreserveSig]` methods return `Result` (struct, `IsSuccess`/`IsFailure`, `ThrowIfFailure()` -> `SlangException`). Strings are `byte*` (null-terminated UTF-8) in raw methods; managed `string` overloads exist as extension methods for the common ones. Global type aliases in the binding: `Int = long`, `UInt = ulong`.
- Flat C API in `Slang.PInvoke` (static, public): `slang_*`, the legacy `sp*` compile-request API, and the **reflection API** `spReflection_*`, `spReflectionEntryPoint_*`, `spReflectionTypeLayout_*`, `spReflectionVariableLayout_*`, `spReflectionVariable_*`, `spReflectionType_*`, `spReflectionParameter_GetBindingIndex/GetBindingSpace`, `spReflection_ToJson` etc. Reflection objects are `[InteropHandle]` opaque structs (`ProgramLayout`, `ReflectionParameter` (VariableLayout), `ReflectionEntryPoint`, `TypeReflection`, `TypeLayoutReflection`, ...). Some reflection functions return `string` (marshalled) directly (e.g. `spReflectionVariable_GetName`).
```csharp
public static IGlobalSession Slang.CreateGlobalSession(Int apiVersion = Constants.ApiVersion /*0*/);
public static IGlobalSession Slang.CreateGlobalSession(in GlobalSessionDesc desc);
public static ISlangBlob Slang.CreateBlob(ReadOnlySpan<byte> data);
public static nint Slang.GetComInstance(object comObject);
// IGlobalSession
Result CreateSession(ref SessionDesc desc, out ISession outSession);  ProfileID FindProfile(byte* name);  string? GetBuildTag() /*ext*/;
// ISession (+ ISessionExtensions)
IModule? LoadModule(string moduleName, out ISlangBlob? diag);
IModule? LoadModuleFromSourceString(string moduleName, string path, string source, out ISlangBlob? diag);
IModule? LoadModuleFromSource(string moduleName, string path, ISlangBlob source, out ISlangBlob? diag);
IModule? LoadModuleFromIRBlob(string moduleName, string path, ISlangBlob source, out ISlangBlob? diag);
Result CreateCompositeComponentType(nint* componentTypes, Int count, out IComponentType composite, out ISlangBlob diag);   // raw
IComponentType CreateCompositeComponentType(this ISession, ReadOnlySpan<IComponentType>, out ISlangBlob diag);           // ext - BROKEN, see gotcha
// IModule : IComponentType
Result FindEntryPointByName(byte* name, out IEntryPoint outEntryPoint); Int32 GetDefinedEntryPointCount(); Result GetDefinedEntryPoint(Int32 i, out IEntryPoint ep);
Result Serialize(out ISlangBlob blob); Result WriteToFile(byte* fileName); Int32 GetDependencyFileCount(); byte* GetDependencyFilePath(Int32 i);
// IComponentType
ProgramLayout GetLayout(Int targetIndex, out IBlob diag);
Result Link(out IComponentType linked, out ISlangBlob diag);
Result GetEntryPointCode(Int entryPointIndex, Int targetIndex, out IBlob code, out IBlob diag);
Result GetTargetCode(Int targetIndex, out IBlob code, out IBlob diag);
Result Specialize(...); Result LinkWithOptions(...); Result GetEntryPointMetadata(...); Result GetTargetMetadata(...)
// ISlangBlob
ReadOnlySpan<byte> AsSpan(this ISlangBlob self);
```
- **Gotcha: `StructureSize` is not auto-filled.** `SessionDesc`, `TargetDesc` (and `GlobalSessionDesc`, `uint`) have a `StructureSize` field you must set: `StructureSize = (ByteSize64)sizeof(TargetDesc)` (48) / `sizeof(SessionDesc)` (96).
- **Gotcha / BUG (verified segfault)**: `ISessionExtensions.CreateCompositeComponentType(this ISession, ReadOnlySpan<IComponentType>, out ISlangBlob)` converts each RCW with `ComWrappers.GetOrCreateComInterfaceForObject`, which produces a managed CCW instead of the original native pointer; Slang then crashes (SIGSEGV, exit 139). The same `Slang.GetComInstance` pattern is used by `Slang.TryDisassembleByteCode`, `ICompileRequestExtensions.SetFileSystem/SetWriter/AddTranslationUnitSourceBlob` - treat those as suspect too (unverified). Workaround (verified): unwrap with `ComWrappers.TryGetComInstance` + `Marshal.QueryInterface` to `IComponentType`'s IID and call the raw `ISession.CreateCompositeComponentType(nint*, ...)`. Generated (source-generator-marshalled) interface parameters are fine.
- Full working pipeline (verified on 2026.10.1; produced 932/1088-byte SPIR-V for VS/FS and reflection `gParams b0 s0, gTex b1 s0, gSamp b2 s0`):
```csharp
using System.Runtime.InteropServices; using Brutal.SlangApi;
var global = Slang.CreateGlobalSession();
fixed (byte* prof = "spirv_1_5"u8) {
  var target = new TargetDesc { StructureSize = (Brutal.ByteSize64)sizeof(TargetDesc), Format = CompileTarget.Spirv, Profile = global.FindProfile(prof) };
  var sdesc  = new SessionDesc { StructureSize = (Brutal.ByteSize64)sizeof(SessionDesc), Targets = &target, TargetCount = 1 };
  // optional: sdesc.DefaultMatrixLayoutMode = MatrixLayoutMode.ColumnMajor; SearchPaths/PreprocessorMacros/CompilerOptionEntries
  global.CreateSession(ref sdesc, out var session).ThrowIfFailure();
  var module = session.LoadModuleFromSourceString("sprite", "sprite.slang", src, out var diag)
               ?? throw new Exception(Encoding.UTF8.GetString(diag!.AsSpan()));
  IEntryPoint vs, fs;
  fixed (byte* n = "vsMain"u8) module.FindEntryPointByName(n, out vs).ThrowIfFailure();
  fixed (byte* n = "fsMain"u8) module.FindEntryPointByName(n, out fs).ThrowIfFailure();
  // workaround for the broken span extension:
  var iid = typeof(IComponentType).GUID;
  nint* parts = stackalloc nint[3];
  int k = 0;
  foreach (object o in new object[] { module, vs, fs }) {
      ComWrappers.TryGetComInstance(o, out var unk); Marshal.QueryInterface(unk, in iid, out parts[k++]); Marshal.Release(unk);
  }
  session.CreateCompositeComponentType(parts, 3, out var program, out _).ThrowIfFailure();
  for (int i = 0; i < 3; i++) Marshal.Release(parts[i]);
  program.Link(out var linked, out _).ThrowIfFailure();
  linked.GetEntryPointCode(0, 0, out var vsCode, out _).ThrowIfFailure();   // entry order = composition order
  ReadOnlySpan<byte> vsSpv = vsCode.AsSpan();
  var layout = linked.GetLayout(0, out _);
  uint n = Slang.PInvoke.spReflection_GetParameterCount(layout);
  for (uint i = 0; i < n; i++) {
      var p = Slang.PInvoke.spReflection_GetParameterByIndex(layout, i);
      string name = Slang.PInvoke.spReflectionVariable_GetName(Slang.PInvoke.spReflectionVariableLayout_GetVariable(p));
      var binding = Slang.PInvoke.spReflectionParameter_GetBindingIndex(p); var space = Slang.PInvoke.spReflectionParameter_GetBindingSpace(p);
  }
}
```
- Slang can also emit GLSL/HLSL/etc. (`CompileTarget` has `Glsl`, `Hlsl`, `Spirv`, `SpirvAsm`, ...) and accept GLSL syntax (`SessionDesc.AllowGlslSyntax`). Precompiled modules: `IModule.Serialize` / `ISession.LoadModuleFromIRBlob` for cache-to-disk workflows.
- Recommendation: **ShaderC for porting existing GLSL/HLSL** (simple, robust API, good errors); **Slang when you want reflection-driven descriptor layouts** (with the workaround above). ONI's Unity shaders are HLSL/CG - porting them to GLSL/HLSL for shaderc (`SetSourceLanguage(SourceLanguage.Hlsl)`) or to Slang (HLSL-like syntax) are both plausible.
## 4. Textures (Brutal.Texture, Texture.Abstractions, Stb, Ktx, Gli)

### Packages (nuget.org, verified by download; all `lib/net10.0`)
| Package ID (exact casing) | Version | Natives (`runtimes/<rid>/native`) | Deps |
|---|---|---|---|
| `Ahwoo.BRUTAL.Stb` | 2026.10.2 | linux-x64 `libstb.so`, win-x64 `stb.dll`, osx-arm64 `libstb.dylib` | Core, Numerics, Strings |
| `Ahwoo.BRUTAL.Gli` | 2026.10.1 | linux-x64 `libgli.so`, win-x64 `gli.dll`, osx-arm64 | Core, Numerics |
| `Ahwoo.BRUTAL.Ktx` | 2026.10.1 | linux-x64 `libKtx.so` (libktx **v4.4.0**), win-x64 `Ktx.dll`, osx-arm64 | Core, Numerics |
| `Ahwoo.BRUTAL.Texture` | 2026.10.2 | none (managed) | Core, Gli, Interop, Ktx, Numerics, Stb |
| `Ahwoo.BRUTAL.Texture.Abstractions` | 2026.10.2 | none | Texture |

Namespaces: `Brutal.StbApi`, `Brutal.GliApi`, `Brutal.KtxApi` (+ `.Abstractions`), `Brutal.TextureApi` (+ `.Stb`, `.Gli`, `.Ktx`), `Brutal.TextureApi.Abstractions`.
**No package depends on Vulkan** — getting pixels into a `VkImage` is your job (Texture.Abstractions only computes staging/copy regions).

### Brutal.Stb (raw stb_image / stb_image_write)
`public static partial class Stb` (ns `Brutal.StbApi`), all `[LibraryImport("stb")]`:
```csharp
StbImage8  Load(string fileName, int desiredChannels = 0);                 // stbi_load
StbImage8  LoadFromMemory(ReadOnlySpan<byte> buffer, int desiredChannels = 0);
StbImage16 Load16(string, int = 0);   StbImage16 Load16FromMemory(ReadOnlySpan<byte>, int = 0);
StbImageF  LoadF(string, int = 0);    StbImageF  LoadFFromMemory(ReadOnlySpan<byte>, int = 0);
bool WritePng(string fileName, int w, int h, int channels, ReadOnlySpan<byte> data, int rowStrideInBytes = 0); // + (string, StbImage8)
bool WriteBmp/WriteTga(string, int w, int h, int channels, ReadOnlySpan<byte>);  bool WriteHdr(..., ReadOnlySpan<float>);
bool WriteJpg(string, int w, int h, int channels, ReadOnlySpan<byte>, int quality);
bool TryGetFailureReason(out string? failureReason);
bool IsHdr(string) / IsHdrFromMemory(ReadOnlySpan<byte>) / Is16Bit(string) / Is16BitFromMemory(ReadOnlySpan<byte>);
```
`desiredChannels` must be 0..4 (0 = keep file's; throws `ArgumentOutOfRangeException` otherwise).
`public readonly unsafe struct StbImage8 : IDisposable { byte* Data; int Width, Height, Channels; Span<byte> AsSpan(); Span<T> AsSpan<T>(); void Dispose() /*stbi_image_free*/; bool IsNull(); }` (16/F analogous).
**Not bound:** `stbi_set_flip_vertically_on_load`, `stbi_info*`, write-to-memory callbacks. Output is top-left-first; Unity-origin data (bottom-up UVs) must be flipped by you or in UVs.

### Brutal.Texture (format-agnostic loader → `ITexture`)
```csharp
namespace Brutal.TextureApi;
public interface ITexture {
    Span<byte> Data { get; }                                   // whole payload (native memory)
    Span<byte> ImageData(int level, int layer, int faceOrSlice);
    TexelCount3 Extent { get; }  TextureFormat Format { get; }
    int LayerCount, LevelCount, FaceCount, DimensionCount { get; }  bool IsCubemap, IsArray { get; }
    ByteSize RowPitch(int level);
}
public static partial class TextureLoader {
    enum FormatType { Bmp, Dds, Gif, Hdr, Jpg, Kmg, Ktx, Ktx2, Pgm, Pic, Png, Ppm, Psd, Tga }
    static ITexture LoadFromFile(string filePath, params TextureLoadSettings?[] settings);
    static ITexture LoadFromMemory(ReadOnlySpan<byte> data, FormatType formatType, params TextureLoadSettings?[] settings);
    static void Unload(ITexture texture);              // MUST call (native memory); or `using var t = tex.Using();`
    static bool IsSupported(string pathOrExtension); static bool TryGetFormatType(string, out FormatType); static FormatType GetFormatType(string);
    static LoadDiagnostics Diagnostics { get; }        // EnableLoadTracing(bool), GetCurrentTracedLoads(int frames=5)
    static ulong TotalLoadedCount, CurrentLoadedCount { get; }
}
public static class TextureWriter { static void SaveToFile(ITexture texture, string filePath); } // ktx/ktx2, png/jpg/jpeg/hdr/tga/bmp, dds/kmg
public record struct TexelCount3(int X, int Y, int Z);
public ref struct TextureDescriptor { required ReadOnlySpan<byte> Data; required TextureFormat Format; required TexelCount3 Extent; required int LayerCount, MipmapCount, FaceCount; required bool IsCubemap; }
// extension props (DerivedTexturePropertiesExtensions): Is1d/Is2d/Is3d, Width, Height, Depth, Extent(int level)
```
Dispatch by extension (case-insensitive), in this order: **Ktx** (`.ktx`, `.ktx2`) → **Stb** (`.png .jpg .jpeg .tga .bmp .hdr .psd .gif .pic .ppm .pgm`) → **Gli** (`.dds .ktx .kmg`; `.ktx` never reaches Gli).

`TextureFormat` is an enum whose **numeric values equal `VkFormat`** (e.g. `R8G8B8A8_UNorm = 37`, `BC1Rgb_UNorm_Block = 131`, `BC1Rgba_UNorm_Block = 133`, `BC2_UNorm_Block = 135`, `BC3_UNorm_Block = 137`, `BC3_SRGB_Block = 138`, `BC4_UNorm_Block = 139`, `BC5_UNorm_Block = 141`, `BC6HUFloat_Block = 143`, `BC7_UNorm_Block = 145`, `BC7_SRGB_Block = 146`) → cast `(VkFormat)(int)tex.Format`.

Per-loader settings (pass via `params TextureLoadSettings?[]`):
- `Brutal.TextureApi.Stb.Loader.LoadSettings { StbApi.Stb.DataType? ForceDataType; int? ForceChannels; static readonly LoadSettings ForceRgba8; }`. Stb textures: 1 level/layer/face; format = `R8_UNorm/R8G8_UNorm/R8G8B8_UNorm/R8G8B8A8_UNorm` (16-bit: `R16..._UNorm`; float analogous). **Never `_SRGB`** — choose sRGB view format yourself. **3-channel PNG/JPG → `R8G8B8_UNorm`**, which most GPUs don't support as an optimal-tiling sampled image: use `LoadSettings.ForceRgba8`.
- `Brutal.TextureApi.Ktx.Loader.LoadSettings { static Default; KtxTranscodeFmt SuperCompressionTranscodeFormat = KtxTranscodeFmt.Bc7Rgba; bool LazyLoad = false; }` — Basis-supercompressed KTX2 is transcoded on load (CPU, `TranscodeBasis(fmt, KtxTranscodeFlagBits.HighQuality)`).
- Gli: no settings.

### BC1/BC3/BC7 (DXT) handling — facts
- DDS via Gli: DXT1→`BC1*`, DXT3→`BC2*`, DXT5→`BC3*`, ATI1/2→`BC4/BC5` map fine. **BC6H/BC7 (`RGB_BP_*`, `RGBA_BP_*`) → `GliTexture.Format` throws `NotImplementedException`** (`Brutal.Texture/Gli/GliFormatExtensions.cs`). Workaround: use raw `new GliApi.Gli.Texture(ReadOnlySpan<byte>)` and map `Gli.Format` yourself.
- **No CPU BC decompressor anywhere** in BRUTAL (no bcdec/squish). The only CPU transcoding is libktx Basis Universal → `KtxTranscodeFmt { Etc1Rgb, Etc2Rgba, Bc1Rgb, Bc3Rgba, Bc4R, Bc5Rg, Bc7Rgba, Pvrtc14Rgb/Rgba, Astc4X4Rgba, Pvrtc24Rgb/Rgba, Etc2EacR11, Etc2EacRg11, Rgba32, Rgb565, Bgr565, Rgba4444, ... }` — applies only to Basis/UASTC-encoded KTX2 input. libktx also exposes encoders: `KtxTexture2Handle.CompressBasis(uint quality)`, `CompressBasisEx`, `CompressAstc(Ex)`, `DeflateZstd/DeflateZlib`.
- Raw BC blocks you already have in memory (e.g. Unity Texture2D payloads, which are DXT1/DXT5/BC7 raw mip chains): skip all loaders; build a `GenericTexture` (below) or compute regions yourself and upload with `VK_FORMAT_BC*`. Requires `VkPhysicalDeviceFeatures.textureCompressionBC` (lavapipe reports it — unverified here).

### Brutal.Gli (raw)
`public sealed unsafe class GliApi.Gli.Texture : INullable, IDisposable` — ctors `()`, `(string filePath)`, `(ReadOnlySpan<byte> data)`, create/subset/`(Texture, Target, Format, Swizzles)`; members `byte* Data; long Size; byte* ImageData(int level, int layer = 0, int face = 0); long ImageSize(int level); int3 Extent; int3 ImageExtent(int level); long ImageRowPitch(int level); long BlockSize; bool IsCompressed; int3 BlockExtent; Format Format; long Layers, Levels, Faces; Target Target; bool Save(string filePath)`.

### Brutal.Ktx (raw libktx 4.4.0)
Handles `KtxTextureHandle`, `KtxTexture1Handle`, `KtxTexture2Handle`; static classes `KtxTexture`, `KtxTexture2` with `CreateFromNamedFile(...)`, `CreateFromMemory(...)`, `Create(...)`, extension methods `GetData, GetDataSize, GetRowPitch(level), GetElementSize, GetImageOffset, GetImageSize, GetLevelSize, IterateLevels, IterateLevelFaces, IterateLoadLevelFaces, NeedsTranscoding, TranscodeBasis, LoadImageData(Span<byte>), SetImageFromMemory, GetVkFormat, Destroy, WriteToNamedFile`. Errors are `KtxErrorCode` return values (no exceptions). `GpuUploadUtils.IsRowSize4BytePadded(handle, level)`.

### Texture.Abstractions — staging helpers (CPU only)
```csharp
namespace Brutal.TextureApi.Abstractions;
public class UploadRegions {
    enum Mode { BatchUploadRegions, UploadRegionPerImage }
    static StageUploadRegions Build(ITexture texture);
    static StageUploadRegions Build(ITexture texture, int firstLevel, int maximumLevelCount, Mode mode = Mode.BatchUploadRegions);
}
public class StageUploadRegions { List<StagingRegion> StageBufferRegions; List<UploadRegion> CopyToImageRegions; ByteSize RequiredBufferSizeBytes; }
public class StagingRegion { ByteSize SourceStartOffset, DestinationOffset, SizeInBytes; int RowWidthInBlocks; ByteSize BlockSizeBytes; required ByteSize RowPitch; } // texture.Data → staging
public class UploadRegion { ByteSize BufferOffset; int BaseLayer, LayerCount, BaseLevel; }  // → VkBufferImageCopy (extent: tex.Extent(level))
public static class TextureFormatExtensions { FormatDescriptor Descriptor(this TextureFormat) } // IsBlockCompressed, BlockSizeInBytes, BlockExtent, ...
public class TextureMemoryLayout(ITexture) { TextureImageMemoryLayout ImageLayout(int level); } // RowPitch, RowPadBytes, Extent.Blocks/Texels
public class GenericTexture : ITexture { struct CreateInfo {Format, Extent, LayerCount, LevelCount, FaceCount, IsCubemap, IsArray, DimensionCount}; struct CreateImageRange {StartByteOffset, ByteLength, RowPitch}; GenericTexture(ITexture); static class Defaults { Uncompressed(...), RGB8UNorm(int2), RGBA8UNorm(int2) } }
```
Strips KTX1 row padding, aligns staging offsets to lcm(4, blockSize) (satisfies `bufferOffset` rules for BC). Usage:
```csharp
var tex = TextureLoader.LoadFromFile(path, Brutal.TextureApi.Stb.Loader.LoadSettings.ForceRgba8);
using var _ = tex.Using();                                    // Unload on scope exit
var r = UploadRegions.Build(tex);
// map staging buffer of r.RequiredBufferSizeBytes; for each StagingRegion copy SizeInBytes bytes
// (row-wise if RowPitch != RowWidthInBlocks*BlockSizeBytes) from tex.Data[SourceStartOffset..] to DestinationOffset;
// for each UploadRegion emit VkBufferImageCopy{bufferOffset=BufferOffset, mip=BaseLevel, baseArrayLayer=BaseLayer, layerCount=LayerCount, imageExtent=tex.Extent(BaseLevel)}
```
(Region semantics read from source comments; not executed here.)

## 5. Fonts

### Brutal.FreeType — **native-only, no C# API**
- Source folder `cache/BRUTAL/Brutal.FreeType` contains only `LICENSES/`. The nupkg's `Brutal.FreeType.dll` (5 KB) has **no types** (assembly attributes only; description "Native FreeType and libpng binaries for use with Brutal packages"). Package README: "native-only package — ships native binaries, not a C# FreeType API… referencing it just deploys the native FreeType dlls … where a consuming Brutal package can load them."
- `Ahwoo.BRUTAL.FreeType` 2026.10.1: FreeType **2.14.3** + libpng18. linux-x64: `libfreetype.so(.6, .6.20.6)`, `libpng18.so(.18)`; win-x64: `freetype.dll`, `png18.dll`; osx-arm64. Also `build/natives/include` (headers), `win-x64/*.lib`, and `buildTransitive/Ahwoo.BRUTAL.FreeType.targets` (adds `-DFREETYPE_NUGET_DIR=…` to `$(CMakeBuildArgs)` — harmless for consumers). Dep: `Ahwoo.BRUTAL.ZLib` (linux `libfreetype.so` NEEDs `libzlib.so.1`, `libpng18.so.18`; RUNPATH `$ORIGIN`).
- **SDF:** the shipped libfreetype has the `sdf` and `bsdf` renderer modules compiled in (`ft_sdf_renderer_class`, `ft_bitmap_sdf_renderer_class` present in binary) → `FT_Render_Glyph(slot, FT_RENDER_MODE_SDF)` works natively. You must write your own `[LibraryImport("freetype")]` P/Invokes (FT_Init_FreeType, FT_New_Memory_Face, FT_Set_Pixel_Sizes, FT_Load_Char, FT_Render_Glyph, FT_Get_Kerning…) to use it. (Not executed.)
- Only consumer in BRUTAL: `Ahwoo.BRUTAL.ImGui.FreeType` 2026.10.2 (native `libimgui_freetype.so` NEEDs `libfreetype.so.6`, `libimgui.so`). API (ns `Brutal.ImGuiApi.FreeType`): `ImGuiFreeType.GetFontLoader() : ImFontLoaderPtr`, `SetAllocatorFunctions(...)`, `DebugEditFontLoaderFlags(ref ImGuiFreeTypeLoaderFlags)`; use `ImGui.GetIO().Fonts.SetFontLoader(ImGuiFreeType.GetFontLoader())`.

### Brutal.Sdl.Ttf (SDL3_ttf 3.2.2)
- `Ahwoo.BRUTAL.Sdl.Ttf` 2026.10.2; natives linux-x64 `libSDL3_ttf.so(.0, .0.2.2)`, win-x64 `SDL3_ttf.dll`, osx-arm64. FreeType + HarfBuzz are **statically linked** into it (linux .so NEEDs only `libSDL3.so.0`, libm, libgcc_s, libc). Deps: `Ahwoo.BRUTAL.Sdl` (provides SDL3 native — required even if you window with GLFW), Core, Interop, Logging.
- Ns `Brutal.SdlApi.Ttf`; `public static unsafe partial class SdlTtf`, DLL name `"SDL3_ttf"` with custom resolver (loads SDL3 first). Ergonomic wrappers throw `SdlException` on null/false where noted. Key signatures:
```csharp
void Init();  void Quit();  int WasInit();  int Version();
void GetFreeTypeVersion(out int major, out int minor, out int patch); void GetHarfBuzzVersion(out int, out int, out int);
TtfFont OpenFont(string file, float ptsize);  TtfFont OpenFontIO(nint src, bool closeio, float ptsize);  void CloseFont(TtfFont); // TtfFont : IDisposable
bool SetFontSize(TtfFont, float ptsize); bool SetFontSizeDPI(TtfFont, float ptsize, int hdpi, int vdpi);
void SetFontHinting(TtfFont, TtfHintingFlags);  bool SetFontSDF(TtfFont font, bool enabled); bool GetFontSDF(TtfFont);   // SDF supported
bool FontHasGlyph(TtfFont, uint ch);
SdlSurface* GetGlyphImage(TtfFont font, uint ch, out TtfImageType image_type);  SdlSurface* GetGlyphImageForIndex(TtfFont, uint glyph_index, out TtfImageType);
bool GetGlyphMetrics(TtfFont font, uint ch, out int minx, out int maxx, out int miny, out int maxy, out int advance);
bool GetGlyphKerning(TtfFont font, uint previous_ch, uint ch, out int kerning);
bool GetStringSize(TtfFont, string text, nuint length, int* w, int* h);  GetStringSizeWrapped(...); MeasureString(...);
SdlSurface* RenderText{Solid,Shaded,Blended,LCD}(TtfFont, string text, nuint length, SdlColor fg[, SdlColor bg]); // + *Wrapped, RenderGlyph*(font, uint ch, ...)
TtfTextEngine* CreateRendererTextEngine(SdlRenderer) ... // SDL_Renderer/GPU text engines (not useful with our Vulkan)
```
~119 P/Invokes total; raw ones under `SdlTtf.PInvoke`. Whether TTF works without `SDL_Init` is unverified (SDL_ttf normally doesn't need video init).
- Verdict for a TextMeshPro replacement: SDL3_ttf gives glyph bitmaps/SDF + metrics + kerning + HarfBuzz shaping out of the box; raw FreeType needs hand-written P/Invokes but avoids SDL3.

## 6. ImGui (Brutal.ImGui, .Backends, .Abstractions, .Extensions, .FreeType)

- `Ahwoo.BRUTAL.ImGui` 2026.10.2: Dear ImGui **1.92.2 docking branch** (`IMGUI_VERSION = "1.92.2"`, `IMGUI_VERSION_NUM = 19220`; cimgui-style flat native `libimgui.so` / `imgui.dll` / `libimgui.dylib` for linux-x64, win-x64, osx-arm64). Deps: CommunityToolkit.HighPerformance 8.4.2, Core, Localization, Memory, Numerics, Strings. Ns `Brutal.ImGuiApi`.
- `Ahwoo.BRUTAL.ImGui.Backends` 2026.10.2: **managed only** (C# ports of imgui_impl_*). Deps: ImGui, CommunityToolkit.HighPerformance, **Glfw, OpenGl, Vulkan.Abstractions** (all pulled transitively). Ns `Brutal.ImGuiApi.Backends`.
  - Platform: `GlfwBackend : IPlatformBackend` only (**no SDL backend**). `public enum ClientApi { Unknown, OpenGL, Vulkan }`; `GlfwBackend(GlfwWindow window, ClientApi clientApi)`; `void NewFrame()`; `Dispose()`. Hooks input by **`+=` on `GlfwWindow` events** (`OnMouseButton, OnScroll, OnCursorPos, OnCursorEnter, OnKey, OnChar, OnFocus`) — multicast, so your own handlers coexist; check `io.WantCaptureMouse/WantCaptureKeyboard` yourself. Sets clipboard fns, mouse cursors, gamepad, multi-viewport (`PlatformHasViewports`), and `Platform_CreateVkSurface` (via `Glfw.CreateWindowSurface`). On Windows it subclasses the WndProc.
  - Renderers: `VulkanBackend : IRendererBackend`, `OpenGlBackend : IRendererBackend` (`OpenGlBackend()`, `RenderDrawData(ImDrawDataPtr)`).
```csharp
public partial class VulkanBackend : IRendererBackend {
    public struct CreateInfo {
        public required DeviceEx Device;            // Brutal.VulkanApi.Abstractions.DeviceEx
        public required Queue GraphicsQueue;        // Brutal.VulkanApi.Queue
        public required VkRenderPass RenderPass;    // REQUIRED: no dynamic-rendering path in this port
        public int SubPass;
        public required int MinImageCount;          // >= 2
        public required int ImageCount;             // >= MinImageCount (#frames of vertex buffers)
        public required VkSampleCountFlags SampleCount;
        public required int DescriptorPoolSize;     // > 0; backend creates its own pool (min 8 per atlas)
        public ByteSize MinAllocationSize;
    }
    public VulkanBackend(in CreateInfo info);
    public void NewFrame();                                                   // no-op
    public void RenderDrawData(ImDrawDataPtr drawData, CommandBuffer commandBuffer); // record inside your active render pass
    public ImTextureRef AddTexture(VkSampler sampler, VkImageView imageView, VkImageLayout imageLayout);
    public void RemoveTexture(ImTextureRef texture);
}
```
  SPIR-V shaders are embedded (no ShaderC needed); allocates with raw `vkAllocateMemory` (not VMA); handles 1.92 dynamic font-atlas texture updates (`RendererHasTextures`) inside `RenderDrawData` (uses its own command pool/buffer + queue submit for uploads). Asserts throw `Exception`.
- Frame loop (construct platform backend **before** renderer — renderer asserts `Platform_CreateVkSurface` set when viewports enabled):
```csharp
using Brutal.ImGuiApi; using Brutal.ImGuiApi.Backends;
ImGui.CreateContext();                               // ImGuiContextPtr CreateContext(ImFontAtlasPtr sharedFontAtlas = default)
var io = ImGui.GetIO();  io.ConfigFlags |= ImGuiConfigFlags.DockingEnable; // ViewportsEnable optional
var plat = new GlfwBackend(window, GlfwBackend.ClientApi.Vulkan);
var rend = new VulkanBackend(new VulkanBackend.CreateInfo { Device = dev, GraphicsQueue = q, RenderPass = rp,
    MinImageCount = 2, ImageCount = swapImages, SampleCount = VkSampleCountFlags._1Bit, DescriptorPoolSize = 64 });
// per frame:
ImString.SharedStorage.Reset();   // !! arena for string args; nobody else resets it (8 MB, throws when full)
plat.NewFrame(); rend.NewFrame(); ImGui.NewFrame();
if (ImGui.Begin("Debug")) { ImGui.Text($"fps {fps}"); } ImGui.End();
ImGui.Render();
rend.RenderDrawData(ImGui.GetDrawData(), cmd);       // inside render pass `rp`
if (io.ConfigFlags.HasFlag(ImGuiConfigFlags.ViewportsEnable)) { ImGui.UpdatePlatformWindows(); ImGui.RenderPlatformWindowsDefault(); }
// shutdown: rend.Dispose(); plat.Dispose(); ImGui.DestroyContext();
```
- Strings: `ImString` (`ref struct`) has implicit conversions from `string`, `ReadOnlySpan<char|byte>`, `"..."u8`, `String8`, `Ptr<byte>`, and supports `$"..."` interpolation without managed allocation — written into the static `ImString.SharedStorage` (`ArenaStringBuilder`, 8 MB). Not thread-safe; reset once per frame.
- Fonts: `ImFontAtlasPtr` extensions `AddFontDefault(ImFontConfigPtr = default)`, `AddFontFromFileTTF(...)`, `AddFontFromMemoryTTF(...)` (takes ownership of data by default), `SetFontLoader(ImFontLoaderPtr)`.
- `Brutal.ImGui.Extensions` (ns `Brutal.ImGuiApi.Extensions`): `ImGuiEx` generic helpers — `CheckboxFlags<T>`, `Combo<T>` (enum), `RadioButton<T>`, `ColorEdit3/ColorPicker3(ref float4)`, `DragScalar<T>/SliderScalar<T>/InputScalar<T>(N)`.
- `Brutal.ImGui.Abstractions` (ns `Brutal.ImGuiApi.Abstractions`): `ConsoleWindow : ILoggerProvider` (in-game log console + `TerminalInterface`/`[TerminalCallback]` command parser), `ConsoleWindowLogger : ILogger`, `ImGuiUtils`.
- Also exists: `Brutal.ImPlot`, `Brutal.RenderDoc.ImGui` (not inspected).

## 7. FMOD (Brutal.Fmod)

- `Ahwoo.BRUTAL.Fmod` 2026.10.3: **managed bindings only — ships NO native FMOD libraries** (no `runtimes/`). Deps: Core, Interop, Numerics, Strings. LICENSES note: generated from FMOD headers; FMOD EULA applies.
- Bound header version: `Brutal.FmodApi.Fmod.VERSION = 0x00020308` → **FMOD Engine 2.03.08**.
- Namespaces/classes: `Brutal.FmodApi` (core: `Fmod`, `Result`, `ResultExtensions.Check(this Result)` → throws `FmodResultException`) and `Brutal.FmodApi.Studio` (`FmodStudio`, `FmodStudioSystem`, e.g. `FmodStudioSystem FmodStudio.SystemCreate(uint headerVersion)` / `Result TrySystemCreate(out FmodStudioSystem, uint)`).
- Native names: `[LibraryImport("fmod")]` (384 imports) and `[LibraryImport("fmodstudio")]` (192). `FmodStudio`'s static ctor does `NativeLibrary.Load("fmod", asm, DllImportSearchPath.AssemblyDirectory)` — **throws if no `fmod`/`libfmod.so` next to the assembly**, i.e. Studio API is unusable with a studio-only distribution.
- ONI (Windows install at `C:\Program Files (x86)\Steam\steamapps\common\OxygenNotIncluded`, checked via /mnt/c): `OxygenNotIncluded_Data/Plugins/x86_64/fmodstudio.dll` only, FileVersion **2.3.12 (build 160365)** = FMOD 2.03.12; managed wrapper in `FMODUnity.dll` (+ `FMODUnityHaptics/Resonance`). Linux build (`/home/nigit/projects/oni-linux/OxygenNotIncluded_Data/Plugins/libfmodstudio.so`): the only FMOD binary; it **exports the core `FMOD_System_*` symbols itself** (94 `FMOD_System_*` exports, no NEEDED libfmod) — i.e. Unity-style single library. Version string not found via `strings` (unverified, presumably 2.03.12 like Windows).
- Conflict assessment: **no C# conflict** (game uses `FMOD`/`FMOD.Studio` namespaces; BRUTAL uses `Brutal.FmodApi*`). Same 2.03 minor series, so ABI-compatible in principle (2.03.08 headers vs 2.03.12 runtime — FMOD only rejects product/major mismatches; inference, unverified). Practical recommendation: don't reference Brutal.Fmod at all — keep the game's own FMOD wrapper + its native lib; Brutal.Fmod brings nothing (no natives) and its `"fmod"` core-library expectation doesn't match the Unity-style single `fmodstudio` binary (Unity's wrapper routes core calls through `fmodstudio`).
## 8. Numerics, Core & utilities

Source: `cache/BRUTAL/Brutal.{Numerics,Maths,Core,Interop,Memory,Collections,Strings,Concurrency,Logging}`. All packages are `net10.0`, version `2026.10.1` (Logging `2026.10.2`). NuGet IDs are spelled `Ahwoo.BRUTAL.<Name>` (README writes `Ahwoo.Brutal.*`; NuGet is case-insensitive, both resolve).

### 8.1 Brutal.Numerics (`namespace Brutal.Numerics`)

Generated (T4) HLSL-style value types, almost all docs are `<inheritdoc cref="System.Numerics.Vector4/Matrix4x4...">`, i.e. **semantics deliberately mirror System.Numerics**.

- Vectors: `{bool,byte,sbyte,short,ushort,int,uint,long,ulong,float,double}{2,3,4}` — e.g. `float2`, `float3`, `float4`, `int2`, `uint4`, `byte4`.
  - Plain sequential public fields `X, Y, Z, W` (no `StructLayout` attribute needed: sequential by default) → `float4` = 16 bytes, `float3` = 12 bytes (no padding!), `int2` = 8 bytes. Safe to blit into vertex/uniform buffers (watch std140 `vec3` alignment yourself).
  - Ctors: `new float4(float x, float y, float z, float w)`, `new float4(float xyzw)`, `new float4(float3, float w)`, etc.
  - Statics: `Dot`, `Cross` (float3), `Normalize`, `NormalizeOrZero`, `Length()`/`LengthSquared()` (instance), `Distance[Squared]`, `Lerp(a,b,float)`, `Clamp`, `Min`, `Max`, `Abs`, `Reflect`, plus the full generic-math surface (`Sin`, `Cos`, `SinCos`, `Floor`, `Round`, `Sqrt`, `Pow`, `DegreesToRadians`, ...) component-wise. Constants `UnitX..UnitW`, `Zero`, `One`.
  - Swizzles as get/set properties: `v.XY`, `v.XZ`, `v.YX`, ... (`float4.swizzle.gen.cs`, ~1800 lines).
  - Color helpers on float3/float4/byte3/byte4: `float4.Rgb(r,g,b)`, `float4.Rgba(r,g,b,a)`, `float4.Grayscale(v[,a])`, `byte3.HexColor(0xRRGGBB)`. Static class `Color` with `Color.Preset` constants (`Color.Black, White, Gray, Clear, Red, Green, Blue, Cyan, Magenta, Yellow`, ...) that implicitly convert to `byte3/byte4/ushort3/ushort4/float3/float4`.
  - Packing/conversion: `float4.Unpack(in byte4 v, Unpack.Float mode = Unpack.Float.Cast)` (modes `Cast`, `Normalize` → e.g. 0..255 → 0..1), `float4.Pack(in double4, Pack.Float mode = Truncate)` (modes `Truncate, Clamp, Normalize, Round`), `UnpackSpan(...)` span overloads, and `Func<>` element-converter overloads.
  - Span access: `CopyTo(Span<float>)`, `TryCopyTo`, `AsSpan()` on matrices/rects.
- Matrices: `float4x4`, `double4x4`, `float3x2`, `double3x2` (only these four; other shapes are "Unsupported"). Quaternions `floatQuat`/`doubleQuat` (fields `X,Y,Z,W`; W = scalar). Planes `floatPlane`/`doublePlane`. Rects `floatRect`/`doubleRect` (`public float2 Min; public float2 Max;` + `Center`, `Extent`).
- **`float4x4` layout & conventions (verified from source):**
  ```csharp
  public partial struct float4x4 { public float4 X; public float4 Y; public float4 Z; public float4 W; } // rows
  // M11..M44 properties alias X.X .. W.W ; this[int row,int column]; Translation (float3, = W.XYZ)
  ```
  - **Row-major storage, row-vector convention — identical to `System.Numerics.Matrix4x4`.** Fields are rows; translation lives in row `W` (M41..M43).
  - `left * right` computes rows of `left` transformed by `right` ⇒ `world * view * proj` applies world first (same as System.Numerics).
  - `float4.Transform(float4 v, float4x4 m)` == `v * m` (row vector, the normal way). `float3.Transform(float3 pos, float4x4 m)`, `float3.TransformNormal(...)`.
  - **Gotcha:** there is also `float4x4 * float4` (`Multiply(float4x4, float4)` = `Dot(row_i, v)`, i.e. `M·v` column-vector style) and `float4x4.Transform(float4x4, float4)` which aliases that, NOT `float4.Transform(v, m)`. `m * v` ≠ `v * m`. Pick one convention (use `v * m` / `float4.Transform(v, m)` to match System.Numerics semantics).
  - Projection/view factories (all System.Numerics names, RH default + `...LeftHanded` variants): `CreatePerspectiveFieldOfView(float fov, float aspect, float near, float far)`, `CreatePerspective`, `CreatePerspectiveOffCenter`, `CreateOrthographic(float w, float h, float zn, float zf)`, `CreateOrthographicOffCenter(left,right,bottom,top,zn,zf)`, `CreateLookAt(float3 eye, float3 target, float3 up)`, `CreateLookTo`, `CreateWorld`, `CreateBillboard`, `CreateViewport`, `CreateTranslation`, `CreateScale`, `CreateRotationX/Y/Z`, `CreateFromQuaternion`, `CreateFromAxisAngle`, `CreateFromYawPitchRoll`, `Invert(m, out r)`, `Transpose`, `Decompose`, `Lerp`, `Identity`, `IsIdentity`, `GetDeterminant()`.
  - Projection depth range is **[0,1]** (D3D/Vulkan style: `Z row = (0,0,far/(near-far),-1)`, `W row = (0,0,near*far/(near-far),0)`), **no Y flip** — for Vulkan clip space (+Y down) flip Y yourself (negate `M22` or use negative viewport height). Far = +∞ supported.
  - Uploading to GLSL/Slang: memory is row-major `float[16]`; a GLSL `mat4` (column-major default) will read it transposed ⇒ either write `v * M` in the shader, declare `layout(row_major)`, or `float4x4.Transpose` on CPU.
  - Ctor `float4x4(ReadOnlySpan<float>)` reads 16 floats row by row; `float4x4(float3x2)` widens a 2D affine.
- `float3x2` = 2D affine (rows `X`,`Y`, translation `Z`) mirroring `Matrix3x2`; `float2 * float3x2`.
- **Interop with System.Numerics:** no implicit/explicit conversion operators exist. Layouts are identical, so use `Unsafe.BitCast<float4x4, Matrix4x4>(m)` / `Unsafe.As<...>` (unverified-at-runtime but layout-equal by construction). Brutal.Numerics ships analyzer BRUTALCORE0005 (Info) nudging you away from System.Numerics.
- `Brutal.Maths` (`namespace Brutal.Maths`): only `RandomExtensions` — `random.NextSingle(float max)`, `NextSingle(min,max)`, `NextDouble(...)`, `NextSinglePointInSquare(float xySize = 1)`, `NextSinglePointInRectangle(xSize, ySize)`, etc.

### 8.2 Brutal.Core (`namespace Brutal`, `Brutal.Pointers`, `Brutal.Pointers.Extensions`, `Brutal.Attributes`, `Brutal.Enums`)

- `Brutal.Platform`: `static Family CurrentFamily` (`enum Family { Windows, Linux, MacOS }`), `IsWindows`, `IsLinux`, `IsMacOS`, `IsUnix`. Static ctor throws `PlatformNotSupportedException` on anything else.
- Pointer wrappers (all `readonly unsafe struct`, implement `INullable { bool IsNull(); }`):
  - `Ptr(void*)`: `.Pointer`, `Offset(ByteSize)`, `AsSpan<T>(ElementCount)`, `Cast<T>()`; implicit `void*`⇄`Ptr`; explicit `nint`⇄`Ptr`.
  - `Ptr<T>(T*)` (`T : unmanaged` on Cast): `.Pointer`, `ref T Value`, `this[int]` (ref), `Offset(ElementCount)`, `AsSpan(ElementCount)`, `AsPtr()`, `Cast<U>()`; implicit `T*`⇄`Ptr<T>`, `Ptr<T>`→`Ptr`/`void*`; **implicit `Span<T>`/`ReadOnlySpan<T>` → `Ptr<T>` marked `[Fixed]` (analyzer-enforced, see §10)**; explicit `nint`.
  - `PtrPtr(void**)`, `PtrPtr<T>(T**)`: `Value`, `this[int]` → `Ptr`/`Ptr<T>`, `AsSpan(...)`. Used for `const char* const*` style args.
  - Extensions (`using Brutal.Pointers.Extensions;`): `span.AsPointer()` → `T*` (no [Fixed] check — escape hatch), `Span<T>.AsPtr()` (no check), `ReadOnlySpan<T>.AsPtr()` (`[Fixed] this` — checked).
- `[Fixed]` attribute (`Brutal.Attributes.FixedAttribute`, targets ReturnValue/Parameter/Field/Property): marks "this memory must not move". `[return: Fixed]` on a method makes its result a valid fixed source.
- Size types: `ByteSize(nuint Value)` (record struct; `ByteSize.Of<T>()`, `Of<T>(ElementCount)`, `Of<T>(ReadOnlySpan<T>)`, `OffsetOf<T>(string field)`, `AlignmentOf<T>()`, `FromKilobytes/Megabytes/Gigabytes`, `AlignTo(...)` extension; explicit from int/uint/ulong/…, implicit to `ulong`; `byte* + ByteSize`), `ByteSize32`, `ByteSize64`; `ElementCount(int)` (implicit from/to `int`). Most BRUTAL APIs take these; `int` literals convert implicitly to `ElementCount` but **need an explicit cast to `ByteSize`** (`(ByteSize)256`).
- `Bool32` / `Bool8`: 4-/1-byte blittable bools with implicit `bool` conversion (Vulkan `VkBool32` style).
- Enum helpers (C# 14 extension members on `T : Enum`): `flags.Add(x)`, `.Remove(x)`, `.Set(x, bool)`, `.ContainsAll(x)`, `.ContainsAny(x)`, `.ToMask()`; `EnumMask<T>` (bitset indexed by enum *value*: `mask[flag]`, `|&^~`, `Add/Remove/Contains`), `BitMask<T>` (`FromBit(byte)`, `FromMask(T)`).
- `ISpanLike` / `ISpanLike<T>`: `Ptr`, `Length`, `Stride`, `IsEmpty`, `AsSpan<T>()` — common interface of native lists/spans.
- `SpanExtensions.Contains`, `TryCopyTo(..., out int written)`; `Utf8Ex.TryFormat<T>(...)`.

### 8.3 Brutal.Interop (`namespace Brutal.Interop`)

Tiny (5 files). Building blocks used by the generated bindings:
- `IInteropHandle<THandle, TSelf> : INullable { THandle Pointer { get; init; } }` + `[InteropHandle]` attribute + `InteropHandleMarshaller<T, THandle>` (`ConvertToUnmanaged(T) => managed.Pointer`, `ConvertToManaged` = bit-reinterpret). Handle structs are therefore zero-cost single-field wrappers passable directly in `[LibraryImport]` signatures.
- `InteropStringMarshaller` (for `string` params with `StringMarshallingCustomType`): UTF-8, uses a 256-byte (`BufferSize => 0x100`) caller-stack buffer, falls back to `NativeMemory.Alloc` for long strings, null-terminates; returned `byte*` → `string` (`null` → `string.Empty`). `InteropNullableStringMarshaller` returns `null` for NULL. Glfw has an identical private copy `Brutal.GlfwApi.CustomStringMarshaller`.
- So: **a C# `string` passed to a binding is transcoded to UTF-8 per call** (stack buffer if ≤ ~85 chars). For zero-copy, APIs that take `RefString8` (Slang, ShaderC, ImGui) accept `"text"u8` / `byte[]` / `String8` (see 8.6).

### 8.4 Brutal.Memory (`namespace Brutal.Memory`)

There is **no allocator framework** — just thin wrappers over `System.Runtime.InteropServices.NativeMemory` (malloc/free):
```csharp
public static class Mem {
  Ptr Alloc(ByteSize); Ptr Alloc(ElementCount, ByteSize stride); Ptr AllocZeroed(ByteSize); Ptr AllocZeroed(ElementCount, ByteSize);
  Ptr Realloc(Ptr, ByteSize); void Free(Ptr); void Free<T>(Span<T>);
  void Copy(Ptr src, Ptr dst, ByteSize); void Fill(Ptr, ByteSize, byte); void Clear(Ptr, ByteSize);
  Ptr<T> Alloc<T>(); Ptr<T> AllocZeroed<T>(); Ptr<T> AllocAndCopy<T>(T);
  Span<T> Alloc<T>(ElementCount); Span<T> AllocZeroed<T>(ElementCount); Span<T> AllocAndCopy<T>(ReadOnlySpan<T>);
  Ptr<T> Realloc<T>(Ptr<T>, ElementCount); void Copy<T>(ReadOnlySpan<T>, Span<T>); void Clear<T>(...);
}
public class PtrOwner<T> : IDisposable, INullable  // owns one Mem.Alloc'd T; finalizer frees
  { Ptr<T> Ptr; ref T Value; this[int]; Swap(Ptr<T>); Release(); Reset(Ptr<T> = default); implicit → Ptr<T>, T* }
public interface ISize { int Size {get;} }  public interface ISize2D { int Width {get;} int Height {get;} }
```
Gotchas: `Mem.Alloc<T>(count)` returns a `Span<T>` **not** marked `[return: Fixed]`, so `Ptr<T> p = Mem.Alloc<T>(n);` triggers analyzer error BRUTALCORE0010 (verified) — use `Mem.Alloc(n, ByteSize.Of<T>()).Cast<T>()` or `.AsPointer()` instead. `PtrOwner.Reset()` calls `Release()` which does **not** free the old pointer (leak) — free it yourself. GPU memory allocators live in Vulkan.Abstractions/Vma (§2).

### 8.5 Brutal.Collections (`namespace Brutal.Collections`)

- `NativeList<T> : INativeList<T>` (`T : unmanaged`, class, unmanaged backing store, finalizer + `Dispose()`/`Free()`): `NativeList(ElementCount capacity = default)`, `NativeList(ReadOnlySpan<T>)`, `Add`, `AddRange(ReadOnlySpan<T>)`, `Insert`, `RemoveAt`, `Clear`, `EnsureCapacity`, `TrimExcess`, `ref T this[Index]`, `Count`, `Capacity`, `Ptr` (`Ptr<T>`), `[return: Fixed] Span<T> AsSpan()`, `Slice(...)`, `GetPinnableReference()`, `AsNativeSpan()`. Good fit for per-frame vertex/instance staging; `AsSpan()` → `Ptr<T>` passes the [Fixed] check.
- `NativeList` (untyped byte list; `Add<T>(T)`, `AsSpan<T>()`, sizes in `ByteSize`), `NativeStrideList`, `NativeStrideSpan(Ptr, ElementCount, ByteSize stride)` (`ref struct`; `FromSpan<T>([Fixed] span)`, `WithStride`, `Slice`).
- `FreeListIndexPool(int capacity, bool allowResize)`: `Add()`/`TryAdd(out int)`, `Remove(int)`, `IsEmpty(int)` — index slot allocator (handle tables).
- `Name<T>` / `Namespace<T>`: interned string IDs (implicit from `string`).
- `Brutal.Collections.Experimental.ScratchSpan<T>(int requiredLength, Span<T> stackBuffer)` — use `stackalloc` buffer or rent from `ArrayPool`; `Dispose()` returns.

### 8.6 Brutal.Strings (`namespace Brutal.Strings`)

Global aliases inside BRUTAL sources (not exported to consumers; replicate if desired): `Char8 = byte`, `Char16 = char`, `String16 = string`, `String8View = ReadOnlySpan<byte>`, `String16View = ReadOnlySpan<char>`, `StringBuilder8 = StringBuilder<byte>`, `ValueStringBuilder8 = ValueStringBuilder<byte>`.
- `String8` (class, immutable UTF-8, **internally null-terminated**): `new String8(ReadOnlySpan<char>)`, `new String8(ReadOnlySpan<byte>)`, `Chars`/`AsSpan()` (without terminator), `Length`, `ToString16()`, implicit ⇄ `ReadOnlySpan<byte>`, `GetPinnableReference()`, `String8.Move(byte[])`.
- `RefString8` (`readonly ref struct`, `[NativeMarshalling(typeof(RefString8Marshaller))]`) = **zero-copy `const char*`**: holds a `ref byte` to the first char; marshaller just pins it. Implicit from `ReadOnlySpan<byte>`, `Span<byte>`, `byte[]`, `String8`, `[Fixed] Ptr<byte>`. **It does not copy or append a terminator** — the source must already be NUL-terminated. `"foo"u8` works in practice because C# u8 literals are emitted with a trailing `\0` (not part of the span); a sliced span or a plain `Encoding.UTF8.GetBytes` array will read past the end. `Chars` scans for NUL (`Utf8.GetNullTerminatedSpan`).
- `RefString8Array` (ref struct, `IDisposable`): build a `char**` from `String8[]`/`string[]` (e.g. extension/layer name lists): implicit from `string[]`, `String8[]`, spans.
- `StringBuilder<TChar>` (+ `StringBuilderPool<TChar>.Shared`), `ValueStringBuilder<TChar>(Span<TChar> buffer)` with interpolation handlers — allocation-free formatting into UTF-8/UTF-16.
- `Utf8`, `Utf16`, `Unicode` helpers; `Utf8.Path.*` mirrors `System.IO.Path` on UTF-8 (`Combine`, `GetExtension`, `GetFileName`, `Exists`, ...).
- **Dependency gotcha:** `Ahwoo.BRUTAL.Strings 2026.10.1` depends on `Microsoft.Extensions.ObjectPool 11.0.0-preview.5.26302.115` (a preview package) — it is pulled transitively by Glfw/Vulkan/ImGui/Slang/ShaderC.

### 8.7 Brutal.Concurrency (`namespace Brutal.Concurrency.Jobs`)

A simple batch "fork-join" scheduler on top of the .NET `ThreadPool` (no dependencies/graphs, no work stealing, no per-job handles):
```csharp
public interface IJob { void Run(); }
public class JobScheduler {
  public JobScheduler(uint jobRunnerCount, string debugName = "", ThreadPriority threadPriority = JobRunner.DEFAULT_THREAD_PRIORITY /*AboveNormal*/);
  public void Queue(IJob job, int timeCostEstimate);   // assigns to least-loaded runner; throws InvalidOperationException if executing
  public void ExecuteJobs();                           // each runner -> ThreadPool.UnsafeQueueUserWorkItem; runs its jobs sequentially
  public void Wait();                                  // BUSY-SPIN until all runners Idle (no sleep/yield)
  public void AbortJobs(bool waitForCompletion = false);
  public bool IsExecuting { get; }  public string Name { get; set; }
  public void SetThreadPriority(ThreadPriority);
  public void GetMetrics(Span<JobRunnerMetrics>); ReadOnlySpan<JobRunnerMetrics> GetMetricsUnsafe(); double GetMaxLastTickTime(); void ResetMetrics();
  public ReadOnlySpan<JobRunner> GetRunners(); ReadOnlySpan<int> GetWorkLoads();
}
public class JobRunner { // usable standalone
  public JobRunner(string name = "", ThreadPriority p = AboveNormal, int capacity = 1024, Lock? @lock = null);
  public void Queue(IJob); public void ExecuteJobs(JobCompletedDelegate? onComplete = null); public void RequestAbort(); public void Wait();
  public JobState State { get; }  // Idle, Scheduled, Running, Abort
  public delegate void JobCompletedDelegate(JobRunner runner, ExecutionResult result /*Success,NoJobs,Aborted*/, double lastTickTimeMillis);
}
public struct JobRunnerMetrics { double AverageTickTimeMillis, MaxTickTimeMillis, LastTickTimeMillis; }
```
Usage:
```csharp
sealed class SimChunkJob(int start, int end) : IJob { public void Run() { /* ... */ } }
var sched = new JobScheduler((uint)Environment.ProcessorCount - 1, "Sim");
for (var i = 0; i < chunks; i++) sched.Queue(new SimChunkJob(i * n, (i + 1) * n), timeCostEstimate: n);
sched.ExecuteJobs();
sched.Wait();      // spin-wait on calling thread
```
Gotchas: jobs are reference types (`IJob` interface ⇒ allocate/pool your job objects); cannot `Queue` while executing; **an exception thrown from `IJob.Run` is not caught** (only `finally` resets state) ⇒ it escapes on a ThreadPool thread and terminates the process; `Wait()` burns a core. `Brutal.Concurrency.Statistics`: `MovingAverage<T>`, `Sampler<T>`.

### 8.8 Brutal.Logging (`namespace Brutal.Logging`)

Wrapper over `Microsoft.Extensions.Logging` (Core already depends on `Microsoft.Extensions.Logging` + `.Console` 10.0.0).
```csharp
using Microsoft.Extensions.Logging;
LogSystem.Initialize(b => b.AddConsole().SetMinimumLevel(LogLevel.Information)); // sets IsEnabled = true, creates loggers for all categories
var log = LogCategory.Make("Renderer");   // or Get / TryGet; DefaultCategory.Log ("Default")
log.Info($"swapchain {w}x{h}");           // interpolated-string handler; formatting skipped when level disabled
log.Warning(...); log.Error(...); log.Error(exception); log.Debug(...); log.Trace(...); log.Critical(...);
log.Log(LogLevel.Information, $"...");  log.IsCategoryEnabled = false;  log.AppendCallerInfo = true;
```
- `LogSystem.IsEnabled` defaults **false** ⇒ all BRUTAL logging is a no-op until `Initialize`. If `IsEnabled` is set true manually without `Initialize`, logging throws `LogNotInitializedException`.
- Library categories: `Glfw.Log` (`"Glfw"`), `Vulkan.Log` (`"Vulkan"`), `"Sdl"`, `"SdlTtf"`, `"SdlImage"`, `"SdlMixer"`, `"RenderDoc"`, `"Monitor"`.
- **Every generated GLFW P/Invoke wrapper calls `Log.Trace()`** (logs the caller member name, e.g. `Init`, `PollEvents`) ⇒ don't enable `Trace` for category `Glfw` in production (filter: `b.AddFilter("Glfw", LogLevel.Information)`). Vulkan: `Vulkan.Log.Warning` when an `Instance` is finalized without `Dispose()`; Vulkan.Abstractions' debug-utils messenger callback (`VulkanHelpers`) routes validation messages to `Vulkan.Log.Error/Warning/Info/Debug` by severity.

## 9. Native binary distribution (downloaded and inspected)

Downloaded from `https://api.nuget.org/v3-flatcontainer/<id>/<ver>/<id>.<ver>.nupkg` into `/tmp/claude-1000/brutal-nupkgs/` and extracted to `/tmp/claude-1000/brutal-nupkgs/x/<lowercase id>/`. Each id has exactly **one** published version in the 2026.10 line (the flat-container index lists only that one version). The table shows the latest version on 2026-10-15.

**Exact package id casing is `Ahwoo.BRUTAL.<Name>`** (nuspec `<id>`; e.g. `Ahwoo.BRUTAL.Vulkan.Abstractions`). The README spells it `Ahwoo.Brutal.*`. NuGet ids are case-insensitive, so both restore, but use the nuspec casing in docs and lock files. Repository: `https://github.com/ahwootech/BRUTAL`. License: file `LICENSE.txt` (BRUTAL License, which is MIT plus attribution: credits notice, BRUTAL logo before gameplay, ship `NOTICE.txt`).

All packages: **TFM `net10.0` only**, `lib/net10.0/<Assembly>.dll` + `.xml` docs + `.pdb`. Native RIDs, where natives ship: **`linux-x64`, `win-x64`, `osx-arm64`** (no linux-arm64, no win-arm64, no osx-x64). Windows packages also carry `build/natives/lib/win-x64/*.lib` import libs for native consumers (no MSBuild targets in `build/`, only data).

| Package (id / version) | Assembly | Native files (linux-x64 / win-x64) | Dependencies (all `exclude="Build,Analyzers"`) |
|---|---|---|---|
| `Ahwoo.BRUTAL.Core` 2026.10.1 | Brutal.Core | none; **ships `analyzers/dotnet/cs/Brutal.Core.Analyzers.dll` + `Brutal.Core.CodeFixes.dll`** | CommunityToolkit.HighPerformance 8.4.2, Microsoft.Extensions.Logging 10.0.0, Microsoft.Extensions.Logging.Console 10.0.0 |
| `Ahwoo.BRUTAL.Glfw` 2026.10.4 | Brutal.Glfw | `libglfw.so`, `.so.3`, `.so.3.5` / `glfw3.dll` | Core 2026.10.1, Logging 2026.10.2, Numerics 2026.10.1 |
| `Ahwoo.BRUTAL.Vulkan` 2026.10.2 | Brutal.Vulkan | `libVulkanEx.so` / `VulkanEx.dll` (small helper lib only). **No Vulkan loader shipped:** it uses the system `libvulkan` / `vulkan-1.dll` | Core, Logging 2026.10.2, Memory, Numerics, Strings (2026.10.1) |
| `Ahwoo.BRUTAL.Vulkan.Abstractions` 2026.10.2 | Brutal.Vulkan.Abstractions | none | Vulkan 2026.10.2, Collections, Logging, Memory, Numerics, Strings |
| `Ahwoo.BRUTAL.Vulkan.Vma` 2026.10.2 | Brutal.Vulkan.Vma | `libvma.so` / `vma.dll` | Vulkan.Abstractions 2026.10.2, Vulkan 2026.10.2 |
| `Ahwoo.BRUTAL.Slang` 2026.10.1 (~100 MB) | Brutal.Slang | `libslang.so`, `libslang-compiler.so{,.0.2026.7}`, `libslang-glslang-2026.7.so`, `libslang-glsl-module-2026.7.so` / `slang.dll`, `slang-compiler.dll`, `slang-glslang.dll`, `slang-glsl-module.dll` | CommunityToolkit.HighPerformance 8.4.2, Core, Interop, Strings |
| `Ahwoo.BRUTAL.ShaderC` 2026.10.2 (~17 MB) | Brutal.ShaderC | `libshaderc_shared.so{,.1}`, `libSPIRV-Tools-shared.so` / `shaderc_shared.dll`, `SPIRV-Tools-shared.dll` | Interop, Memory, Strings |
| `Ahwoo.BRUTAL.FreeType` 2026.10.1 | Brutal.FreeType (5 KB, **no managed API**; see section 5) | `libfreetype.so{,.6,.6.20.6}`, `libpng18.so{,.18,...}` / `freetype.dll`, `png18.dll`; plus `build/natives/include/freetype/*.h`, `cmake/FindFreetype.cmake`, `buildTransitive/Ahwoo.BRUTAL.FreeType.targets` (adds `-DFREETYPE_NUGET_DIR=` to `$(CMakeBuildArgs)`) | Core, ZLib 2026.10.1 |
| `Ahwoo.BRUTAL.Stb` 2026.10.2 | Brutal.Stb | `libstb.so` / `stb.dll` | Core, Numerics, Strings |
| `Ahwoo.BRUTAL.ImGui` 2026.10.2 | Brutal.ImGui | `libimgui.so` / `imgui.dll` (Dear ImGui 1.92.2 docking branch) | CommunityToolkit.HighPerformance, Core, Localization 2026.10.2, Memory, Numerics, Strings |
| `Ahwoo.BRUTAL.ImGui.Backends` 2026.10.2 | Brutal.ImGui.Backends (pure managed) | none | ImGui 2026.10.2, CommunityToolkit.HighPerformance, **Glfw 2026.10.4, OpenGl 2026.10.1, Vulkan.Abstractions 2026.10.2** |
| `Ahwoo.BRUTAL.Sdl` 2026.10.2 | Brutal.Sdl | `libSDL3.so{,.0,.0.4.12}` / `SDL3.dll` | (not inspected) |

(For Fmod/Texture/Ktx/Gli/Sdl.Ttf packages see sections 4, 5 and 7.)

Notes:
- Native-library resolution is done per assembly in static constructors with `NativeLibrary.Load(name, assembly, DllImportSearchPath.AssemblyDirectory)` plus `NativeLibrary.SetDllImportResolver(assembly, ...)`. Consequences:
  - Each BRUTAL assembly owns its resolver, so **you cannot install your own `DllImportResolver` for those assemblies**. A second `SetDllImportResolver` throws `InvalidOperationException`, and installing yours first makes their static ctor throw.
  - Use `AssemblyLoadContext.Default.ResolvingUnmanagedDll` as the fallback hook. This was verified to work for Vulkan.
- **Linux Vulkan loader gotcha (verified):**
  - `Brutal.VulkanApi.Vulkan`'s static ctor calls `NativeLibrary.Load("vulkan", asm, DllImportSearchPath.LegacyBehavior)`. That probes only `libvulkan.so` / `vulkan.so` / `libvulkan` / `vulkan`, never `libvulkan.so.1`.
  - Most end-user systems, including this WSL2 box, have only `libvulkan.so.1`, because the unversioned symlink comes from `libvulkan-dev`.
  - Result: `TypeInitializationException -> DllNotFoundException: Unable to load shared library 'vulkan'`.
  - Fix (verified), installed before first touching `Vulkan`:
    ```csharp
    System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (asm, name) =>
        name == "vulkan" && OperatingSystem.IsLinux() ? NativeLibrary.Load("libvulkan.so.1") : nint.Zero;
    ```
  - Alternatives: ship or symlink `libvulkan.so` into the app directory, or install `libvulkan-dev`.
  - Windows loads `vulkan-1` from System32, which is fine wherever a driver is installed.
- The ONI game ships its own natives (e.g. `libfmodstudio.so`). BRUTAL natives are resolved per assembly by explicit path in the app directory, so name clashes only matter if two libraries with the same file name must sit in the same output folder (see section 7).
- Package sizes: Slang 99.9 MB, ShaderC 16.8 MB, FreeType 4.3 MB, ImGui 2.9 MB, Vulkan 1.3 MB, Glfw 0.9 MB. Choose ShaderC **or** Slang if download or installer size matters.

## 10. Project settings, analyzers, architecture guidance

### 10.1 Required / recommended consumer project settings
- `TargetFramework` **`net10.0`** (all packages are net10.0-only). A **.NET 10 SDK (Roslyn 5.x)** is required: the analyzer DLLs reference `Microsoft.CodeAnalysis 5.0.0.0` (verified — loading them with Roslyn 4.14 fails), an older compiler would only emit CS8032 and skip them.
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` — practically required (pointers, `Ptr<T>`, `fixed`, struct `pNext` chains). Not forced by the packages (no props set it; a safe-code-only consumer should compile without it — not separately verified), but any `unsafe` block errors with CS0227 otherwise (verified).
- No `.props`/`.targets` are shipped by Core/Glfw/Vulkan/Vma/Slang/ShaderC/Stb/ImGui packages. Only `Ahwoo.BRUTAL.FreeType` ships `buildTransitive/Ahwoo.BRUTAL.FreeType.targets`, which only appends `-DFREETYPE_NUGET_DIR=...` to a `CMakeBuildArgs` property (for building native code against its headers; harmless otherwise).
- Uses C# 14 features (extension members in `Brutal.Enums`), fine with default LangVersion on net10.
- Transitive packages always pulled: `CommunityToolkit.HighPerformance 8.4.2`, `Microsoft.Extensions.Logging(.Console) 10.0.0`, `Microsoft.Extensions.ObjectPool 11.0.0-preview.5.26302.115` (via Strings).
- Licence obligations for a shipped product (README/LICENSE): BRUTAL copyright in credits, BRUTAL logo before gameplay, ship `NOTICE.txt`; third-party notices in each package's `LICENSES`.

### 10.2 Roslyn analyzers (shipped in packages — not in the source repo)
Dumped by loading the analyzer DLLs with Roslyn 5.0 and enumerating `SupportedDiagnostics` (scratch tool, verified):

| ID | Package | Default severity | Title / message |
|---|---|---|---|
| BRUTALCORE0001 | Strings | Warning | `ToString()` on `(ReadOnly)Span<byte>` — did you mean `ToString16`? (codefix) |
| BRUTALCORE0002 | Strings | Warning | `ToString()` on `Span<T>` (T not char/byte) |
| BRUTALCORE0003 | Core | Info | Prefer `Brutal.Platform` over `System.OperatingSystem.IsWindows/IsLinux/IsMacOS` (codefix) |
| BRUTALCORE0004 | Numerics | Warning | Prefer Brutal.Numerics over `System.Drawing.Color` |
| BRUTALCORE0005 | Numerics | Info | Prefer Brutal.Numerics over System.Numerics |
| BRUTALCORE0006 | Numerics | Info | Prefer `Color.Gray` etc. over explicit channel values (codefix) |
| BRUTALCORE0007 | Core | Info | Prefer `var` over explicit type (codefix) |
| BRUTALCORE0008 | Core | Info | Prefer `var x = default(T)` over `T x = default` (codefix) |
| **BRUTALCORE0009** | Core | **Error** | "Potential unsafe call to {0}. Ensure the argument for '{1}' comes from a fixed source." (argument to a `[Fixed]` parameter) |
| **BRUTALCORE0010** | Core | **Error** | "Potential unsafe implicit conversion from {0} to {1}. Ensure cast comes from a fixed source." (`Span`→`Ptr<T>` etc.) |
| BRUTALCORE0011 | Strings | Warning | "Prefer using a UTF8 string: {0}" (a `string` overload used where a UTF-8 overload exists; codefix) |
| BRUTALCORE0012 | Core | Warning | Delegate conversion loses `[Fixed]` |

**They DO apply to consumer code, transitively** (verified): although every BRUTAL nuspec dependency says `exclude="Build,Analyzers"`, a project referencing only `Ahwoo.BRUTAL.Glfw` got `/analyzer:` args for Core, Strings and Numerics analyzers and failed with `error BRUTALCORE0010` (and `warning BRUTALCORE0002`). Adding a direct `PackageReference ... ExcludeAssets="analyzers"` for Core/Strings/Numerics did **not** remove them (other transitive paths re-add them).

FixedAnalyzer behaviour (verified on a test file):
- `Ptr<int> p = array.AsSpan();` → **error 0010**. `Take(array)` where param is `Ptr<int>` → error 0010.
- `Ptr<int> p = Mem.Alloc<int>(4);` (Span result) → **error 0010**; `(ReadOnlySpan<int>)x` → error.
- OK: `Span<int> s = stackalloc int[4]; Ptr<int> p = s;`, pointers from `fixed (int* fp = arr)`, `nativeList.AsSpan()` (`[return: Fixed]`), `ptr.AsSpan(n)`.
- `RefString8` from `"..."u8` / `byte[]` → no diagnostic (only its `Ptr<byte>` ctor is `[Fixed]`).
- Escape hatches: `span.AsPointer()` / `Span<T>.AsPtr()` (from `Brutal.Pointers.Extensions`, unchecked), `fixed` statements, `[Fixed]` on your own params/returns, or suppress.

Suppression that works (verified): `<NoWarn>$(NoWarn);BRUTALCORE0010;BRUTALCORE0009</NoWarn>` in the csproj, or `.editorconfig`: `dotnet_diagnostic.BRUTALCORE0010.severity = warning|none`. Info-level ones (var, Platform, System.Numerics) only show in IDE; they don't fail builds. Recommendation for the ONI port: keep 0009/0010 on (they catch real GC-moving-memory bugs) but be ready to use `fixed`/`stackalloc`/`NativeList` patterns; consider downgrading 0011/0001 noise if porting lots of `string`-based code.

### 10.3 Architecture guidance (README/CONTRIBUTING; `docs/` contains only branding PNGs)
- BRUTAL is a "kernel": interop, memory, concurrency, maths, strings + native bindings. **No renderer, scene graph, editor**; you build your own stack ("you actually have to know what you want to do first").
- Design ethos: avoid allocations in hot paths, unmanaged memory + `unsafe` where it makes sense, expose native APIs directly rather than abstract them.
- Prereqs per README: .NET 10 SDK, Vulkan **1.3**-capable GPU (if rendering), Vulkan SDK for validation layers; Windows or Linux (packages also ship osx-arm64 natives).
- The repo has **no project files/solution/build scripts yet** ("coming") — consume via NuGet; source is for reading only. Generated code (`Generated/`, `*.gen.cs`) comes from generators not in the repo.
- Getting-started sample (README) — the canonical minimal GLFW window:
  ```csharp
  using Brutal.GlfwApi; using Brutal.Numerics;
  if (!Glfw.Init()) throw new Exception("Failed to initialize GLFW");
  Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
  var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "Hello BRUTAL", Size = new int2(1280, 720) });
  while (!window.ShouldClose) Glfw.PollEvents();
  Glfw.Terminate();
  ```
- Contributions: scope is policed aggressively; PRs need DCO sign-off; questions go to forums.ahwoo.com.

## 11. Smoke tests (verified 2026-10-15 on WSL2 + WSLg, Mesa lavapipe, .NET SDK 10.0.402 / runtime 10.0.13)

### 11.1 GLFW + Vulkan: `/tmp/claude-1000/brutal-smoke`
The exact csproj that worked:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Ahwoo.BRUTAL.Glfw" Version="2026.10.4" />
    <PackageReference Include="Ahwoo.BRUTAL.Vulkan" Version="2026.10.2" />
    <PackageReference Include="Ahwoo.BRUTAL.Vulkan.Abstractions" Version="2026.10.2" />
  </ItemGroup>
</Project>
```
- `RuntimeIdentifier` is not strictly required, since portable builds copy `runtimes/` too, but it flattens the natives into `bin/` (`libglfw.so*` and `libVulkanEx.so`).
- Build output contains `Brutal.{Collections,Core,Glfw,Logging,Memory,Numerics,Strings,Vulkan,Vulkan.Abstractions}.dll`, `CommunityToolkit.HighPerformance.dll` and a dozen `Microsoft.Extensions.*` dlls (pulled in by Core/Logging).

Program (abridged; full file at `/tmp/claude-1000/brutal-smoke/Program.cs`):
```csharp
using System.Runtime.InteropServices; using Brutal.GlfwApi; using Brutal.VulkanApi;
System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (asm, name) =>
    name == "vulkan" && OperatingSystem.IsLinux() ? NativeLibrary.Load("libvulkan.so.1") : nint.Zero;   // REQUIRED here
unsafe {
  Glfw.ErrorCallback += (c, d) => Console.WriteLine($"[glfw error] {c}: {d}");
  if (args.Contains("x11")) Glfw.SetInitHint(GlfwInitHint.Platform, 0x00060004);
  bool ok = Glfw.Init();
  var exts = new HashSet<string>();
  byte** names = Glfw.GetRequiredInstanceExtensions(out uint count);
  for (int i = 0; i < count; i++) exts.Add(Marshal.PtrToStringUTF8((nint)names[i])!);
  fixed (byte* pName = "brutal-smoke"u8) {
    using var instance = Vulkan.CreateInstance(new Instance.CreateInfo {
        AppInfo = new VkApplicationInfo { ApplicationName = pName, ApiVersion = (1 << 22) | (3 << 12) },
        EnabledExtensions = exts, EnabledLayers = new HashSet<string>() });
    foreach (var pd in instance.PhysicalDevices) {
        var props = pd.GetProperties();
        Console.WriteLine($"{Marshal.PtrToStringUTF8((nint)props.DeviceName)} {props.DeviceType} gfxQueue={pd.GetFirstQueueFamilyWithFlag(VkQueueFlags.GraphicsBit)}");
    }
    Glfw.WindowHint(GlfwWindowHint.ClientApi, 0); Glfw.WindowHint(GlfwWindowHint.Visible, 0);
    using var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "smoke", Size = new Brutal.Numerics.int2(320, 240) });
    int r = Glfw.CreateWindowSurface(instance.VkHandle, window, out ulong surface);
    bool present = Glfw.GetPhysicalDevicePresentationSupport(instance.VkHandle, instance.PhysicalDevices[0].VkHandle, 0);
  }
  Glfw.Terminate();
}
```
Output (default platform = Wayland under WSLg):
```
GLFW version: 3.5.1 Wayland X11 GLX Null EGL OSMesa monotonic shared
Glfw.Init() = True, platform = Wayland
VulkanSupported = True
GLFW required instance extensions: VK_KHR_surface, VK_KHR_wayland_surface
Monitor weston rdp: 2560x1440@60 scale=1   (x2)
VkInstance created: 0x629FE4CAB040, physical devices: 1
  - llvmpipe (LLVM 20.1.2, 256 bits) type=Cpu api=1.4.318 gfxQueue=0
[glfw error] FeatureUnavailable: Wayland: The platform does not provide the window position
Window created: fb=320x240 contentScale=1
glfwCreateWindowSurface -> VkResult 0, surface=0x629FE4C1BB90
Presentation support (pd0, qf0): True
```
- With `x11`: platform = X11; the extensions are `VK_KHR_surface, VK_KHR_xcb_surface`; monitors are `rdp-0`/`rdp-2`; the window, surface and presentation all work, with no position error.
- With `DISPLAY= WAYLAND_DISPLAY=`: `Glfw.Init()` = False (`PlatformUnavailable: Failed to detect any supported platform`). The Vulkan instance and enumeration still work (lavapipe listed).
- **Without the `ResolvingUnmanagedDll` hook the program dies** at `Vulkan.CreateInstance` with `TypeInitializationException -> DllNotFoundException: Unable to load shared library 'vulkan'`. The machine has only `libvulkan.so.1`.
- Not exercised: device creation, swapchain, rendering, VMA.

### 11.2 Shaders: `/tmp/claude-1000/brutal-smoke-shaders`
Same csproj shape with `Ahwoo.BRUTAL.ShaderC` 2026.10.2 + `Ahwoo.BRUTAL.Slang` 2026.10.1. Output:
```
ShaderC: 448 bytes SPIR-V, magic=0x07230203, warnings=0
ShaderC error path OK: ShaderCompilationException: bad.vert:2: error: 'x' : undeclared identifier
Slang build tag: 2026.7
Slang entry 0: 932 bytes SPIR-V, magic=0x07230203
Slang entry 1: 1088 bytes SPIR-V, magic=0x07230203
Slang reflection: 3 global params ...  gParams: binding=0 space=0 / gTex: binding=1 space=0 / gSamp: binding=2 space=0
```
- The first attempt segfaulted (exit 139) inside `ISessionExtensions.CreateCompositeComponentType(ReadOnlySpan<IComponentType>)`. The run above uses the raw-pointer workaround documented in section 3.2.
