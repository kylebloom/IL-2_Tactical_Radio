using System.Runtime.InteropServices;

namespace OverlayKit;

/// <summary>
/// Mirror of overlay/include/overlaykit.h (protocol version 1). Keep the two in step; the sizes are
/// checked at start-up by <see cref="Protocol.CheckLayout"/>.
/// </summary>
internal static class Protocol
{
    public const uint Magic = 0x31564B4F; // "OKV1"
    public const uint Version = 1;
    public const string DirectoryName = @"Local\OverlayKit.v1.Directory";
    public const int MaxHosts = 8;
    public const int MaxLayers = 16;
    public const string MappingSddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;IU)";
    public const ulong StaleMs = 3000;

    public const uint LayerFree = 0, LayerClaimed = 1;
    public const uint FlagVisible = 1;

    public static void CheckLayout()
    {
        if (Marshal.SizeOf<HostEntry>() != 64 || Marshal.SizeOf<LayerEntry>() != 256
            || Marshal.SizeOf<DirectoryBlock>() != 4672 || Marshal.SizeOf<PixelHeader>() != 64)
            throw new InvalidOperationException("OverlayKit protocol structs do not match overlaykit.h");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HostEntry
{
    public uint Pid;
    public uint Width;
    public uint Height;
    public uint Format;
    public ulong LastFrameTick;
    public ulong FrameCount;
    public fixed byte ExeName[32];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LayerEntry
{
    public uint State;
    public uint OwnerPid;
    public ulong HeartbeatTick;
    public uint Reserved0;
    public uint Flags;
    public int X;
    public int Y;
    public uint Anchor;
    public float Opacity;
    public int ZOrder;
    public uint BufferGeneration;
    public fixed byte Owner[64];
    public fixed byte TargetExe[32];
    public fixed byte BufferName[64];
    public fixed byte Reserved[48];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DirectoryBlock
{
    public uint Magic;
    public uint Version;
    public uint HostCapacity;
    public uint LayerCapacity;
    public uint CaptureRequest;
    public uint InitLock;
    public fixed byte Reserved[40];
    public fixed byte Hosts[Protocol.MaxHosts * 64];
    public fixed byte Layers[Protocol.MaxLayers * 256];

    public static HostEntry* Host(DirectoryBlock* dir, int i) => (HostEntry*)((byte*)dir + 64) + i;

    public static LayerEntry* Layer(DirectoryBlock* dir, int i) => (LayerEntry*)((byte*)dir + 64 + Protocol.MaxHosts * 64) + i;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PixelHeader
{
    public uint Sequence;
    public uint Width;
    public uint Height;
    public uint Stride;
    public ulong Capacity;
    public fixed byte Reserved[40];
}

internal static unsafe class Ascii
{
    /// <summary>Writes a NUL-terminated ASCII string into a fixed field, truncating to fit.</summary>
    public static void Write(byte* field, int size, string value)
    {
        int n = Math.Min(value.Length, size - 1);
        for (int i = 0; i < n; i++)
            field[i] = value[i] < 128 ? (byte)value[i] : (byte)'?';
        for (int i = n; i < size; i++)
            field[i] = 0;
    }

    public static string Read(byte* field, int size)
    {
        int n = 0;
        while (n < size && field[n] != 0)
            n++;
        return new string((sbyte*)field, 0, n);
    }
}
