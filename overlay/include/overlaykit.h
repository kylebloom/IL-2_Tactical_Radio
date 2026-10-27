// OverlayKit shared-memory protocol, version 1.
//
// A host (overlaykit64.dll, loaded into a Direct3D 11 game) composites layers that client programs
// publish through named shared memory. Clients never touch the game: they write pixels and placement
// here, and the host draws them into each frame just before it is presented.
//
//   Directory  "Local\OverlayKit.v1.Directory"   (OkDirectory, created by whichever side comes first)
//   Pixels     OkLayer.bufferName                 (OkPixelHeader followed by BGRA pixels, created by the client)
//
// All fields are little-endian, x64 layout. Ticks are GetTickCount64() milliseconds, which every
// process on the machine shares. The C# mirror is overlay/dotnet/OverlayKit.Client/Protocol.cs; keep
// the two in step and bump OK_VERSION (and the mapping names) for any incompatible change.
#pragma once
#include <stdint.h>

#define OK_MAGIC 0x31564B4Fu            // "OKV1"
#define OK_VERSION 1u
#define OK_DIRECTORY_NAME L"Local\\OverlayKit.v1.Directory"
#define OK_MAX_HOSTS 8
#define OK_MAX_LAYERS 16

// Who may open the mappings: SYSTEM, administrators and interactive users, so an elevated client and a
// non-elevated game (or the other way round) can share them.
#define OK_MAPPING_SDDL L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;IU)"

// A layer whose client has not refreshed heartbeatTick for this long is hidden, and may be reclaimed.
#define OK_STALE_MS 3000u

enum OkAnchor : uint32_t
{
    OK_ANCHOR_TOP_LEFT = 0,
    OK_ANCHOR_TOP_RIGHT = 1,
    OK_ANCHOR_BOTTOM_LEFT = 2,
    OK_ANCHOR_BOTTOM_RIGHT = 3,
    OK_ANCHOR_CENTER = 4,
};

enum OkLayerFlags : uint32_t
{
    OK_LAYER_VISIBLE = 1u << 0,
};

enum OkLayerState : uint32_t
{
    OK_LAYER_FREE = 0,
    OK_LAYER_CLAIMED = 1,   // a client owns it (claim with a compare-exchange from FREE)
};

// One per game process the host runs in; written by the host every frame.
struct OkHost                       // 64 bytes
{
    uint32_t pid;                   // 0 = free slot (claim with a compare-exchange)
    uint32_t width;                 // back buffer size in pixels
    uint32_t height;
    uint32_t format;                // DXGI_FORMAT of the back buffer
    uint64_t lastFrameTick;
    uint64_t frameCount;
    char exeName[32];               // lower-case file name, e.g. "il-2.exe"
};

// One per client surface.
struct OkLayer                      // 256 bytes
{
    uint32_t state;                 // OkLayerState
    uint32_t ownerPid;
    uint64_t heartbeatTick;         // the client refreshes this at least once a second
    uint32_t reserved0;
    uint32_t flags;                 // OkLayerFlags
    int32_t x;                      // offset from the anchor, in back-buffer pixels, towards the screen's inside
    int32_t y;
    uint32_t anchor;                // OkAnchor
    float opacity;                  // 0..1, multiplies the pixels' own alpha
    int32_t zOrder;                 // higher draws later (on top)
    uint32_t bufferGeneration;      // bumped whenever bufferName changes, so the host reopens it
    char owner[64];                 // display name, e.g. "IL-2 Tactical Radio"
    char targetExe[32];             // lower-case exe name to draw in, or empty for any host
    char bufferName[64];            // the pixel mapping, e.g. "Local\OverlayKit.v1.Px.1234.0.1"
    uint8_t reserved[48];
};

struct OkDirectory                  // 64 + 8*64 + 16*256 = 4672 bytes
{
    uint32_t magic;                 // OK_MAGIC once initialised
    uint32_t version;               // OK_VERSION
    uint32_t hostCapacity;          // OK_MAX_HOSTS
    uint32_t layerCapacity;         // OK_MAX_LAYERS
    uint32_t captureRequest;        // bump to ask every host to save its next frame to %TEMP%\OverlayKit\capture-<pid>.bmp
    uint32_t initLock;              // 0 until magic is written; 1 while initialising
    uint8_t reserved[40];
    OkHost hosts[OK_MAX_HOSTS];
    OkLayer layers[OK_MAX_LAYERS];
};

// Start of a pixel mapping. The client writes it as a seqlock: make sequence odd, write the pixels and
// size, then make it even. The host copies only when sequence is even and unchanged across the copy.
struct OkPixelHeader                // 64 bytes, pixels follow
{
    uint32_t sequence;
    uint32_t width;
    uint32_t height;
    uint32_t stride;                // bytes per row, at least width * 4
    uint64_t capacity;              // pixel bytes available after the header
    uint8_t reserved[40];
};

// Pixels are 32-bit premultiplied BGRA, top row first (GDI+ Format32bppPArgb, Direct2D's default).

#ifdef __cplusplus
static_assert(sizeof(OkHost) == 64, "OkHost layout");
static_assert(sizeof(OkLayer) == 256, "OkLayer layout");
static_assert(sizeof(OkDirectory) == 4672, "OkDirectory layout");
static_assert(sizeof(OkPixelHeader) == 64, "OkPixelHeader layout");
#endif
