# OverlayKit

Draws other programs' images inside a Direct3D 11 game's own frames, including exclusive fullscreen, without RivaTuner Statistics Server. IL-2 Tactical Radio is its first client (chat output `ingame`), but nothing here knows about IL-2: any add-on can publish layers.

**Status: proof of concept (2026-10-27).** Verified against the bundled test host only, not yet in IL-2 (see "Verified" below).

## How it works

```
 client program (any language)                game process (D3D11)
 ┌───────────────────────────┐                ┌──────────────────────────────────────┐
 │ OverlayLayer.Update(bgra) │──shared mem──► │ overlaykit64.dll                     │
 │ OverlayLayer.Place(...)   │  directory +   │   hooks IDXGISwapChain::Present(1)   │
 │ OverlayInjector.EnsureLoaded ─LoadLibrary─►│   uploads changed layers to textures │
 └───────────────────────────┘  (once)        │   draws them, then calls Present     │
                                              └──────────────────────────────────────┘
```

- **Host** (`native/OverlayKit`, `overlaykit64.dll`): once loaded into a game, it makes a throwaway device and swap chain to find DXGI's swap-chain vtable, and points `Present` and `Present1` at its own functions. Before forwarding each frame it draws every visible layer as a 1:1 textured quad with premultiplied-alpha blending. The game's whole pipeline state is set aside with `ID3D11DeviceContext1::SwapDeviceContextState` and restored afterwards, so nothing leaks into the game's next draw. It holds no back-buffer references between frames, so it doesn't interfere with `ResizeBuffers`. A fault while drawing turns the overlay off for that session instead of crashing the game. It pins itself in memory (the hooks point into it) and stays until the game exits.
- **Protocol** (`include/overlaykit.h`): a fixed directory, `Local\OverlayKit.v1.Directory`, with 8 host slots (each game reports its pid, exe name, back-buffer size and frame count) and 16 layer slots (owner, target exe, position, anchor, opacity, z-order, heartbeat, and the name of a pixel buffer). Each layer's pixels live in a mapping of their own, written as a seqlock, so the host never draws a half-written image. Pixels are 32-bit premultiplied BGRA (GDI+ `Format32bppPArgb`, Direct2D's default). A layer whose client stops beating for 3 s disappears, so a crashed client's text doesn't stick on screen.
- **Client** (`dotnet/OverlayKit.Client`): `OverlayLayer` (publish and place a bitmap), `Overlay` (list hosts, request a capture), and `OverlayInjector` (load the host DLL into a process). The DLL is embedded in the library when `build.ps1` has been run, and written once to `%LOCALAPPDATA%\OverlayKit\overlaykit64-<hash>.dll` for the game to load. A client in another language only has to follow `overlaykit.h`.
- **Diagnostics:** each host logs to `%TEMP%\OverlayKit\host-<pid>.log`. `Overlay.RequestCapture()` makes every host save its next frame, overlay included, to `%TEMP%\OverlayKit\capture-<pid>.bmp`. This also works in exclusive fullscreen, where screen captures see nothing.

## Using it from .NET

```csharp
using OverlayKit;

var game = Process.GetProcessesByName("Il-2")[0];
OverlayInjector.EnsureLoaded(game);                  // ask the player first: this touches the game process

var host = Overlay.Host("il-2.exe");                 // null until the game presents a frame
using var layer = new OverlayLayer("My add-on", "il-2.exe");
layer.Place(OverlayAnchor.TopRight, 40, 40);         // back-buffer pixels in from that corner
layer.Update(pixels, width, height, stride);         // premultiplied BGRA; render at host.Width x host.Height scale
layer.Visible = true;
// Dispose the layer to remove it at once.
```

`dotnet/OverlayKit.Demo` is a complete client: `OverlayKit.Demo --target <exe|pid> [--seconds N] [--capture] [--no-load]`.

## Building

```bash
pwsh overlay/build.ps1
```

This needs Visual Studio 2022 or later (or its Build Tools) with the C++ workload and a Windows 10/11 SDK. It writes `overlay/bin/overlaykit64.dll` and `overlay/bin/OverlayKitTestHost.exe`. Then build any .NET client as usual; `OverlayKit.Client` embeds the DLL if it exists. Without the native build, the .NET projects still compile, and `OverlayInjector.HostDllPath()` reports the DLL missing.

The test host stands in for a game: `OverlayKitTestHost [--size WxH] [--seconds N] [--blt] [--fullscreen] [--load overlaykit64.dll]`. It never takes focus when windowed.

## Limits and rules

- **Direct3D 11, 64-bit only.** D3D12, Vulkan and OpenGL games aren't hooked. A D3D12 swap chain is detected and skipped. 32-bit games are refused.
- **The headset in VR isn't covered.** The layers land on the game's desktop mirror window, as with RTSS. In-headset display would need an OpenXR API layer or a SteamVR overlay instead.
- **Colour:** layers are blended as stored. On an sRGB or 10-bit back buffer, translucent edges may look slightly different than on screen in a window.
- **Coexistence:** RTSS, Steam, Discord and ReShade hook the same calls in other ways (inline patches or proxies). Hooking through the vtable chains with them in principle. Each pairing still needs checking.
- **Only load into single-player or offline games.** Loading a foreign DLL is what anti-cheat systems look for. The host never reads or writes game memory, but loading it at all is a choice the player should make knowingly, and a client should not load it into multiplayer sessions.

## Verified (2026-10-27, test host on the development PC; IL-2 was running at the time and was not touched)

- Injection with `LoadLibraryW` on a remote thread, and loading at start-up; hooks installed on `Present` and `Present1`.
- Drawing in a flip-model (`FLIP_DISCARD`) and a legacy (`DISCARD`) swap chain, 1280×720 and 1920×1080, R8G8B8A8 back buffer: the captured frame shows the layer at the right place, 1:1 and alpha-blended over the game's image.
- IL-2 Tactical Radio's chat (`InGameChat`) rendered through it, identical in style to the window overlay.

## Not yet verified

- IL-2 itself: exclusive fullscreen, its back-buffer format, frame-time cost, and running alongside RTSS.
- The exclusive-fullscreen test host (`--fullscreen`) wasn't run, so as not to disturb the IL-2 session that was open.
- Device loss or re-creation, multiple swap chains, and MSAA back buffers. The code handles each, but none has been exercised.
