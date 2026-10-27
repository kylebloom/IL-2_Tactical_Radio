// Internal declarations shared by the host DLL's files.
#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <string>
#include <vector>
#include "../../include/overlaykit.h"

using Microsoft::WRL::ComPtr;

// Appends a line to %TEMP%\OverlayKit\host-<pid>.log.
void Log(const char* format, ...);

// The directory in shared memory, opened (or created) on first use.
class Directory
{
public:
    // Null until the mapping can be opened.
    OkDirectory* Get();

    // This process's host slot, claimed on first use; null when all are taken.
    OkHost* Host();

private:
    HANDLE mapping_ = nullptr;
    OkDirectory* view_ = nullptr;
    OkHost* host_ = nullptr;
    ULONGLONG nextAttempt_ = 0;
};

// One client layer as the host sees it: its pixel mapping and the GPU texture holding a copy.
struct LayerCache
{
    uint32_t generation = 0;
    std::string bufferName;
    HANDLE mapping = nullptr;
    const OkPixelHeader* pixels = nullptr;
    size_t viewSize = 0;
    uint32_t uploadedSequence = UINT32_MAX;
    uint32_t width = 0, height = 0;
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11ShaderResourceView> view;

    void Close();
};

// Draws the visible layers into a back buffer. Lives on the game's render thread.
class Renderer
{
public:
    // Composites every visible layer for this process into the swap chain's current back buffer.
    void Draw(IDXGISwapChain* swapChain);

private:
    bool Init(ID3D11Device* device);
    void Release();
    bool Upload(LayerCache& cache, const OkLayer& layer);
    void Capture(ID3D11Texture2D* backBuffer, const D3D11_TEXTURE2D_DESC& desc);

    Directory directory_;
    ID3D11Device* device_ = nullptr;     // not owned; compared to notice a new device
    ComPtr<ID3D11DeviceContext1> context_;
    ComPtr<ID3DDeviceContextState> state_;
    ComPtr<ID3D11VertexShader> vertexShader_;
    ComPtr<ID3D11PixelShader> pixelShader_;
    ComPtr<ID3D11Buffer> constants_;
    ComPtr<ID3D11SamplerState> sampler_;
    ComPtr<ID3D11BlendState> blend_;
    ComPtr<ID3D11RasterizerState> rasterizer_;
    ComPtr<ID3D11DepthStencilState> depth_;
    LayerCache layers_[OK_MAX_LAYERS];
    uint32_t handledCapture_ = 0;
    bool captureArmed_ = false;
    bool failed_ = false;
    bool loggedFirstFrame_ = false;
};
