using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Il2Radio.App;

/// <summary>
/// The radio's look: palette, fonts and the art, all drawn in code (no image files, nothing from IL-2).
/// The fighter is a generic single-engine silhouette with no national markings, so it fits any air force.
/// tools/BrandArt renders app.ico and the README images from this same code.
/// </summary>
internal static class Brand
{
    public const string Name = "IL-2 Tactical Radio";

    // Night sky and brass, like a radio panel lit at dusk.
    public static readonly Color SkyTop = Color.FromArgb(0x12, 0x19, 0x21);
    public static readonly Color SkyBottom = Color.FromArgb(0x32, 0x45, 0x58);
    public static readonly Color Glow = Color.FromArgb(0xE8, 0xB0, 0x5A);
    public static readonly Color Nav = Color.FromArgb(0x1A, 0x22, 0x2B);
    public static readonly Color NavText = Color.FromArgb(0xA9, 0xB4, 0xBF);
    public static readonly Color Brass = Color.FromArgb(0xD4, 0x9B, 0x3A);
    public static readonly Color BrassHover = Color.FromArgb(0xE2, 0xAD, 0x50);
    public static readonly Color BrassInk = Color.FromArgb(0x8A, 0x5E, 0x16);
    public static readonly Color Cream = Color.FromArgb(0xF2, 0xEA, 0xD8);
    public static readonly Color Paper = Color.FromArgb(0xF5, 0xF3, 0xEE);
    public static readonly Color Footer = Color.FromArgb(0xEA, 0xE6, 0xDD);
    public static readonly Color Rule = Color.FromArgb(0xD8, 0xD2, 0xC6);
    public static readonly Color Ink = Color.FromArgb(0x1F, 0x24, 0x2A);
    public static readonly Color Muted = Color.FromArgb(0x6A, 0x70, 0x78);
    public static readonly Color Good = Color.FromArgb(0x4C, 0xB0, 0x5C);
    public static readonly Color Bad = Color.FromArgb(0xD0, 0x4A, 0x3C);
    public static readonly Color Idle = Color.FromArgb(0x8C, 0x96, 0xA0);
    public static readonly Color ProblemBack = Color.FromArgb(0xFB, 0xEA, 0xE7);
    public static readonly Color ProblemText = Color.FromArgb(0x8B, 0x22, 0x18);

    public static Font Ui(float size, GraphicsUnit unit = GraphicsUnit.Point) => new("Segoe UI", size, unit);

    public static Font Semibold(float size, GraphicsUnit unit = GraphicsUnit.Point) => SemiboldFamily is { } family
        ? new Font(family, size, unit)
        : new Font("Segoe UI", size, FontStyle.Bold, unit);

    /// <summary>The radio log boxes: Cascadia Mono where Windows has it, Consolas otherwise.</summary>
    public static Font Mono(float size) => new(MonoFamily, size);

    private static readonly string? SemiboldFamily = Installed("Segoe UI Semibold");
    private static readonly string MonoFamily = Installed("Cascadia Mono") ?? "Consolas";

    private static string? Installed(string family)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name.Equals(family, StringComparison.OrdinalIgnoreCase)) ? family : null;
    }

    /// <summary>The window and exe icon, from the embedded app.ico.</summary>
    public static Icon? AppIcon()
    {
        using var stream = typeof(Brand).Assembly.GetManifestResourceStream("Il2Radio.App.app.ico");
        return stream is null ? null : new Icon(stream);
    }

    /// <summary>
    /// A fighter seen from above, nose toward −y, 96 units span and 82 long, centred on the origin.
    /// One half is listed nose to tail; the other is its mirror.
    /// </summary>
    public static GraphicsPath Fighter()
    {
        PointF[] half =
        [
            new(0, -41), new(1.8f, -40.5f), new(3.0f, -38.5f), new(3.8f, -35), new(4.4f, -28), new(4.8f, -18),
            new(5.0f, -12), new(40, -8), new(45, -7), new(47.5f, -5.5f), new(48.3f, -3.5f), new(47.6f, -1.6f),
            new(45, -0.6f), new(40, -0.2f), new(5.0f, 4.5f), new(4.0f, 12), new(3.0f, 22), new(2.4f, 28.5f),
            new(11, 29.5f), new(15, 31), new(16.2f, 33), new(15.4f, 35), new(12, 35.8f), new(2.0f, 35.5f),
            new(1.4f, 39), new(0.8f, 41), new(0, 41.3f),
        ];
        var outline = half.Concat(half.Reverse().Skip(1).SkipLast(1).Select(p => new PointF(-p.X, p.Y))).ToArray();
        var path = new GraphicsPath(FillMode.Winding);
        path.AddPolygon(outline);
        return path;
    }

    /// <summary>Draws the fighter centred at <paramref name="center"/>, <paramref name="span"/> pixels wingtip to wingtip, nose on <paramref name="heading"/> (degrees, 0 up, clockwise).</summary>
    public static void DrawFighter(Graphics g, PointF center, float span, float heading, Color fill, Color? canopy = null)
    {
        var state = g.Save();
        g.TranslateTransform(center.X, center.Y);
        g.RotateTransform(heading);
        g.ScaleTransform(span / 96f, span / 96f);
        using (var path = Fighter())
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        if (canopy is Color glass && span >= 40)
        {
            using var brush = new SolidBrush(glass);
            g.FillEllipse(brush, -2.2f, -14, 4.4f, 12);
        }

        g.Restore(state);
    }

    /// <summary>The round badge: a fighter climbing to the right, transmitting to its left, inside a brass ring.</summary>
    public static void DrawEmblem(Graphics g, RectangleF r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float d = Math.Min(r.Width, r.Height);
        bool tiny = d <= 24;
        float ring = Math.Max(1.2f, d * (tiny ? 0.07f : 0.045f));
        var disc = new RectangleF(r.X + ring / 2, r.Y + ring / 2, d - ring, d - ring);

        using (var face = new GraphicsPath())
        {
            face.AddEllipse(disc);
            using var brush = new PathGradientBrush(face)
            {
                CenterColor = Color.FromArgb(0x3A, 0x4F, 0x64),
                SurroundColors = [SkyTop],
                CenterPoint = new PointF(disc.X + disc.Width * 0.62f, disc.Y + disc.Height * 0.38f),
            };
            g.FillPath(brush, face);
        }

        using (var pen = new Pen(Brass, ring))
            g.DrawEllipse(pen, disc);
        if (!tiny)
        {
            float inset = d * 0.09f;
            using var pen = new Pen(Color.FromArgb(70, Brass), Math.Max(1f, d * 0.012f));
            g.DrawEllipse(pen, RectangleF.Inflate(disc, -inset, -inset));
        }

        var plane = new PointF(r.X + d * 0.54f, r.Y + d * 0.53f);
        DrawFighter(g, plane, d * (tiny ? 0.68f : 0.60f), 45, Cream, Color.FromArgb(150, SkyBottom));

        if (d < 20)
            return;
        int arcs = tiny ? 2 : 3;
        using var wave = new Pen(Brass, Math.Max(1.1f, d * 0.035f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < arcs; i++)
        {
            float radius = d * (0.30f + i * 0.085f);
            wave.Color = Color.FromArgb(255 - i * 55, Brass);
            g.DrawArc(wave, plane.X - radius, plane.Y - radius, radius * 2, radius * 2, -158, 46);
        }
    }

    /// <summary>
    /// The window's header: dusk sky, the emblem and name on the left, a finger-four with contrails on the right.
    /// <paramref name="scale"/> is the DPI factor (1 at 96 DPI).
    /// </summary>
    public static void PaintBanner(Graphics g, Rectangle r, string version, float scale)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var sky = new LinearGradientBrush(r, SkyTop, SkyBottom, LinearGradientMode.Vertical))
            g.FillRectangle(sky, r);

        var state = g.Save();
        g.SetClip(r);
        // A low sun behind the formation.
        Soft(g, new RectangleF(r.Right - 470 * scale, r.Top + r.Height * 0.15f, 560 * scale, r.Height * 1.9f),
            Color.FromArgb(105, Glow));
        Soft(g, new RectangleF(r.Right - 330 * scale, r.Top + r.Height * 0.55f, 300 * scale, r.Height * 0.9f),
            Color.FromArgb(70, 0xF6, 0xC9, 0x7E));

        // Soft cloud banks low in the sky.
        (float x, float y, float w, float h, int alpha)[] banks =
        [
            (-0.05f, 0.72f, 0.40f, 0.30f, 26), (0.24f, 0.80f, 0.42f, 0.34f, 22), (0.52f, 0.66f, 0.30f, 0.22f, 20),
            (0.70f, 0.82f, 0.42f, 0.32f, 30),
        ];
        foreach (var (x, y, w, h, alpha) in banks)
            Soft(g, new RectangleF(r.X + r.Width * x, r.Y + r.Height * y, r.Width * w, r.Height * h), Color.FromArgb(alpha, Color.White));

        // A Schwarm in finger-four: the lead, his wingman behind on the left, the element behind on the right.
        const float heading = 72;
        float span = Math.Min(22 * scale, r.Height * 0.23f);
        float spacing = span * 1.05f;
        var lead = new PointF(r.Right - 112 * scale, r.Top + r.Height * 0.30f);
        (float side, float back)[] slots = [(0, 0), (-1.0f, 0.8f), (1.1f, 0.7f), (2.2f, 1.5f)];
        double a = heading * Math.PI / 180;
        var forward = new PointF((float)Math.Sin(a), -(float)Math.Cos(a));
        var right = new PointF(-forward.Y, forward.X);
        var silhouette = Color.FromArgb(240, 0x0E, 0x13, 0x1A);
        foreach (var (side, back) in slots)
        {
            var at = new PointF(
                lead.X + (right.X * side - forward.X * back) * spacing,
                lead.Y + (right.Y * side - forward.Y * back) * spacing);
            var tail = new PointF(at.X - forward.X * span * 0.42f, at.Y - forward.Y * span * 0.42f);
            var end = new PointF(at.X - forward.X * span * 6f, at.Y - forward.Y * span * 6f);
            using (var trail = new LinearGradientBrush(tail, end, Color.FromArgb(80, Color.White), Color.FromArgb(0, Color.White)))
            using (var pen = new Pen(trail, 1.2f * scale))
                g.DrawLine(pen, tail, end);
            DrawFighter(g, at, span, heading, silhouette);
        }

        g.Restore(state);

        float pad = 20 * scale;
        float badge = Math.Min(r.Height - 28 * scale, 60 * scale);
        DrawEmblem(g, new RectangleF(r.X + pad, r.Y + (r.Height - badge) / 2, badge, badge));

        float textX = r.X + pad + badge + 14 * scale;
        // Sized in pixels from the scale, so a bitmap rendered at 2× gets 2× text too.
        using var title = Semibold(23 * scale, GraphicsUnit.Pixel);
        using var subtitle = Ui(12.5f * scale, GraphicsUnit.Pixel);
        using var small = Ui(11f * scale, GraphicsUnit.Pixel);
        float titleH = g.MeasureString(Name.ToUpperInvariant(), title).Height;
        float subH = g.MeasureString("Ag", subtitle).Height;
        float top = r.Y + (r.Height - titleH - subH) / 2 - 2 * scale;
        using (var white = new SolidBrush(Color.White))
            g.DrawString(Name.ToUpperInvariant(), title, white, textX, top);
        string strap = "Flight radio for IL-2 Great Battles Career";
        using (var muted = new SolidBrush(NavText))
            g.DrawString(strap, subtitle, muted, textX + 2 * scale, top + titleH - 4 * scale);

        if (string.IsNullOrWhiteSpace(version))
            return;
        float strapW = g.MeasureString(strap, subtitle).Width;
        string tag = "v" + version;
        var tagSize = g.MeasureString(tag, small);
        var tagRect = new RectangleF(textX + strapW + 10 * scale, top + titleH - 2 * scale, tagSize.Width + 8 * scale, tagSize.Height + 1 * scale);
        using (var outline = new Pen(Color.FromArgb(150, Brass), 1))
        using (var pill = RoundedRect(tagRect, tagRect.Height / 2))
            g.DrawPath(outline, pill);
        using (var brass = new SolidBrush(Brass))
            g.DrawString(tag, small, brass, tagRect.X + 4 * scale, tagRect.Y + 0.5f * scale);
    }

    /// <summary>An ellipse of <paramref name="color"/> fading to nothing at its edge.</summary>
    private static void Soft(Graphics g, RectangleF bounds, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var brush = new PathGradientBrush(path) { CenterColor = color, SurroundColors = [Color.FromArgb(0, color)] };
        g.FillPath(brush, path);
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A dark title bar the colour of the header's sky (Windows 11; older Windows keeps its own).</summary>
    public static void DarkCaption(IntPtr window)
    {
        const int UseImmersiveDarkMode = 20, CaptionColor = 35;
        try
        {
            int on = 1;
            DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref on, sizeof(int));
            int colorRef = SkyTop.R | SkyTop.G << 8 | SkyTop.B << 16;
            DwmSetWindowAttribute(window, CaptionColor, ref colorRef, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void Primary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Brass;
        button.ForeColor = SkyTop;
        button.Font = Semibold(9);
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = BrassHover;
        button.FlatAppearance.MouseDownBackColor = BrassInk;
        button.Cursor = Cursors.Hand;
    }

    public static void Secondary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.White;
        button.ForeColor = Ink;
        button.FlatAppearance.BorderColor = Rule;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xF3, 0xEE, 0xE2);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xE6, 0xDE, 0xCC);
        button.Cursor = Cursors.Hand;
    }
}
