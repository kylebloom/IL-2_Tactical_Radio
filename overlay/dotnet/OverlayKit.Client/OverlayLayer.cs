namespace OverlayKit;

/// <summary>
/// One surface drawn into a game's frames by the OverlayKit host: a premultiplied BGRA bitmap placed
/// against a corner of the frame, drawn 1:1 with the game's pixels. Render it at the host's back-buffer
/// size (<see cref="Overlay.Hosts"/>) for crisp text. The layer stays on screen while this object lives:
/// a background heartbeat tells the host it is still wanted, so a crashed client's layer disappears
/// within a few seconds. Thread-safe.
/// </summary>
public sealed unsafe class OverlayLayer : IDisposable
{
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromMilliseconds(500);
    private const long ReclaimAfterMs = 10_000;

    private readonly object _gate = new();
    private readonly int _slot;
    private readonly LayerEntry* _entry;
    private readonly Timer _heartbeat;
    private SharedSection? _pixels;
    private uint _generation;
    private bool _disposed;

    /// <param name="owner">Shown in diagnostics, e.g. "IL-2 Tactical Radio".</param>
    /// <param name="targetExe">Only hosts in a process with this exe name draw the layer (e.g. "Il-2.exe"); null for any.</param>
    public OverlayLayer(string owner, string? targetExe = null)
    {
        var dir = Overlay.Directory;
        int pid = Environment.ProcessId;
        _slot = Claim(dir, pid);
        if (_slot < 0)
            throw new InvalidOperationException($"All {Protocol.MaxLayers} OverlayKit layers are in use");
        _entry = DirectoryBlock.Layer(dir, _slot);
        _entry->Flags = 0;
        _entry->X = _entry->Y = 0;
        _entry->Anchor = (uint)OverlayAnchor.TopLeft;
        _entry->Opacity = 1;
        _entry->ZOrder = 0;
        _entry->BufferName[0] = 0;
        Ascii.Write(_entry->Owner, 64, owner);
        Ascii.Write(_entry->TargetExe, 32, (targetExe ?? "").ToLowerInvariant());
        _generation = _entry->BufferGeneration;
        Beat();
        _heartbeat = new Timer(_ => Beat(), null, HeartbeatPeriod, HeartbeatPeriod);
    }

    /// <summary>Finds a free slot, or one whose owner died or stopped beating, and takes it.</summary>
    private static int Claim(DirectoryBlock* dir, int pid)
    {
        ulong now = Native.GetTickCount64();
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < Protocol.MaxLayers; i++)
            {
                var e = DirectoryBlock.Layer(dir, i);
                if (pass == 0)
                {
                    if (Interlocked.CompareExchange(ref *(int*)&e->State, (int)Protocol.LayerClaimed, (int)Protocol.LayerFree) != Protocol.LayerFree)
                        continue;
                }
                else
                {
                    uint owner = e->OwnerPid;
                    bool abandoned = now - Math.Min(now, e->HeartbeatTick) > ReclaimAfterMs || !Overlay.Running((int)owner);
                    if (!abandoned || Interlocked.CompareExchange(ref *(int*)&e->OwnerPid, pid, (int)owner) != owner)
                        continue;
                }
                e->OwnerPid = (uint)pid;
                e->HeartbeatTick = now;
                return i;
            }
        }
        return -1;
    }

    /// <summary>Which of the shared slots this layer has (for diagnostics).</summary>
    public int Slot => _slot;

    /// <summary>Shows or hides the layer without dropping its pixels.</summary>
    public bool Visible
    {
        get => (_entry->Flags & Protocol.FlagVisible) != 0;
        set { lock (_gate) if (!_disposed) _entry->Flags = value ? _entry->Flags | Protocol.FlagVisible : _entry->Flags & ~Protocol.FlagVisible; }
    }

    /// <summary>0..1, applied on top of the pixels' own alpha.</summary>
    public float Opacity
    {
        get => _entry->Opacity;
        set { lock (_gate) if (!_disposed) _entry->Opacity = Math.Clamp(value, 0f, 1f); }
    }

    /// <summary>Higher layers draw over lower ones.</summary>
    public int ZOrder
    {
        get => _entry->ZOrder;
        set { lock (_gate) if (!_disposed) _entry->ZOrder = value; }
    }

    /// <summary>
    /// Places the layer <paramref name="x"/>, <paramref name="y"/> back-buffer pixels in from the
    /// <paramref name="anchor"/> corner of the frame.
    /// </summary>
    public void Place(OverlayAnchor anchor, int x, int y)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _entry->Anchor = (uint)anchor;
            _entry->X = x;
            _entry->Y = y;
        }
    }

    /// <summary>
    /// Publishes a new image: premultiplied BGRA, top row first (GDI+ Format32bppPArgb). The host
    /// uploads it on its next frame.
    /// </summary>
    public void Update(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        if (pixels.Length < (long)stride * (height - 1) + width * 4)
            throw new ArgumentException("pixels is smaller than height rows of stride bytes", nameof(pixels));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long needed = (long)width * 4 * height;
            bool newBuffer = _pixels is null || ((PixelHeader*)_pixels.Pointer)->Capacity < (ulong)needed;
            if (newBuffer)
            {
                long capacity = (needed + 0xFFFF) & ~0xFFFFL;
                _generation++;
                string name = $@"Local\OverlayKit.v1.Px.{Environment.ProcessId}.{_slot}.{_generation}";
                var section = SharedSection.CreateOrOpen(name, sizeof(PixelHeader) + capacity);
                ((PixelHeader*)section.Pointer)->Capacity = (ulong)capacity;
                Write(section, pixels, width, height, stride);
                Ascii.Write(_entry->BufferName, 64, name);
                Thread.MemoryBarrier();
                Volatile.Write(ref _entry->BufferGeneration, _generation);
                _pixels?.Dispose(); // the host keeps its own view until it switches
                _pixels = section;
            }
            else
            {
                Write(_pixels!, pixels, width, height, stride);
            }
        }
    }

    /// <summary>Seqlock write: odd sequence while the pixels change, even once they are whole.</summary>
    private static void Write(SharedSection section, ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var header = (PixelHeader*)section.Pointer;
        Volatile.Write(ref header->Sequence, header->Sequence + 1);
        Thread.MemoryBarrier();
        header->Width = (uint)width;
        header->Height = (uint)height;
        header->Stride = (uint)width * 4;
        var target = new Span<byte>(section.Pointer + sizeof(PixelHeader), width * 4 * height);
        for (int y = 0; y < height; y++)
            pixels.Slice(y * stride, width * 4).CopyTo(target.Slice(y * width * 4, width * 4));
        Thread.MemoryBarrier();
        Volatile.Write(ref header->Sequence, header->Sequence + 1);
    }

    private void Beat()
    {
        lock (_gate)
            if (!_disposed)
                _entry->HeartbeatTick = Native.GetTickCount64();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _heartbeat.Dispose();
            _entry->Flags = 0;
            _entry->BufferName[0] = 0;
            _entry->OwnerPid = 0;
            Thread.MemoryBarrier();
            Volatile.Write(ref _entry->State, Protocol.LayerFree);
            _pixels?.Dispose();
            _pixels = null;
        }
    }
}
