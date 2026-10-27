using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Il2Radio.Core;

namespace Il2Radio.App;

/// <summary>The painted header at the top of the window.</summary>
internal sealed class HeaderBanner : Control
{
    private readonly string _version = AppVersion.Text.Split(' ')[0];

    public HeaderBanner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
        Dock = DockStyle.Top;
        Height = 96;
        TabStop = false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = LogicalToDeviceUnits(96);
    }

    protected override void OnPaint(PaintEventArgs e) =>
        Brand.PaintBanner(e.Graphics, ClientRectangle, _version == "unknown" ? "" : _version, DeviceDpi / 96f);
}

/// <summary>The radio's state as the nav strip shows it.</summary>
internal enum RadioState { Starting, Running, Stopped, Error }

/// <summary>
/// The tabs, drawn as a dark strip under the header with a brass underline, and the radio's state on the right.
/// The pages themselves are a <see cref="PageHost"/>, so code still selects them through the TabControl.
/// </summary>
internal sealed class NavStrip : Control
{
    private readonly TabControl _tabs;
    private readonly List<RectangleF> _hits = [];
    private int _hover = -1;
    private RadioState _state = RadioState.Starting;
    private string _stateText = "Starting";

    public NavStrip(TabControl tabs)
    {
        _tabs = tabs;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
        Dock = DockStyle.Top;
        Height = 38;
        TabStop = false;
        Cursor = Cursors.Hand;
        _tabs.SelectedIndexChanged += (_, _) => Invalidate();
        _tabs.ControlAdded += (_, _) => Invalidate();
    }

    public void SetState(RadioState state, string text)
    {
        _state = state;
        _stateText = text;
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = LogicalToDeviceUnits(38);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float scale = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Brand.Nav);

        _hits.Clear();
        using var font = Brand.Semibold(9.5f);
        float x = 18 * scale;
        for (int i = 0; i < _tabs.TabPages.Count; i++)
        {
            string text = _tabs.TabPages[i].Text;
            var size = g.MeasureString(text, font);
            var hit = new RectangleF(x - 10 * scale, 0, size.Width + 20 * scale, Height);
            _hits.Add(hit);
            bool selected = i == _tabs.SelectedIndex;
            var color = selected ? Color.White : i == _hover ? Brand.Cream : Brand.NavText;
            using (var brush = new SolidBrush(color))
                g.DrawString(text, font, brush, x, (Height - size.Height) / 2);
            if (selected)
            {
                using var brass = new SolidBrush(Brand.Brass);
                g.FillRectangle(brass, x - 2 * scale, Height - 3 * scale, size.Width + 4 * scale, 3 * scale);
            }

            x += size.Width + 22 * scale;
        }

        using var small = Brand.Ui(8.75f);
        var textSize = g.MeasureString(_stateText, small);
        float dot = 8 * scale;
        var pill = new RectangleF(Width - textSize.Width - dot - 38 * scale, (Height - 22 * scale) / 2,
            textSize.Width + dot + 22 * scale, 22 * scale);
        var accent = _state switch
        {
            RadioState.Running => Brand.Good,
            RadioState.Error => Brand.Bad,
            RadioState.Starting => Brand.Brass,
            _ => Brand.Idle,
        };
        using (var path = Brand.RoundedRect(pill, pill.Height / 2))
        using (var back = new SolidBrush(Color.FromArgb(40, accent)))
        using (var edge = new Pen(Color.FromArgb(120, accent), 1))
        {
            g.FillPath(back, path);
            g.DrawPath(edge, path);
        }

        using (var brush = new SolidBrush(accent))
            g.FillEllipse(brush, pill.X + 9 * scale, pill.Y + (pill.Height - dot) / 2, dot, dot);
        using (var brush = new SolidBrush(Brand.Cream))
            g.DrawString(_stateText, small, brush, pill.X + 13 * scale + dot, pill.Y + (pill.Height - textSize.Height) / 2);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = _hits.FindIndex(h => h.Contains(e.Location));
        Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int index = _hits.FindIndex(h => h.Contains(e.Location));
        if (index >= 0)
            _tabs.SelectedIndex = index;
    }
}

/// <summary>A TabControl without its own tab headers or border; <see cref="NavStrip"/> draws the tabs.</summary>
internal sealed class PageHost : TabControl
{
    private const int TcmAdjustRect = 0x1328;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == TcmAdjustRect && !DesignMode)
        {
            m.Result = 1;
            return;
        }

        base.WndProc(ref m);
    }
}

/// <summary>A section heading in brass capitals with a hairline running to the right edge.</summary>
internal sealed class SectionRule : Label
{
    public SectionRule(string text)
    {
        Text = text.ToUpperInvariant();
        Font = Brand.Semibold(8.75f);
        ForeColor = Brand.BrassInk;
        AutoSize = false;
        Height = 26;
        TextAlign = ContentAlignment.BottomLeft;
        Padding = new Padding(0, 0, 0, 3);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var size = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        int y = Height - Padding.Bottom - size.Height / 2;
        int x = size.Width + LogicalToDeviceUnits(10);
        if (x >= Width)
            return;
        using var pen = new Pen(Brand.Rule);
        e.Graphics.DrawLine(pen, x, y, Width - 1, y);
    }
}
