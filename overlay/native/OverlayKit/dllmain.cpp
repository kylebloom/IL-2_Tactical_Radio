// OverlayKit host: once loaded into a Direct3D 11 game, it hooks the swap chain's Present and
// Present1 by swapping their entries in DXGI's shared swap-chain vtable, and draws the clients'
// layers into every frame. It reads and writes only its own shared memory; it never reads the game's.
#include "host.h"

namespace
{
    using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
    using Present1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);

    constexpr int kPresentSlot = 8;   // IDXGISwapChain::Present
    constexpr int kPresent1Slot = 22; // IDXGISwapChain1::Present1

    PresentFn g_present;
    Present1Fn g_present1;
    Renderer* g_renderer;
    SRWLOCK g_drawLock = SRWLOCK_INIT;
    volatile bool g_disabled;

    // A fault in our drawing turns the overlay off instead of taking the game down.
    void DrawGuarded(IDXGISwapChain* swapChain)
    {
        __try
        {
            g_renderer->Draw(swapChain);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            g_disabled = true;
            Log("fault 0x%08lx while drawing; overlay disabled for this session", GetExceptionCode());
        }
    }

    void DrawOverlay(IDXGISwapChain* swapChain, UINT flags)
    {
        if (g_disabled || (flags & DXGI_PRESENT_TEST))
            return;
        AcquireSRWLockExclusive(&g_drawLock);
        DrawGuarded(swapChain);
        ReleaseSRWLockExclusive(&g_drawLock);
    }

    HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
    {
        DrawOverlay(swapChain, flags);
        return g_present(swapChain, syncInterval, flags);
    }

    HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* swapChain, UINT syncInterval, UINT flags, const DXGI_PRESENT_PARAMETERS* parameters)
    {
        DrawOverlay(swapChain, flags);
        return g_present1(swapChain, syncInterval, flags, parameters);
    }

    // Points one vtable entry at our hook; the original is stored first, so a game thread calling
    // through the entry mid-swap always finds a function to forward to.
    template <typename Fn>
    void Patch(void** vtable, int slot, Fn hook, Fn& original)
    {
        original = reinterpret_cast<Fn>(vtable[slot]);
        DWORD protect;
        if (!VirtualProtect(&vtable[slot], sizeof(void*), PAGE_READWRITE, &protect))
        {
            Log("VirtualProtect failed on vtable slot %d (error %lu)", slot, GetLastError());
            return;
        }
        void* previous = InterlockedExchangePointer(&vtable[slot], reinterpret_cast<void*>(hook));
        original = reinterpret_cast<Fn>(previous);
        VirtualProtect(&vtable[slot], sizeof(void*), protect, &protect);
    }

    // Makes a throwaway device and swap chain on a hidden window just to find DXGI's vtable.
    DWORD WINAPI Install(void*)
    {
        WNDCLASSEXW wc{ sizeof(wc) };
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"OverlayKitProbe";
        RegisterClassExW(&wc);
        HWND window = CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);

        DXGI_SWAP_CHAIN_DESC desc{};
        desc.BufferCount = 1;
        desc.BufferDesc = { 64, 64, { 0, 1 }, DXGI_FORMAT_B8G8R8A8_UNORM };
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.OutputWindow = window;
        desc.SampleDesc = { 1, 0 };
        desc.Windowed = TRUE;
        desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        ComPtr<IDXGISwapChain> swapChain;
        ComPtr<ID3D11Device> device;
        HRESULT hr = E_FAIL;
        for (D3D_DRIVER_TYPE driver : { D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP })
            if (SUCCEEDED(hr = D3D11CreateDeviceAndSwapChain(nullptr, driver, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &desc, &swapChain, &device, nullptr, nullptr)))
                break;
        if (FAILED(hr))
        {
            Log("probe swap chain failed (0x%08lx); no overlay", hr);
        }
        else
        {
            void** vtable = *reinterpret_cast<void***>(swapChain.Get());
            Patch(vtable, kPresentSlot, &HookPresent, g_present);
            ComPtr<IDXGISwapChain1> swapChain1;
            if (SUCCEEDED(swapChain.As(&swapChain1)))
            {
                void** vtable1 = *reinterpret_cast<void***>(swapChain1.Get());
                Patch(vtable1, kPresent1Slot, &HookPresent1, g_present1);
            }
            Log("hooks installed (Present%s)", g_present1 ? ", Present1" : "");
        }
        swapChain.Reset();
        device.Reset();
        if (window)
            DestroyWindow(window);
        UnregisterClassW(wc.lpszClassName, wc.hInstance);
        return 0;
    }
}

extern "C" __declspec(dllexport) uint32_t OverlayKitVersion()
{
    return OK_VERSION;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        // The hooks point into this DLL, so it must never unload while the game runs.
        HMODULE pinned;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&DllMain), &pinned);
        wchar_t exe[MAX_PATH];
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        Log("loaded into %ls", exe);
        g_renderer = new Renderer();
        if (HANDLE thread = CreateThread(nullptr, 0, Install, nullptr, 0, nullptr))
            CloseHandle(thread);
    }
    return TRUE;
}
