// A stand-in Direct3D 11 game for testing OverlayKit without a real one: a window that clears to a
// slowly shifting colour and presents every frame.
//
//   OverlayKitTestHost [--fullscreen] [--seconds N] [--flip | --blt] [--load path\to\overlaykit64.dll]
//
// --load puts the host DLL in at start-up; without it, inject it from a client while this runs.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

using Microsoft::WRL::ComPtr;

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_DESTROY || (msg == WM_KEYDOWN && wp == VK_ESCAPE))
    {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

int wmain(int argc, wchar_t** argv)
{
    bool fullscreen = false, flip = true;
    double seconds = 0;
    const wchar_t* load = nullptr;
    int width = 1280, height = 720;
    for (int i = 1; i < argc; i++)
    {
        if (!wcscmp(argv[i], L"--fullscreen")) fullscreen = true;
        else if (!wcscmp(argv[i], L"--flip")) flip = true;
        else if (!wcscmp(argv[i], L"--blt")) flip = false;
        else if (!wcscmp(argv[i], L"--seconds") && i + 1 < argc) seconds = _wtof(argv[++i]);
        else if (!wcscmp(argv[i], L"--load") && i + 1 < argc) load = argv[++i];
        else if (!wcscmp(argv[i], L"--size") && i + 1 < argc) swscanf_s(argv[++i], L"%dx%d", &width, &height);
    }
    if (fullscreen)
    {
        width = GetSystemMetrics(SM_CXSCREEN);
        height = GetSystemMetrics(SM_CYSCREEN);
    }

    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    WNDCLASSEXW wc{ sizeof(wc) };
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"OverlayKitTestHost";
    RegisterClassExW(&wc);
    DWORD style = fullscreen ? WS_POPUP : WS_OVERLAPPEDWINDOW;
    RECT r{ 0, 0, width, height };
    AdjustWindowRect(&r, style, FALSE);
    HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"OverlayKit test host", style, fullscreen ? 0 : 40, fullscreen ? 0 : 40,
        r.right - r.left, r.bottom - r.top, nullptr, nullptr, wc.hInstance, nullptr);
    ShowWindow(hwnd, fullscreen ? SW_SHOW : SW_SHOWNOACTIVATE); // windowed, never steal focus from a game being played

    DXGI_SWAP_CHAIN_DESC desc{};
    desc.BufferCount = 2;
    desc.BufferDesc = { (UINT)width, (UINT)height, { 60, 1 }, DXGI_FORMAT_R8G8B8A8_UNORM };
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.OutputWindow = hwnd;
    desc.SampleDesc = { 1, 0 };
    desc.Windowed = TRUE;
    desc.SwapEffect = flip ? DXGI_SWAP_EFFECT_FLIP_DISCARD : DXGI_SWAP_EFFECT_DISCARD;
    desc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;

    ComPtr<IDXGISwapChain> swapChain;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
        &desc, &swapChain, &device, nullptr, &context);
    if (FAILED(hr))
    {
        fwprintf(stderr, L"D3D11CreateDeviceAndSwapChain failed: 0x%08lx\n", hr);
        return 1;
    }
    if (fullscreen && FAILED(hr = swapChain->SetFullscreenState(TRUE, nullptr)))
        fwprintf(stderr, L"SetFullscreenState failed: 0x%08lx (running windowed)\n", hr);

    wprintf(L"pid %lu, %dx%d, %s%s\n", GetCurrentProcessId(), width, height, flip ? L"flip" : L"blt", fullscreen ? L", exclusive fullscreen" : L"");
    fflush(stdout);
    if (load)
    {
        if (LoadLibraryW(load)) wprintf(L"loaded %s\n", load);
        else fwprintf(stderr, L"LoadLibrary %s failed: %lu\n", load, GetLastError());
        fflush(stdout);
    }

    ULONGLONG start = GetTickCount64();
    MSG msg{};
    while (msg.message != WM_QUIT)
    {
        if (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
            continue;
        }
        double t = (GetTickCount64() - start) / 1000.0;
        if (seconds > 0 && t >= seconds)
            break;

        ComPtr<ID3D11Texture2D> backBuffer;
        swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
        ComPtr<ID3D11RenderTargetView> rtv;
        device->CreateRenderTargetView(backBuffer.Get(), nullptr, &rtv);
        float colour[4] = { 0.30f + 0.10f * (float)sin(t * 0.7), 0.42f, 0.55f + 0.10f * (float)cos(t * 0.5), 1 };
        context->ClearRenderTargetView(rtv.Get(), colour);
        ID3D11RenderTargetView* targets[] = { rtv.Get() };
        context->OMSetRenderTargets(1, targets, nullptr);
        swapChain->Present(1, 0);
    }

    if (fullscreen)
        swapChain->SetFullscreenState(FALSE, nullptr);
    return 0;
}
