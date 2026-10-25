using System.IO.MemoryMappedFiles;
using Il2Radio.Core.Osd;
using Il2Radio.Core.Radio;

namespace Il2Radio.Diag;

/// <summary>
/// Shows the radio log in RivaTuner Statistics Server's on-screen display through its documented
/// shared memory (see <see cref="RtssOsd"/>). RTSS draws it into IL-2's own frames, so it is visible
/// in exclusive fullscreen; this process only writes text into RTSS's memory, never into the game.
/// The mapping is opened per update, as in the RTSS SDK sample, so starting or restarting RTSS
/// while this runs is fine.
/// </summary>
internal sealed class RtssChat : IChatSink
{
    public const string OwnerName = "IL-2 Tactical Radio";

    private readonly ChatOptions _options;
    private readonly ChatTimeline _timeline;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private string? _written;           // text RTSS currently holds from us; null = unknown, rewrite
    private DateTime _nextRefreshUtc;
    private bool _disposed;

    public string? Status { get; private set; }

    public RtssChat(ChatOptions options)
    {
        _options = options;
        _timeline = ChatSinks.Timeline(options);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        Update();
        _timer = new System.Threading.Timer(_ => Update(), null, 100, 100);
    }

    public void Post(IReadOnlyList<ChatLine> lines, bool syncToVoice)
    {
        _timeline.Post(lines, DateTime.UtcNow, syncToVoice);
        ThreadPool.QueueUserWorkItem(_ => Update()); // show the call now, not on the next 100 ms tick
    }

    public void Spoken(string text) => _timeline.Spoken(text, DateTime.UtcNow);

    public void ToggleMenu(IReadOnlyList<ChatLine> menu)
    {
        _timeline.ToggleMenu(menu, DateTime.UtcNow);
        ThreadPool.QueueUserWorkItem(_ => Update());
    }

    public void Scroll(int pages)
    {
        _timeline.Scroll(pages, DateTime.UtcNow);
        ThreadPool.QueueUserWorkItem(_ => Update());
    }

    private void Update()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var now = DateTime.UtcNow;
            var visible = _timeline.Snapshot(now);
            try
            {
                using var view = RtssView.Open();
                if (view is null)
                {
                    SetStatus("RTSS: not running. Start RivaTuner Statistics Server; the radio text appears once it is up.");
                    _written = null;
                    return;
                }

                var memory = view.Memory;
                uint version = RtssOsd.Version(memory) ?? 0;
                bool tags = RtssOsd.SupportsFormatTags(version);
                const int positionTagRoom = 32;
                var fit = tags ? Fit(memory) : null; // without tags the text sits at RTSS's own position, which we can't know
                string text = RtssOsd.Format(visible, tags, RtssOsd.TextCapacity(version) - positionTagRoom,
                    fit?.Columns ?? _options.RtssColumns, maxRows: fit?.Rows ?? int.MaxValue);
                if (tags && text.Length > 0 && Position(memory, text) is var (x, y))
                    text = $"<P={x},{y}>" + text;

                // Rewrite on change, and every 2 s in case RTSS restarted and dropped our slot.
                if (text == _written && now < _nextRefreshUtc)
                    return;
                _nextRefreshUtc = now.AddSeconds(2);

                if (text.Length == 0)
                {
                    RtssOsd.Release(memory, OwnerName); // an empty slot would still take an OSD line
                    _written = "";
                    SetStatus($"RTSS: connected (shared memory v{version >> 16}.{version & 0xFFFF}{(tags ? "" : ", no colour tags")})");
                    return;
                }

                var result = RtssOsd.Write(memory, OwnerName, text);
                _written = result == RtssWriteResult.Written ? text : null;
                SetStatus(result switch
                {
                    RtssWriteResult.Written => $"RTSS: connected (shared memory v{version >> 16}.{version & 0xFFFF}{(tags ? "" : ", no colour tags")})",
                    RtssWriteResult.NoFreeSlot => $"RTSS: no free OSD slot (in use by {string.Join(", ", RtssOsd.Owners(memory))})",
                    RtssWriteResult.NotRunning => "RTSS: shared memory not ready (RTSS starting or closing)",
                    _ => Status,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus($"RTSS: cannot open shared memory ({ex.Message})");
                _written = null;
            }
        }
    }

    private void SetStatus(string? status) =>
        Status = status is null ? null : string.Join("; ", new[] { status, _placementInfo, _fitInfo }.Where(s => s is not null));

    private string? _placementInfo, _fitInfo;

    private RtssPlacement Placement => _options.RtssPlacement ?? new RtssPlacement();

    /// <summary>The game's resolution and the OSD zoom RTSS reports for Il-2.exe (else --rtss-zoom, else 1).</summary>
    private ((int Width, int Height) Screen, int Zoom, bool ZoomFromProfile) Geometry(Span<byte> memory)
    {
        var screen = _options.RtssScreen ?? (System.Windows.Forms.Screen.PrimaryScreen?.Bounds is { } b ? (b.Width, b.Height) : (1920, 1080));
        int? reported = RtssOsd.AppOsdZoom(memory, "Il-2.exe");
        return (screen, _options.RtssZoom ?? reported ?? 1, _options.RtssZoom is null && reported is not null);
    }

    /// <summary>Columns and rows the text may use so that it stays out of IL-2's message line and subtitles.</summary>
    private RtssFit Fit(Span<byte> memory)
    {
        var (screen, zoom, _) = Geometry(memory);
        var fit = Placement.Fit(screen.Width, screen.Height, zoom, _options.RtssCell ?? ChatOptions.DefaultRtssCell,
            _options.RtssColumns, _options.RtssRows, _options.Areas);
        _fitInfo = fit switch
        {
            { Overlapping.Count: > 0 } => $"overlaps {string.Join(", ", fit.Overlapping.Select(a => a.Name))}: choose another --rtss-pos",
            { Avoided.Count: > 0 } => $"limited to {fit.Columns} columns x {fit.Rows} rows to keep clear of {string.Join(", ", fit.Avoided.Select(a => a.Name))}",
            _ => null,
        };
        return fit;
    }

    /// <summary>The &lt;P=x,y&gt; position for this text: the chosen preset converted with the game's resolution and OSD zoom.</summary>
    private (int X, int Y)? Position(Span<byte> memory, string text)
    {
        var placement = Placement;
        var (screen, zoom, fromProfile) = Geometry(memory);
        var (lines, columns) = RtssOsd.VisibleSize(text);
        var position = placement.Position(screen.Width, screen.Height, zoom, lines, columns, _options.RtssCell ?? ChatOptions.DefaultRtssCell);
        _placementInfo = position is var (x, y)
            ? $"text at <P={x},{y}> ({placement.Anchor}, margin {placement.Margin} px, {screen.Width}x{screen.Height}, zoom {zoom}{(fromProfile ? " from IL-2's RTSS profile" : "")})"
            : "text at RTSS's own OSD position";
        return position;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _timer?.Dispose();
        try
        {
            using var view = RtssView.Open();
            if (view is not null)
                RtssOsd.Release(view.Memory, OwnerName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A read-write view of RTSS's named mapping, exposed as one span.</summary>
    private sealed unsafe class RtssView : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly byte* _pointer;
        private readonly int _length;

        private RtssView(MemoryMappedFile file)
        {
            _file = file;
            _accessor = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _pointer = pointer + _accessor.PointerOffset;
            _length = (int)Math.Min(_accessor.Capacity, int.MaxValue);
        }

        public Span<byte> Memory => new(_pointer, _length);

        /// <summary>Null when RTSS (and so its mapping) is not running.</summary>
        public static RtssView? Open()
        {
            try
            {
                return new RtssView(MemoryMappedFile.OpenExisting(RtssOsd.MappingName, MemoryMappedFileRights.ReadWrite));
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _file.Dispose();
        }
    }
}
