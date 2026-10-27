// Composites the clients' layers into the game's back buffer, just before the frame is presented.
// All of the game's pipeline state is set aside with SwapDeviceContextState and put back afterwards,
// so nothing we bind can leak into the game's next draw.
#include "host.h"
#include <d3dcompiler.h>
#include <algorithm>
#include <stdio.h>

namespace
{
    const char kShader[] = R"(
cbuffer Layer : register(b0)
{
    float4 rect;     // left, top, right, bottom in clip space
    float opacity;
    float3 unused;
};
Texture2D image : register(t0);
SamplerState pointClamp : register(s0);

struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };

Vertex vs(uint id : SV_VertexID)
{
    float2 t = float2(id & 1, id >> 1);
    Vertex v;
    v.position = float4(lerp(rect.x, rect.z, t.x), lerp(rect.y, rect.w, t.y), 0, 1);
    v.uv = t;
    return v;
}

float4 ps(Vertex v) : SV_Target
{
    return image.Sample(pointClamp, v.uv) * opacity; // premultiplied, so opacity scales colour and alpha alike
}
)";

    struct LayerConstants
    {
        float rect[4];
        float opacity;
        float unused[3];
    };

    DXGI_FORMAT ViewFormat(DXGI_FORMAT format)
    {
        switch (format)
        {
        case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
        case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
        case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
        case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
        default: return format;
        }
    }

    bool SameName(const std::string& cached, const char (&name)[64])
    {
        return strncmp(cached.c_str(), name, sizeof(name)) == 0;
    }
}

bool Renderer::Init(ID3D11Device* device)
{
    ComPtr<ID3D11Device1> device1;
    ComPtr<ID3D11DeviceContext> context;
    device->GetImmediateContext(&context);
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&device1))) || FAILED(context.As(&context_)))
    {
        Log("device has no Direct3D 11.1 interface; overlay disabled");
        return false;
    }

    D3D_FEATURE_LEVEL level = device->GetFeatureLevel();
    UINT stateFlags = (device->GetCreationFlags() & D3D11_CREATE_DEVICE_SINGLETHREADED) ? D3D11_1_CREATE_DEVICE_CONTEXT_STATE_SINGLETHREADED : 0;
    if (FAILED(device1->CreateDeviceContextState(stateFlags, &level, 1, D3D11_SDK_VERSION, __uuidof(ID3D11Device1), nullptr, &state_)))
    {
        Log("CreateDeviceContextState failed; overlay disabled");
        return false;
    }

    ComPtr<ID3DBlob> vs, ps, errors;
    if (FAILED(D3DCompile(kShader, sizeof(kShader) - 1, "overlaykit", nullptr, nullptr, "vs", "vs_4_0", 0, 0, &vs, &errors))
        || FAILED(D3DCompile(kShader, sizeof(kShader) - 1, "overlaykit", nullptr, nullptr, "ps", "ps_4_0", 0, 0, &ps, &errors)))
    {
        Log("shader compile failed: %s", errors ? (const char*)errors->GetBufferPointer() : "?");
        return false;
    }
    HRESULT hr = device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &vertexShader_);
    if (SUCCEEDED(hr)) hr = device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &pixelShader_);

    D3D11_BUFFER_DESC cb{ sizeof(LayerConstants), D3D11_USAGE_DYNAMIC, D3D11_BIND_CONSTANT_BUFFER, D3D11_CPU_ACCESS_WRITE };
    if (SUCCEEDED(hr)) hr = device->CreateBuffer(&cb, nullptr, &constants_);

    D3D11_SAMPLER_DESC sampler{};
    sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT; // layers are drawn 1:1 with the back buffer's pixels
    sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler.MaxLOD = D3D11_FLOAT32_MAX;
    if (SUCCEEDED(hr)) hr = device->CreateSamplerState(&sampler, &sampler_);

    D3D11_BLEND_DESC blend{};
    auto& target = blend.RenderTarget[0];
    target.BlendEnable = TRUE;
    target.SrcBlend = target.SrcBlendAlpha = D3D11_BLEND_ONE;
    target.DestBlend = target.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    target.BlendOp = target.BlendOpAlpha = D3D11_BLEND_OP_ADD;
    target.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    if (SUCCEEDED(hr)) hr = device->CreateBlendState(&blend, &blend_);

    D3D11_RASTERIZER_DESC raster{};
    raster.FillMode = D3D11_FILL_SOLID;
    raster.CullMode = D3D11_CULL_NONE;
    raster.DepthClipEnable = TRUE;
    if (SUCCEEDED(hr)) hr = device->CreateRasterizerState(&raster, &rasterizer_);

    D3D11_DEPTH_STENCIL_DESC depth{};
    if (SUCCEEDED(hr)) hr = device->CreateDepthStencilState(&depth, &depth_);

    if (FAILED(hr))
    {
        Log("creating overlay resources failed (0x%08lx)", hr);
        return false;
    }
    Log("renderer ready (feature level %x, flags %x)", level, device->GetCreationFlags());
    return true;
}

void Renderer::Release()
{
    for (auto& layer : layers_)
        layer.Close();
    context_.Reset();
    state_.Reset();
    vertexShader_.Reset();
    pixelShader_.Reset();
    constants_.Reset();
    sampler_.Reset();
    blend_.Reset();
    rasterizer_.Reset();
    depth_.Reset();
    device_ = nullptr;
}

bool Renderer::Upload(LayerCache& cache, const OkLayer& layer)
{
    if (layer.bufferGeneration != cache.generation || !SameName(cache.bufferName, layer.bufferName))
    {
        cache.Close();
        char name[65] = {};
        memcpy(name, layer.bufferName, sizeof(layer.bufferName));
        cache.generation = layer.bufferGeneration;
        cache.bufferName = name;
        cache.mapping = OpenFileMappingA(FILE_MAP_READ, FALSE, name);
        if (cache.mapping)
            cache.pixels = static_cast<const OkPixelHeader*>(MapViewOfFile(cache.mapping, FILE_MAP_READ, 0, 0, 0));
        MEMORY_BASIC_INFORMATION info{};
        if (cache.pixels && VirtualQuery(cache.pixels, &info, sizeof(info)))
            cache.viewSize = info.RegionSize;
        if (!cache.pixels)
            Log("cannot open layer buffer %s (error %lu)", name, GetLastError());
    }
    if (!cache.pixels)
        return false;

    const OkPixelHeader* header = cache.pixels;
    uint32_t sequence = ReadAcquire(reinterpret_cast<const volatile LONG*>(&header->sequence));
    if ((sequence & 1) || sequence == cache.uploadedSequence)
        return cache.texture != nullptr; // being written, or already on the GPU

    uint32_t width = header->width, height = header->height, stride = header->stride;
    if (width == 0 || height == 0 || width > 16384 || height > 16384 || stride < width * 4
        || sizeof(OkPixelHeader) + (uint64_t)stride * height > cache.viewSize)
        return false;

    if (!cache.texture || width != cache.width || height != cache.height)
    {
        cache.view.Reset();
        cache.texture.Reset();
        D3D11_TEXTURE2D_DESC desc{ width, height, 1, 1, DXGI_FORMAT_B8G8R8A8_UNORM, { 1, 0 },
            D3D11_USAGE_DYNAMIC, D3D11_BIND_SHADER_RESOURCE, D3D11_CPU_ACCESS_WRITE, 0 };
        ComPtr<ID3D11Device> device;
        context_->GetDevice(&device);
        if (FAILED(device->CreateTexture2D(&desc, nullptr, &cache.texture))
            || FAILED(device->CreateShaderResourceView(cache.texture.Get(), nullptr, &cache.view)))
        {
            cache.texture.Reset();
            return false;
        }
        cache.width = width;
        cache.height = height;
    }

    D3D11_MAPPED_SUBRESOURCE mapped;
    if (FAILED(context_->Map(cache.texture.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return false;
    const uint8_t* source = reinterpret_cast<const uint8_t*>(header + 1);
    for (uint32_t y = 0; y < height; y++)
        memcpy(static_cast<uint8_t*>(mapped.pData) + (size_t)y * mapped.RowPitch, source + (size_t)y * stride, (size_t)width * 4);
    context_->Unmap(cache.texture.Get(), 0);

    // If the client wrote while we copied, keep the old sequence so the next frame copies again.
    if (ReadAcquire(reinterpret_cast<const volatile LONG*>(&header->sequence)) == (LONG)sequence)
        cache.uploadedSequence = sequence;
    return true;
}

void Renderer::Draw(IDXGISwapChain* swapChain)
{
    if (failed_)
        return;
    OkDirectory* dir = directory_.Get();
    OkHost* host = dir ? directory_.Host() : nullptr;
    if (!host)
        return;

    ComPtr<ID3D11Device> device;
    if (FAILED(swapChain->GetDevice(IID_PPV_ARGS(&device))))
    {
        if (!loggedFirstFrame_)
            Log("swap chain is not Direct3D 11; nothing drawn");
        loggedFirstFrame_ = true;
        return;
    }
    if (device.Get() != device_)
    {
        Release();
        if (!Init(device.Get()))
        {
            failed_ = true;
            return;
        }
        device_ = device.Get();
    }

    ComPtr<ID3D11Texture2D> backBuffer;
    if (FAILED(swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer))))
        return;
    D3D11_TEXTURE2D_DESC desc;
    backBuffer->GetDesc(&desc);
    host->width = desc.Width;
    host->height = desc.Height;
    host->format = desc.Format;
    host->lastFrameTick = GetTickCount64();
    host->frameCount++;
    if (!loggedFirstFrame_)
    {
        Log("first frame: back buffer %ux%u format %d, %u sample(s)", desc.Width, desc.Height, desc.Format, desc.SampleDesc.Count);
        loggedFirstFrame_ = true;
    }

    ULONGLONG now = GetTickCount64();
    struct Visible { int index; int32_t z; };
    Visible visible[OK_MAX_LAYERS];
    int count = 0;
    for (int i = 0; i < OK_MAX_LAYERS; i++)
    {
        const OkLayer& layer = dir->layers[i];
        if (layer.state != OK_LAYER_CLAIMED)
        {
            if (layers_[i].mapping)
                layers_[i].Close(); // let a released client's mapping go
            continue;
        }
        if (!(layer.flags & OK_LAYER_VISIBLE) || now - layer.heartbeatTick > OK_STALE_MS || layer.opacity <= 0)
            continue;
        if (layer.targetExe[0] && strncmp(layer.targetExe, host->exeName, sizeof(layer.targetExe)) != 0)
            continue;
        if (Upload(layers_[i], layer))
            visible[count++] = { i, layer.zOrder };
    }

    bool capture = dir->captureRequest != handledCapture_;
    if (count == 0 && !capture)
        return;
    std::stable_sort(visible, visible + count, [](const Visible& a, const Visible& b) { return a.z < b.z; });

    if (count > 0)
    {
        D3D11_RENDER_TARGET_VIEW_DESC rtvDesc{};
        rtvDesc.Format = ViewFormat(desc.Format);
        rtvDesc.ViewDimension = desc.SampleDesc.Count > 1 ? D3D11_RTV_DIMENSION_TEXTURE2DMS : D3D11_RTV_DIMENSION_TEXTURE2D;
        ComPtr<ID3D11RenderTargetView> rtv;
        if (FAILED(device->CreateRenderTargetView(backBuffer.Get(), &rtvDesc, &rtv)))
            return;

        ComPtr<ID3DDeviceContextState> gameState;
        context_->SwapDeviceContextState(state_.Get(), &gameState);

        D3D11_VIEWPORT viewport{ 0, 0, (float)desc.Width, (float)desc.Height, 0, 1 };
        ID3D11RenderTargetView* targets[] = { rtv.Get() };
        context_->OMSetRenderTargets(1, targets, nullptr);
        float factor[4] = {};
        context_->OMSetBlendState(blend_.Get(), factor, 0xFFFFFFFF);
        context_->OMSetDepthStencilState(depth_.Get(), 0);
        context_->RSSetViewports(1, &viewport);
        context_->RSSetState(rasterizer_.Get());
        context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        context_->IASetInputLayout(nullptr);
        context_->VSSetShader(vertexShader_.Get(), nullptr, 0);
        context_->PSSetShader(pixelShader_.Get(), nullptr, 0);
        ID3D11Buffer* buffers[] = { constants_.Get() };
        context_->VSSetConstantBuffers(0, 1, buffers);
        context_->PSSetConstantBuffers(0, 1, buffers);
        ID3D11SamplerState* samplers[] = { sampler_.Get() };
        context_->PSSetSamplers(0, 1, samplers);

        for (int i = 0; i < count; i++)
        {
            const OkLayer& layer = dir->layers[visible[i].index];
            const LayerCache& cache = layers_[visible[i].index];
            float w = (float)cache.width, h = (float)cache.height, W = (float)desc.Width, H = (float)desc.Height;
            float x = (float)layer.x, y = (float)layer.y;
            switch (layer.anchor)
            {
            case OK_ANCHOR_TOP_RIGHT: x = W - w - x; break;
            case OK_ANCHOR_BOTTOM_LEFT: y = H - h - y; break;
            case OK_ANCHOR_BOTTOM_RIGHT: x = W - w - x; y = H - h - y; break;
            case OK_ANCHOR_CENTER: x += (W - w) / 2; y += (H - h) / 2; break;
            default: break;
            }

            D3D11_MAPPED_SUBRESOURCE mapped;
            if (FAILED(context_->Map(constants_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
                continue;
            auto* c = static_cast<LayerConstants*>(mapped.pData);
            c->rect[0] = x / W * 2 - 1;
            c->rect[1] = 1 - y / H * 2;
            c->rect[2] = (x + w) / W * 2 - 1;
            c->rect[3] = 1 - (y + h) / H * 2;
            c->opacity = std::clamp(layer.opacity, 0.0f, 1.0f);
            context_->Unmap(constants_.Get(), 0);

            ID3D11ShaderResourceView* views[] = { cache.view.Get() };
            context_->PSSetShaderResources(0, 1, views);
            context_->Draw(4, 0);
        }

        context_->SwapDeviceContextState(gameState.Get(), nullptr);
    }

    if (capture)
    {
        handledCapture_ = dir->captureRequest;
        Capture(backBuffer.Get(), desc);
    }
}

// Saves the back buffer, overlay included, as a BMP, so what the game shows can be checked even in
// exclusive fullscreen, where screen captures see nothing.
void Renderer::Capture(ID3D11Texture2D* backBuffer, const D3D11_TEXTURE2D_DESC& desc)
{
    DXGI_FORMAT format = ViewFormat(desc.Format);
    bool rgba = format == DXGI_FORMAT_R8G8B8A8_UNORM || format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    bool bgra = format == DXGI_FORMAT_B8G8R8A8_UNORM || format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    bool rgb10 = format == DXGI_FORMAT_R10G10B10A2_UNORM;
    if (!rgba && !bgra && !rgb10)
    {
        Log("capture: back buffer format %d not supported", desc.Format);
        return;
    }

    ComPtr<ID3D11Device> device;
    context_->GetDevice(&device);
    ComPtr<ID3D11Texture2D> source = backBuffer;
    if (desc.SampleDesc.Count > 1)
    {
        D3D11_TEXTURE2D_DESC resolved = desc;
        resolved.SampleDesc = { 1, 0 };
        resolved.Format = format;
        resolved.BindFlags = 0;
        resolved.MiscFlags = 0;
        if (FAILED(device->CreateTexture2D(&resolved, nullptr, &source)))
            return;
        context_->ResolveSubresource(source.Get(), 0, backBuffer, 0, format);
    }
    D3D11_TEXTURE2D_DESC stagingDesc = desc;
    stagingDesc.SampleDesc = { 1, 0 };
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.BindFlags = 0;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stagingDesc.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    if (FAILED(device->CreateTexture2D(&stagingDesc, nullptr, &staging)))
        return;
    context_->CopyResource(staging.Get(), source.Get());
    D3D11_MAPPED_SUBRESOURCE mapped;
    if (FAILED(context_->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)))
        return;

    std::vector<uint8_t> pixels((size_t)desc.Width * desc.Height * 4);
    for (UINT y = 0; y < desc.Height; y++)
    {
        const uint8_t* row = static_cast<const uint8_t*>(mapped.pData) + (size_t)y * mapped.RowPitch;
        uint8_t* out = pixels.data() + (size_t)y * desc.Width * 4;
        for (UINT x = 0; x < desc.Width; x++, row += 4, out += 4)
        {
            if (rgb10)
            {
                uint32_t v = *reinterpret_cast<const uint32_t*>(row);
                out[2] = (uint8_t)((v & 0x3FF) >> 2);
                out[1] = (uint8_t)(((v >> 10) & 0x3FF) >> 2);
                out[0] = (uint8_t)(((v >> 20) & 0x3FF) >> 2);
            }
            else
            {
                out[0] = row[rgba ? 2 : 0];
                out[1] = row[1];
                out[2] = row[rgba ? 0 : 2];
            }
            out[3] = 255;
        }
    }
    context_->Unmap(staging.Get(), 0);

    wchar_t path[MAX_PATH];
    GetTempPathW(MAX_PATH, path);
    wcscat_s(path, L"OverlayKit");
    CreateDirectoryW(path, nullptr);
    wchar_t file[MAX_PATH];
    swprintf_s(file, L"%s\\capture-%lu.bmp", path, GetCurrentProcessId());

    BITMAPINFOHEADER info{ sizeof(info), (LONG)desc.Width, -(LONG)desc.Height, 1, 32, BI_RGB };
    BITMAPFILEHEADER head{ 0x4D42, (DWORD)(sizeof(BITMAPFILEHEADER) + sizeof(info) + pixels.size()), 0, 0,
        (DWORD)(sizeof(BITMAPFILEHEADER) + sizeof(info)) };
    HANDLE f = CreateFileW(file, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE)
        return;
    DWORD written;
    WriteFile(f, &head, sizeof(head), &written, nullptr);
    WriteFile(f, &info, sizeof(info), &written, nullptr);
    WriteFile(f, pixels.data(), (DWORD)pixels.size(), &written, nullptr);
    CloseHandle(f);
    Log("capture saved (%ux%u)", desc.Width, desc.Height);
}
