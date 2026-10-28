using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Il2Radio.Core.Radio;
using OverlayKit;

namespace Il2Radio.Windows;

/// <summary>
/// Shows the radio log, the radio menu and the kneeboard inside IL-2's own frames through OverlayKit, each as a layer of
/// its own. The OverlayKit host DLL is loaded into Il-2.exe once it runs (the player opts in by choosing this output); it
/// only draws the bitmaps this sink publishes, rendered at the game's back-buffer resolution (the log exactly as the
/// window overlay renders it). It never reads or writes the game's memory.
/// </summary>
internal sealed class InGameChat : IChatSink
{
    public const string OwnerName = "IL-2 Tactical Radio";
    private static readonly TimeSpan LoadRetry = TimeSpan.FromSeconds(3);

    private readonly ChatOptions _options;
    private readonly ChatTimeline _timeline;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private OverlayLayer? _layer;
    private OverlayLayer? _menuLayer;
    private volatile RadioMenuView? _menuView;
    private volatile bool _menuDirty;
    private OverlayLayer? _kneeboardLayer;
    private volatile KneeboardPicture? _kneeboard;
    private string _kneeboardDrawn = "";
    private Bitmap? _bitmap;
    private Font? _font;
    private (int Width, int Height) _screen;
    private int _loadedPid;
    private DateTime _nextLoadUtc;
    private string _lastFrame = "";
    private bool _disposed;

    public string? Status { get; private set; }

    private string GameExe => _options.InGameExe;

    public InGameChat(ChatOptions options)
    {
        _options = options;
        _timeline = ChatSinks.Timeline(options);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        _timer = new System.Threading.Timer(_ => Update(), null, 0, 50); // a pressed key shows its call line within ~50 ms
    }

    public void Post(IReadOnlyList<ChatLine> lines, bool syncToVoice) => _timeline.Post(lines, DateTime.UtcNow, syncToVoice);

    public void Spoken(string text) => _timeline.Spoken(text, DateTime.UtcNow);

    /// <summary>The menu is a panel of its own above the radio log, so the log stays readable while choosing.</summary>
    public void ShowMenu(RadioMenuView? menu)
    {
        _menuView = menu;
        _menuDirty = true;
    }

    /// <summary>The kneeboard is a third layer, pinned where its placement says; it is redrawn only when the picture changes.</summary>
    public void ShowKneeboard(KneeboardPicture? picture) => _kneeboard = picture;

    public void Scroll(int pages) => _timeline.Scroll(pages, DateTime.UtcNow);

    public void BindMissionClock(MissionClock clock) => _timeline.MissionTime = clock;

    public void FreezeMissionTime() => _timeline.FreezeMissionTime();

    private void Update()
    {
        if (!Monitor.TryEnter(_gate))
            return; // the previous tick is still drawing
        try
        {
            if (_disposed)
                return;
            var host = EnsureHost();
            if (host is null)
                return;
            if ((host.Width, host.Height) != _screen)
                Resize(host.Width, host.Height);
            if (_menuDirty)
                PresentMenu();
            PresentKneeboard();

            var lines = _timeline.Snapshot(DateTime.UtcNow).Select(v => (v.Line, v.Alpha)).ToList();
            string frame = string.Join("\n", lines.Select(l => $"{l.Line.Speaker}|{l.Line.Text}|{l.Alpha:F2}"));
            if (frame == _lastFrame)
                return;
            _lastFrame = frame;
            Present(lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or ExternalException or ArgumentException)
        {
            Status = $"in-game text: {ex.Message}";
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Loads the host into IL-2 when it is running and returns it once it reports frames.</summary>
    private OverlayHost? EnsureHost()
    {
        var host = Overlay.Host(GameExe);
        if (host is not null)
        {
            Status = $"in-game text: drawing in {GameExe} at {host.Width}x{host.Height}";
            return host;
        }

        var now = DateTime.UtcNow;
        if (now < _nextLoadUtc)
            return null;
        _nextLoadUtc = now + LoadRetry;

        var game = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe)).FirstOrDefault();
        if (game is null)
        {
            Status = $"in-game text: waiting for {GameExe} to start";
            return null;
        }
        if (game.Id != _loadedPid)
        {
            OverlayInjector.EnsureLoaded(game);
            _loadedPid = game.Id;
            Status = $"in-game text: overlay loaded into {GameExe}, waiting for its first frame";
        }
        else
        {
            Status = $"in-game text: overlay loaded, but IL-2 is not drawing frames (paused or minimised?); log: {Overlay.LogPath(game.Id)}";
        }
        return null;
    }

    /// <summary>A bitmap and font for this back-buffer size: the overlay's screen fractions and 1080-line font size, scaled.</summary>
    private void Resize(int width, int height)
    {
        var rect = ChatOverlay.OverlayRect(_options).Rect;
        _bitmap?.Dispose();
        _font?.Dispose();
        _bitmap = new Bitmap(Math.Max(1, (int)(width * rect.Width)), Math.Max(1, (int)(height * rect.Height)), PixelFormat.Format32bppPArgb);
        _font = new Font(Il2ChatStyle.FontFamily, _options.FontPixels * height / 1080f, FontStyle.Regular, GraphicsUnit.Pixel);
        _screen = (width, height);
        _lastFrame = "\0"; // redraw at the new size
        _menuDirty = true;
        _kneeboardDrawn = "";

        _layer ??= new OverlayLayer(OwnerName, GameExe);
        _layer.Place(OverlayAnchor.TopLeft, (int)(width * rect.Left), (int)(height * rect.Top));
    }

    /// <summary>Draws the menu panel with its bottom just above the radio log's area (or hides it).</summary>
    private void PresentMenu()
    {
        _menuDirty = false;
        if (_font is null)
            return;
        _menuLayer ??= new OverlayLayer(OwnerName + " menu", GameExe) { ZOrder = 1 };
        if (_menuView is not { } view)
        {
            _menuLayer.Visible = false;
            return;
        }

        using var panel = RadioMenuPanel.Render(view, _font);
        var rect = ChatOverlay.OverlayRect(_options).Rect;
        int gap = (int)(_screen.Height * 0.01);
        int x = (int)(_screen.Width * rect.Left);
        int y = Math.Max(gap, (int)(_screen.Height * rect.Top) - panel.Height - gap);
        Publish(_menuLayer, panel);
        _menuLayer.Place(OverlayAnchor.TopLeft, x, y);
        _menuLayer.Visible = true;
    }

    /// <summary>Draws the kneeboard when its picture has changed (or hides it), at its placement on this back buffer.</summary>
    private void PresentKneeboard()
    {
        var picture = _kneeboard;
        string signature = picture is null || picture.View == KneeboardView.Hidden ? "hidden" : picture.Signature;
        if (signature == _kneeboardDrawn)
            return;
        _kneeboardDrawn = signature;
        _kneeboardLayer ??= new OverlayLayer(OwnerName + " kneeboard", GameExe) { ZOrder = 2 };
        using var sheet = picture is null ? null : KneeboardPanel.Render(picture, _screen.Height);
        if (sheet is null)
        {
            _kneeboardLayer.Visible = false;
            return;
        }
        var (x, y) = _options.KneeboardAt.Place(_screen, (sheet.Width, sheet.Height));
        Publish(_kneeboardLayer, sheet);
        _kneeboardLayer.Place(OverlayAnchor.TopLeft, x, y);
        _kneeboardLayer.Visible = true;
    }

    private static void Publish(OverlayLayer layer, Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            unsafe
            {
                layer.Update(new ReadOnlySpan<byte>((void*)data.Scan0, data.Stride * data.Height), data.Width, data.Height, data.Stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void Present(List<(ChatLine Line, float Alpha)> lines)
    {
        if (_layer is null || _bitmap is null || _font is null)
            return;
        if (lines.Count == 0)
        {
            _layer.Visible = false;
            return;
        }
        using (var g = Graphics.FromImage(_bitmap))
        {
            g.Clear(Color.Transparent);
            ChatOverlay.DrawLines(g, _font, lines, _bitmap.Size);
        }
        Publish(_layer, _bitmap);
        _layer.Visible = true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _timer.Dispose();
        _layer?.Dispose(); // the host stops drawing it at once; the DLL itself stays in IL-2 until it exits
        _menuLayer?.Dispose();
        _kneeboardLayer?.Dispose();
        _bitmap?.Dispose();
        _font?.Dispose();
    }
}
