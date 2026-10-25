using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Il2Radio.Core.Osd;
using Il2Radio.Core.Radio;

namespace Il2Radio.Windows;

/// <summary>
/// Shows the radio exchange as text in the style of IL-2's chat. It is a separate window drawn by
/// this process; nothing is written into the game. In overlay mode it is a per-pixel-alpha layered
/// window that ignores the mouse, never takes focus and stays above the game. IL-2 in fullscreen
/// hides it whenever the game has focus (verified), so use <see cref="RtssChat"/> there.
/// </summary>
internal sealed class ChatOverlay : IChatSink
{
    private readonly ChatOptions _options;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ChatTimeline _timeline;
    private ChatForm? _form;

    public ChatOverlay(ChatOptions options)
    {
        _options = options;
        _timeline = ChatSinks.Timeline(options);
        _thread = new Thread(Run) { IsBackground = true, Name = "radio chat" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Post(IReadOnlyList<ChatLine> lines, bool syncToVoice) => _timeline.Post(lines, DateTime.UtcNow, syncToVoice);

    public void Spoken(string text) => _timeline.Spoken(text, DateTime.UtcNow);

    public void ToggleMenu(IReadOnlyList<ChatLine> menu) => _timeline.ToggleMenu(menu, DateTime.UtcNow);

    public void Scroll(int pages) => _timeline.Scroll(pages, DateTime.UtcNow);

    /// <summary>Says when the window was narrowed to stay out of IL-2's own text.</summary>
    public string? Status { get; private set; }

    public void Dispose()
    {
        try { _form?.BeginInvoke(() => _form.Close()); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        _form = new ChatForm(this, _options);
        _form.HandleCreated += (_, _) => _ready.Set();
        Application.Run(_form);
        _ready.Set();
    }

    private List<(ChatLine Line, float Alpha)> Snapshot(DateTime now) =>
        _timeline.Snapshot(now).Select(v => (v.Line, v.Alpha)).ToList();

    /// <summary>The overlay's area in screen fractions, narrowed to keep clear of IL-2's own text.</summary>
    private static (ScreenRect Rect, IReadOnlyList<Il2TextArea> Avoided) OverlayRect(ChatOptions options) =>
        Il2ScreenAreas.KeepClear(ScreenRect.FromSize(options.Left, options.Top, options.Width, options.Height), options.Areas);

    /// <summary>
    /// Renders the overlay's live view of <paramref name="exchange"/> (oldest lines fading), the
    /// command menu and a page of the radio log to PNG files, over the terrain grey of the reference
    /// screenshot, for checking the look without IL-2. Returns the files written.
    /// </summary>
    public static IReadOnlyList<string> SaveFrames(ChatOptions options, IReadOnlyList<ChatLine> exchange, string path, (int Width, int Height) screen)
    {
        var rect = OverlayRect(options).Rect;
        var size = new Size((int)(screen.Width * rect.Width), (int)(screen.Height * rect.Height));
        using var font = new Font(Il2ChatStyle.FontFamily, options.FontPixels * screen.Height / 1080f, FontStyle.Regular, GraphicsUnit.Pixel);
        var timeline = ChatSinks.Timeline(options);
        var start = DateTime.UtcNow;
        timeline.Post(exchange, start);
        var at = start.AddSeconds(options.HoldSeconds + 1.5); // the first lines half faded

        string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path));
        var written = new List<string>();
        void Save(string suffix)
        {
            using var bitmap = new Bitmap(size.Width, size.Height);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(0x78, 0x78, 0x6E));
                ChatForm.Draw(g, font, timeline.Snapshot(at).Select(v => (v.Line, v.Alpha)).ToList(), size);
            }
            string file = stem + suffix + ".png";
            bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            written.Add(file);
        }

        Save("");
        timeline.ToggleMenu(options.KeyMap.Menu, at);
        Save("-menu");
        timeline.Scroll(+1, at);
        Save("-log");
        return written;
    }

    private sealed class ChatForm : Form
    {
        private readonly ChatOverlay _owner;
        private readonly ChatOptions _options;
        private readonly bool _overlay;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 }; // a pressed key shows its call line within ~50 ms
        private readonly Font _font;
        private string _lastFrame = "";
        private int _ticks;

        public ChatForm(ChatOverlay owner, ChatOptions options)
        {
            _owner = owner;
            _options = options;
            _overlay = options.Mode == ChatMode.Overlay;
            var screens = System.Windows.Forms.Screen.AllScreens;
            var area = screens[Math.Clamp(options.Screen, 0, screens.Length - 1)].Bounds;
            float pixels = options.FontPixels * area.Height / 1080f; // FontPixels is for a 1080-line screen
            _font = new Font(Il2ChatStyle.FontFamily, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
            Text = "IL-2 radio";
            StartPosition = FormStartPosition.Manual;
            var rect = ScreenRect.FromSize(options.Left, options.Top, options.Width, options.Height);
            if (_overlay)
            {
                // Over the game only: a window on another screen can't collide with IL-2's text.
                (rect, var avoided) = OverlayRect(options);
                if (avoided.Count > 0)
                    owner.Status = $"chat overlay: fitted to {rect.Left:F3},{rect.Top:F3},{rect.Width:F3},{rect.Height:F3} to keep clear of {string.Join(", ", avoided.Select(a => a.Name))} (--keep-clear off to disable)";
            }
            Bounds = new Rectangle(
                area.Left + (int)(area.Width * rect.Left), area.Top + (int)(area.Height * rect.Top),
                (int)(area.Width * rect.Width), (int)(area.Height * rect.Height));
            ShowInTaskbar = !_overlay;
            TopMost = _overlay;
            if (_overlay)
            {
                FormBorderStyle = FormBorderStyle.None;
            }
            else
            {
                BackColor = Color.FromArgb(24, 26, 28);
                DoubleBuffered = true;
            }

            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (_overlay)
                    cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_overlay)
                Present([]);
        }

        private void Tick()
        {
            var lines = _owner.Snapshot(DateTime.UtcNow);
            // Games may push themselves above other topmost windows; reassert every couple of seconds.
            if (_overlay && ++_ticks % 40 == 0)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            string frame = string.Join("\n", lines.Select(l => $"{l.Line.Speaker}|{l.Line.Text}|{l.Alpha:F2}"));
            if (frame == _lastFrame)
                return;
            _lastFrame = frame;
            try
            {
                if (_overlay) Present(lines); else Invalidate();
            }
            catch (Exception ex) when (ex is ExternalException or ArgumentException)
            {
                Console.Error.WriteLine($"chat overlay: {ex.Message}");
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!_overlay)
                Draw(e.Graphics, _font, _owner.Snapshot(DateTime.UtcNow), ClientSize);
        }

        /// <summary>
        /// Draws newest-at-bottom lines, wrapping long replies, clipped from the top. Each line sits on
        /// its own box sized to its text, in the colours and proportions of IL-2's message line
        /// ("Game is Paused", see <see cref="Il2ChatStyle"/>).
        /// </summary>
        internal static void Draw(Graphics g, Font font, List<(ChatLine Line, float Alpha)> lines, Size size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float cap = font.Size * Il2ChatStyle.CapHeightEm;
            float pad = cap * Il2ChatStyle.PaddingCaps, gap = cap * Il2ChatStyle.GapCaps;
            float row = font.GetHeight(g);
            float padY = Math.Max(0, (cap * Il2ChatStyle.BoxHeightCaps - row) / 2); // one-row box is BoxHeightCaps tall
            float y = size.Height;
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Trimming = StringTrimming.Word,
                FormatFlags = StringFormatFlags.MeasureTrailingSpaces,
            };

            foreach (var (line, alpha) in Enumerable.Reverse(lines))
            {
                string text = (line.Speaker.Length > 0 ? line.Speaker + ": " : "") + line.Text;
                var textArea = new SizeF(size.Width - 2 * pad, size.Height);
                var textSize = g.MeasureString(text, font, textArea, format);
                float height = Math.Max(row, textSize.Height) + 2 * padY;
                y -= height;
                if (y < 0)
                    break;

                using (var box = new SolidBrush(Color.FromArgb((int)(Il2ChatStyle.BoxAlpha * alpha), Color.FromArgb(Il2ChatStyle.BoxRgb))))
                    g.FillRectangle(box, 0, y, Math.Min(size.Width, textSize.Width + 2 * pad), height);
                Fill(g, text, font, Color.FromArgb(Il2ChatStyle.Colour(line)), alpha, new RectangleF(pad, y + padY, textArea.Width, height - 2 * padY), format);
                y -= gap;
            }
        }

        /// <summary>Text as a filled path: it antialiases correctly onto the transparent layered bitmap, where ClearType cannot.</summary>
        private static void Fill(Graphics g, string text, Font font, Color colour, float alpha, RectangleF rect, StringFormat format)
        {
            if (text.Length == 0)
                return;
            using var path = new GraphicsPath();
            path.AddString(text, font.FontFamily, (int)font.Style, font.Size, rect, format);
            using var fill = new SolidBrush(Color.FromArgb((int)(255 * alpha), colour.R, colour.G, colour.B));
            g.FillPath(fill, path);
        }

        /// <summary>Renders into a 32-bit ARGB bitmap and hands it to the layered window (per-pixel alpha).</summary>
        private void Present(List<(ChatLine Line, float Alpha)> lines)
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0)
                return;
            using var bitmap = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                Draw(g, _font, lines, bitmap.Size);
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBitmap);
            try
            {
                var size = new NativeSize(bitmap.Width, bitmap.Height);
                var source = new NativePoint(0, 0);
                var topLeft = new NativePoint(Left, Top);
                var blend = new BlendFunction { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                UpdateLayeredWindow(Handle, screenDc, ref topLeft, ref size, memDc, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                SelectObject(memDc, old);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _font.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    private const int ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativeSize(int Cx, int Cy);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize,
        IntPtr hdcSrc, ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
