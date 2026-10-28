using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Il2Radio.Core.Geometry;
using Il2Radio.Core.Radio;

namespace Il2Radio.Windows;

/// <summary>
/// Draws the kneeboard plot (<see cref="KneeboardPicture"/>) as a pilot's kneeboard of the period: a sheet of map paper,
/// folded once and clipped to a dark board, with the 10 km squares printed fine and numbered only in the margins, as on a
/// military map. What the pilot prepared before take-off (the briefed route, each leg's course and distance, the target
/// and home field) is in soft pencil; what the radio has said since is in coloured pencil, with its age, fading as it
/// ages. Printed matter is typewriter type, notes are handwriting, strokes wobble a little. Every wobble is seeded by what
/// it draws, never by the clock, so a redraw does not shimmer. Small is the sheet with a scale; large adds the title, the
/// square numbers, a key and the footer. Sizes are shares of the screen height. Used by <see cref="InGameChat"/>.
/// </summary>
internal static class KneeboardPanel
{
    /// <summary>Outer height of each view as a share of the screen height (an open question in the roadmap; to be tried in a flight).</summary>
    public const float SmallShare = 0.30f, LargeShare = 0.56f;

    private static readonly Color Board = Color.FromArgb(0x2E, 0x29, 0x22);
    private static readonly Color BoardEdge = Color.FromArgb(0x55, 0x4B, 0x3C);
    private static readonly Color Paper = Color.FromArgb(0xE6, 0xDE, 0xC6);
    private static readonly Color GridLine = Color.FromArgb(0x9C, 0x8F, 0x74);
    private static readonly Color Print = Color.FromArgb(0x4A, 0x40, 0x30);
    private static readonly Color Graphite = Color.FromArgb(0x3A, 0x38, 0x36);
    private static readonly Color RedPencil = Color.FromArgb(0xA8, 0x2A, 0x1E);
    private static readonly Color BluePencil = Color.FromArgb(0x24, 0x3E, 0x78);
    private static readonly Color MatePencil = Color.FromArgb(0x2E, 0x5C, 0x9E);

    private static readonly string Typewriter = Installed("Courier New") ?? "Arial";
    private static readonly string Printed = Installed("Georgia") ?? Installed("Times New Roman") ?? "Arial";
    private static readonly string Hand = Installed("Ink Free") ?? Installed("Segoe Print") ?? "Arial";

    /// <summary>Renders the picture for a back buffer <paramref name="screenHeight"/> pixels tall; null when hidden.</summary>
    public static Bitmap? Render(KneeboardPicture picture, int screenHeight)
    {
        if (picture.View == KneeboardView.Hidden)
            return null;
        bool large = picture.View == KneeboardView.Large;
        var sheet = picture.Sheet;
        float u = screenHeight / 1080f; // one pixel at 1080 lines
        float height = screenHeight * (large ? LargeShare : SmallShare);
        float board = 9 * u, clip = large ? 16 * u : 11 * u, paperPad = 10 * u;
        float text = 14 * u;
        float header = large ? text * 2.1f : 0;
        float margin = large ? text * 1.3f : 0; // the printed square numbers above and left of the map
        float footer = large ? (picture.Footer.Count + 1) * text * 1.55f + 6 * u : 0;
        float mapHeight = height - 2 * board - clip - 2 * paperPad - header - margin - footer;
        float scale = mapHeight / (float)sheet.HeightMetres; // pixels per metre
        float mapWidth = (float)sheet.WidthMetres * scale;
        int width = (int)Math.Ceiling(mapWidth + margin + 2 * paperPad + 2 * board);
        var paper = new RectangleF(board, board + clip, width - 2 * board, height - 2 * board - clip);
        var map = new RectangleF(paper.Left + paperPad + margin, paper.Top + paperPad + header + margin, mapWidth, mapHeight);

        var bitmap = new Bitmap(width, (int)Math.Ceiling(height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        DrawBoard(g, bitmap.Size, u);
        DrawPaper(g, paper, map, u);

        PointF At(double north, double east) =>
            new(map.Left + (float)((east - sheet.West) * scale), map.Top + (float)((sheet.North - north) * scale));

        using var typed = new Font(Typewriter, text, FontStyle.Bold, GraphicsUnit.Pixel);
        using var typedSmall = new Font(Typewriter, text * 0.82f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var numbers = new Font(Printed, text * 0.78f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var hand = new Font(Hand, text * (large ? 1.15f : 1.05f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var handSmall = new Font(Hand, text * 0.95f, FontStyle.Bold, GraphicsUnit.Pixel);

        if (large)
        {
            Text(g, $"KNEEBOARD  ·  {sheet.MapName.ToUpperInvariant()}", typed, Print, map.Left - margin, paper.Top + paperPad);
            string right = "BRIEFED ROUTE";
            Text(g, right, typedSmall, Print, map.Right - Measure(g, right, typedSmall).Width, paper.Top + paperPad + 2 * u);
        }

        DrawGrid(g, sheet, map, scale, u, large ? numbers : null);
        var save = g.Save();
        g.SetClip(map);
        DrawRoute(g, sheet, At, scale, u, hand, handSmall, large);
        foreach (var entry in picture.Marks.Where(m => m.Mark.Subject != PlotSubject.Player))
            DrawMark(g, entry, At, scale, u, handSmall, map);
        foreach (var entry in picture.Marks.Where(m => m.Mark.Subject == PlotSubject.Player))
            DrawFix(g, entry, At, scale, u, handSmall, map, picture.Player is null ? picture.WithPlayer : []);
        if (picture.Player is { } you && picture.OffSheet is null)
            DrawReckoning(g, you, At, scale, u, hand, handSmall, map, picture.WithPlayer, picture.Marks.FirstOrDefault(m => m.Mark.Subject == PlotSubject.Player));
        g.Restore(save);
        if (picture.OffSheet is { } edgeName)
            OffSheet(g, edgeName, map, hand, u);

        NorthArrow(g, map, u, typedSmall);
        ScaleBar(g, map, scale, u, typedSmall);

        if (large)
        {
            float y = map.Bottom + 8 * u;
            foreach (string line in picture.Footer)
            {
                Text(g, Fit(g, line, handSmall, map.Width + margin), handSmall, Graphite, map.Left - margin, y);
                y += text * 1.55f;
            }
            Key(g, typedSmall, u, map.Left - margin, y + 2 * u, map.Right);
        }
        return bitmap;
    }

    /// <summary>The board the sheet is clipped to: dark, a lighter bevel, and the metal clip across the top.</summary>
    private static void DrawBoard(Graphics g, Size size, float u)
    {
        var outer = new RectangleF(0.5f, 0.5f, size.Width - 1, size.Height - 1);
        using (var path = Rounded(outer, 8 * u))
        using (var fill = new LinearGradientBrush(outer, Color.FromArgb(250, BoardEdge), Color.FromArgb(250, Board), LinearGradientMode.ForwardDiagonal))
        using (var edge = new Pen(Color.FromArgb(200, 0x1A, 0x17, 0x13), Math.Max(1, 1.5f * u)))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }

        float clipWidth = Math.Min(size.Width * 0.34f, 90 * u), clipHeight = 22 * u;
        var clip = new RectangleF((size.Width - clipWidth) / 2, 3 * u, clipWidth, clipHeight);
        using (var path = Rounded(clip, 4 * u))
        using (var metal = new LinearGradientBrush(clip, Color.FromArgb(0xB4, 0xB2, 0xAA), Color.FromArgb(0x5E, 0x5C, 0x57), LinearGradientMode.Vertical))
        using (var rim = new Pen(Color.FromArgb(0x33, 0x31, 0x2D), Math.Max(1, u)))
        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
        {
            g.FillRectangle(shadow, clip.Left + 2 * u, clip.Bottom, clip.Width - 4 * u, 3 * u);
            g.FillPath(metal, path);
            g.DrawPath(rim, path);
        }
        using var rivet = new SolidBrush(Color.FromArgb(0x3C, 0x3A, 0x36));
        foreach (float x in new[] { clip.Left + 8 * u, clip.Right - 8 * u })
            g.FillEllipse(rivet, x - 2.5f * u, clip.Top + clipHeight / 2 - 2.5f * u, 5 * u, 5 * u);
    }

    /// <summary>Aged map paper: grain, darker edges, and the fold across the middle of the map.</summary>
    private static void DrawPaper(Graphics g, RectangleF paper, RectangleF map, float u)
    {
        using (var fill = new SolidBrush(Paper))
            g.FillRectangle(fill, paper);

        var rng = new Random((int)(paper.Width * 31 + paper.Height)); // grain fixed for a sheet of this size
        int specks = (int)(paper.Width * paper.Height / (9 * u * u + 1));
        using (var dark = new SolidBrush(Color.FromArgb(16, 0x5A, 0x48, 0x2A)))
        using (var light = new SolidBrush(Color.FromArgb(22, 255, 255, 240)))
        {
            for (int i = 0; i < specks; i++)
            {
                float x = paper.Left + (float)rng.NextDouble() * paper.Width, y = paper.Top + (float)rng.NextDouble() * paper.Height;
                float s = (0.6f + (float)rng.NextDouble() * 1.4f) * u;
                g.FillRectangle(i % 3 == 0 ? light : dark, x, y, s, s);
            }
        }

        float edge = 16 * u;
        var shade = Color.FromArgb(55, 0x6E, 0x55, 0x30);
        void Edge(RectangleF band, LinearGradientMode mode, bool flip)
        {
            using var brush = new LinearGradientBrush(band, flip ? Color.Transparent : shade, flip ? shade : Color.Transparent, mode);
            g.FillRectangle(brush, band);
        }
        Edge(new RectangleF(paper.Left, paper.Top, paper.Width, edge), LinearGradientMode.Vertical, false);
        Edge(new RectangleF(paper.Left, paper.Bottom - edge, paper.Width, edge), LinearGradientMode.Vertical, true);
        Edge(new RectangleF(paper.Left, paper.Top, edge, paper.Height), LinearGradientMode.Horizontal, false);
        Edge(new RectangleF(paper.Right - edge, paper.Top, edge, paper.Height), LinearGradientMode.Horizontal, true);

        // Folded once to the briefed area: a soft shadow and a highlight down the middle of the map.
        float fold = map.Left + map.Width / 2;
        using (var crease = new Pen(Color.FromArgb(40, 0x50, 0x40, 0x25), 2.5f * u))
            g.DrawLine(crease, fold, paper.Top + 2 * u, fold, paper.Bottom - 2 * u);
        using (var shine = new Pen(Color.FromArgb(70, 255, 255, 245), 1.2f * u))
            g.DrawLine(shine, fold + 2 * u, paper.Top + 2 * u, fold + 2 * u, paper.Bottom - 2 * u);
        using (var edgeLine = new Pen(Color.FromArgb(90, 0x6E, 0x5E, 0x40), Math.Max(1, u)))
            g.DrawRectangle(edgeLine, paper.X, paper.Y, paper.Width, paper.Height);
    }

    /// <summary>Fine printed grid; on the large view the square numbers along the top and left margins, and a double neat line.</summary>
    private static void DrawGrid(Graphics g, KneeboardSheet sheet, RectangleF map, float scale, float u, Font? numbers)
    {
        double step = MapGrid.SquareMetres;
        using var pen = new Pen(Color.FromArgb(105, GridLine), Math.Max(0.7f, 0.7f * u));
        for (double east = sheet.West + step; east < sheet.East - 1; east += step)
        {
            float x = map.Left + (float)((east - sheet.West) * scale);
            g.DrawLine(pen, x, map.Top, x, map.Bottom);
        }
        for (double north = sheet.South + step; north < sheet.North - 1; north += step)
        {
            float y = map.Top + (float)((sheet.North - north) * scale);
            g.DrawLine(pen, map.Left, y, map.Right, y);
        }
        using (var neat = new Pen(Print, Math.Max(1, 1.4f * u)))
            g.DrawRectangle(neat, map.X, map.Y, map.Width, map.Height);
        using (var outer = new Pen(Color.FromArgb(160, Print), Math.Max(0.8f, 0.7f * u)))
            g.DrawRectangle(outer, map.X - 3 * u, map.Y - 3 * u, map.Width + 6 * u, map.Height + 6 * u);

        if (numbers is null || !sheet.Labelled)
            return;
        // Military-map style: the column's two figures along the top, the row's down the side; a square is row then column.
        for (double west = sheet.West; west < sheet.East - 1; west += step)
            if (MapGrid.SquareLabel(sheet.North - step, west) is { } label)
            {
                float x = map.Left + (float)((west + step / 2 - sheet.West) * scale);
                var size = Measure(g, label[2..], numbers);
                Text(g, label[2..], numbers, Print, x - size.Width / 2, map.Top - 4 * u - size.Height);
            }
        for (double south = sheet.South; south < sheet.North - 1; south += step)
            if (MapGrid.SquareLabel(south, sheet.West) is { } label)
            {
                float y = map.Top + (float)((sheet.North - south - step / 2) * scale);
                var size = Measure(g, label[..2], numbers);
                Text(g, label[..2], numbers, Print, map.Left - 5 * u - size.Width, y - size.Height / 2);
            }
    }

    /// <summary>The route as the pilot pencilled it before take-off: the legs, each one's course and distance, the target ringed and hatched, home.</summary>
    private static void DrawRoute(Graphics g, KneeboardSheet sheet, Func<double, double, PointF> at, float scale, float u, Font hand, Font handSmall, bool large)
    {
        var route = sheet.Route;
        using (var line = new Pen(Color.FromArgb(215, Graphite), 1.8f * u) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var noted = new HashSet<(int, int, int, int)>();
            for (int i = 1; i < route.Count; i++)
            {
                var a = route[i - 1].Position;
                var b = route[i].Position;
                var p = at(a.North, a.East);
                var q = at(b.North, b.East);
                Pencil(g, line, p, q, u, Seed(a.North, b.East));
                var leg = Navigation.Relative(a, b);
                // Each leg once (a patrol loop is flown round once; the way home repeats no leg), long enough to letter.
                var key = ((int)p.X, (int)p.Y, (int)q.X, (int)q.Y);
                if (large && leg is { RangeMetres: > 8_000 } r && noted.Add(key))
                    LegNote(g, $"{Navigation.CourseDegrees(r.BearingDegrees):000}°  {r.RangeMetres / 1000:0}", handSmall, p, q, u);
            }
        }

        using var dot = new Pen(Graphite, 1.5f * u);
        using var red = new Pen(RedPencil, 1.8f * u);
        bool targetNamed = false;
        foreach (var w in route)
        {
            var p = at(w.Position.North, w.Position.East);
            int seed = Seed(w.Position.North, w.Position.East);
            if (w.IsAttack)
            {
                float r = Math.Max(10 * u, 3_000 * scale);
                Hatch(g, Color.FromArgb(70, RedPencil), new RectangleF(p.X - r, p.Y - r, 2 * r, 2 * r), ellipse: true, 5 * u, u);
                PencilCircle(g, red, p, r, u, seed);
                if (!targetNamed)
                {
                    Centred(g, "target", handSmall, RedPencil, p.X, p.Y + r + 2 * u);
                    targetNamed = true;
                }
            }
            else if (!w.IsLanding && !w.Name.Contains("TAKEOFF", StringComparison.OrdinalIgnoreCase))
            {
                PencilCircle(g, dot, p, 4.5f * u, u, seed);
            }
        }
        if (sheet.HomeField is { } home)
        {
            var p = at(home.Position.North, home.Position.East);
            // A field as on a period map: a ring with the runway across it.
            using var field = new Pen(Graphite, 1.6f * u);
            PencilCircle(g, field, p, 8 * u, u, Seed(home.Position.North, 1));
            g.DrawLine(field, p.X - 6 * u, p.Y + 3 * u, p.X + 6 * u, p.Y - 3 * u);
            Text(g, home.Name, hand, Graphite, p.X + 11 * u, p.Y - hand.Size * 0.7f);
        }
    }

    /// <summary>
    /// "095°  25" along a leg, kept upright, on the left of the way it is flown: an outbound and a return leg side by side
    /// then carry their notes on their outer sides instead of on top of each other.
    /// </summary>
    private static void LegNote(Graphics g, string note, Font font, PointF p, PointF q, float u)
    {
        float angle = (float)(Math.Atan2(q.Y - p.Y, q.X - p.X) * 180 / Math.PI);
        bool flipped = angle is > 90 or < -90;
        if (angle > 90) angle -= 180;
        if (angle < -90) angle += 180;
        var size = Measure(g, note, font);
        var state = g.Save();
        g.TranslateTransform((p.X + q.X) / 2, (p.Y + q.Y) / 2);
        g.RotateTransform(angle);
        Text(g, note, font, Color.FromArgb(200, Graphite), -size.Width / 2, flipped ? 3 * u : -size.Height - 3 * u);
        g.Restore(state);
    }

    /// <summary>The player's fix: the square or ninth hatched in blue pencil (a ringed patch when a mate's words placed him), "fix · 2 min".</summary>
    private static void DrawFix(Graphics g, PlotEntry entry, Func<double, double, PointF> at, float scale, float u, Font hand, RectangleF map, IReadOnlyList<string> with)
    {
        var m = entry.Mark;
        var (n, e, _) = m.Spread;
        if (!map.Contains(at(n, e)))
            return;
        int a = Alpha(entry.Alpha);
        var area = Shape(g, m, at, scale, u, BluePencil, a, dashed: false, hatch: true);
        string label = $"fix {entry.AgeText}";
        var size = Measure(g, label, hand);
        Note(g, label, hand, Color.FromArgb(a, BluePencil), area.Left - size.Width - 4 * u, area.Top + area.Height / 2 - size.Height / 2, map);
        if (with.Count > 0)
            Note(g, $"{string.Join(", ", with)} with you", hand, Color.FromArgb(a, MatePencil), area.Right + 4 * u, area.Bottom - size.Height, map);
    }

    /// <summary>The dead-reckoning patch: a dashed pencil ring, "DR" and the time, and a dotted line back to the fix it started from.</summary>
    private static void DrawReckoning(Graphics g, PlotEntry you, Func<double, double, PointF> at, float scale, float u, Font hand, Font handSmall, RectangleF map,
        IReadOnlyList<string> with, PlotEntry? fix)
    {
        var m = you.Mark;
        var centre = at(m.North, m.East);
        float r = Math.Max(8 * u, (float)m.Size * scale);
        int seed = Seed(m.MadeUtc.Ticks, 7);
        if (fix is { } f)
        {
            var (fn, fe, _) = f.Mark.Spread;
            using var trail = new Pen(Color.FromArgb(170, BluePencil), 1.2f * u) { DashStyle = DashStyle.Dot };
            g.DrawLine(trail, at(fn, fe), centre);
        }
        using (var dashed = new Pen(BluePencil, 1.7f * u) { DashStyle = DashStyle.Dash })
            PencilCircle(g, dashed, centre, r, u, seed);
        using (var cross = new Pen(BluePencil, 1.5f * u))
        {
            g.DrawLine(cross, centre.X - 4 * u, centre.Y, centre.X + 4 * u, centre.Y);
            g.DrawLine(cross, centre.X, centre.Y - 4 * u, centre.X, centre.Y + 4 * u);
        }
        Note(g, "DR", hand, BluePencil, centre.X + 5 * u, centre.Y - hand.Size * 1.3f, map);
        if (with.Count > 0)
            Note(g, $"{string.Join(", ", with)} with you", handSmall, MatePencil, centre.X + r * 0.7f + 4 * u, centre.Y + r * 0.5f, map);
    }

    private static void DrawMark(Graphics g, PlotEntry entry, Func<double, double, PointF> at, float scale, float u, Font hand, RectangleF map)
    {
        var m = entry.Mark;
        int a = Alpha(entry.Alpha);
        var (n, e, _) = m.Spread;
        var centre = at(n, e);
        if (!map.Contains(centre))
            return;
        var colour = m.Subject == PlotSubject.Mate || m.Side > 0 ? MatePencil : RedPencil; // a contact not yet made out is drawn as a threat
        var area = Shape(g, m, at, scale, u, colour, a * 160 / 255, dashed: m.LastSeen, hatch: false);
        using var pen = new Pen(Color.FromArgb(a, colour), 1.9f * u);
        if (m.Subject == PlotSubject.Mate)
        {
            PencilCircle(g, pen, centre, 5 * u, u, Seed(m.North, m.East));
        }
        else
        {
            // A contact as a pilot jots one: a small cross, and an arrow the way it was flying.
            g.DrawLine(pen, centre.X - 5 * u, centre.Y - 5 * u, centre.X + 5 * u, centre.Y + 5 * u);
            g.DrawLine(pen, centre.X - 5 * u, centre.Y + 5 * u, centre.X + 5 * u, centre.Y - 5 * u);
            if (m.Course is double course)
            {
                double c = course * Math.PI / 180;
                float len = 24 * u;
                var tip = new PointF(centre.X + (float)Math.Sin(c) * len, centre.Y - (float)Math.Cos(c) * len);
                using var arrow = new Pen(Color.FromArgb(a, colour), 2f * u) { CustomEndCap = new AdjustableArrowCap(3.5f, 4.5f) };
                g.DrawLine(arrow, centre, tip);
            }
        }
        string label = m.Subject == PlotSubject.Contact && m.LastSeen ? $"{m.Label}, last seen {entry.AgeText}" : $"{m.Label}  {entry.AgeText}";
        var size = Measure(g, label, hand);
        Note(g, label, hand, Color.FromArgb(a, colour), centre.X + 9 * u, Math.Max(centre.Y + 6 * u, area.Bottom - size.Height * 0.3f), map);
    }

    /// <summary>A cell (hatched or outlined) or a patch (a lightly shaded pencil ring); returns its bounds on the bitmap.</summary>
    private static RectangleF Shape(Graphics g, PlotMark m, Func<double, double, PointF> at, float scale, float u, Color colour, int alpha, bool dashed, bool hatch)
    {
        int seed = Seed(m.North, m.East);
        using var pen = new Pen(Color.FromArgb(alpha, colour), 1.5f * u);
        if (dashed)
            pen.DashStyle = DashStyle.Dash;
        if (m.Shape == PlotShape.Cell)
        {
            var nw = at(m.North + m.Size, m.East);
            float side = Math.Max(6 * u, (float)m.Size * scale);
            var rect = new RectangleF(nw.X, nw.Y, side, side);
            Hatch(g, Color.FromArgb(alpha * (hatch ? 150 : 90) / 255, colour), rect, ellipse: false, 3.2f * u, u);
            Pencil(g, pen, new PointF(rect.Left, rect.Top), new PointF(rect.Right, rect.Top), u, seed);
            Pencil(g, pen, new PointF(rect.Right, rect.Top), new PointF(rect.Right, rect.Bottom), u, seed + 1);
            Pencil(g, pen, new PointF(rect.Right, rect.Bottom), new PointF(rect.Left, rect.Bottom), u, seed + 2);
            Pencil(g, pen, new PointF(rect.Left, rect.Bottom), new PointF(rect.Left, rect.Top), u, seed + 3);
            return rect;
        }
        var c = at(m.North, m.East);
        float r = Math.Max(6 * u, (float)m.Size * scale);
        var circle = new RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r);
        using (var shade = new SolidBrush(Color.FromArgb(alpha * 30 / 255, colour)))
            g.FillEllipse(shade, circle);
        PencilCircle(g, pen, c, r, u, seed);
        return circle;
    }

    private static void OffSheet(Graphics g, string edge, RectangleF map, Font hand, float u)
    {
        string text = $"off the sheet, {edge} →";
        var size = Measure(g, text, hand);
        float x = edge.Contains("east") ? map.Right - size.Width - 4 * u : edge.Contains("west") ? map.Left + 4 * u : map.Left + (map.Width - size.Width) / 2;
        float y = edge.Contains("north") ? map.Top + 4 * u : edge.Contains("south") ? map.Bottom - size.Height - 4 * u : map.Top + (map.Height - size.Height) / 2;
        Text(g, text, hand, BluePencil, x, y);
    }

    /// <summary>A printed north arrow in the top-right corner of the map.</summary>
    private static void NorthArrow(Graphics g, RectangleF map, float u, Font font)
    {
        float x = map.Right - 16 * u, y = map.Top + 28 * u, h = 12 * u, w = 5 * u;
        using var ink = new SolidBrush(Print);
        using var paper = new SolidBrush(Paper);
        using var pen = new Pen(Print, Math.Max(1, 0.9f * u));
        var left = new[] { new PointF(x, y - h), new PointF(x - w, y + h * 0.6f), new PointF(x, y + h * 0.25f) };
        var right = new[] { new PointF(x, y - h), new PointF(x + w, y + h * 0.6f), new PointF(x, y + h * 0.25f) };
        g.FillPolygon(ink, left);
        g.FillPolygon(paper, right);
        g.DrawPolygon(pen, right);
        Centred(g, "N", font, Print, x, y - h - font.Size * 1.15f);
    }

    /// <summary>A period scale bar: 10 km in alternate black and white blocks of 2.5 km.</summary>
    private static void ScaleBar(Graphics g, RectangleF map, float scale, float u, Font font)
    {
        float length = (float)MapGrid.SquareMetres * scale;
        float right = map.Right - 10 * u, left = right - length, y = map.Bottom - 12 * u, h = 4 * u;
        using var ink = new SolidBrush(Print);
        using var paper = new SolidBrush(Paper);
        using var pen = new Pen(Print, Math.Max(1, 0.9f * u));
        g.FillRectangle(paper, left - 4 * u, y - font.Size * 1.3f - 2 * u, length + 8 * u, h + font.Size * 1.3f + 5 * u);
        float half = length / 2;
        g.FillRectangle(ink, left, y, half / 2, h);
        g.FillRectangle(ink, left + half, y, half / 2, h);
        g.DrawRectangle(pen, left, y, length, h);
        Centred(g, "10 km", font, Print, (left + right) / 2, y - Measure(g, "10 km", font).Height - 1 * u);
    }

    /// <summary>A typed key along the bottom of the large sheet.</summary>
    private static void Key(Graphics g, Font font, float u, float x, float y, float right)
    {
        float mid = y + font.Size * 0.55f, gap = 14 * u;
        using (var blue = new Pen(BluePencil, 1.3f * u))
        {
            var cell = new RectangleF(x, mid - 5 * u, 10 * u, 10 * u);
            Hatch(g, Color.FromArgb(150, BluePencil), cell, ellipse: false, 3 * u, u);
            g.DrawRectangle(blue, cell.X, cell.Y, cell.Width, cell.Height);
        }
        x += 14 * u;
        x += Text(g, "FIX", font, Print, x, y) + gap;
        using (var dashed = new Pen(BluePencil, 1.3f * u) { DashStyle = DashStyle.Dash })
            g.DrawEllipse(dashed, x, mid - 6 * u, 12 * u, 12 * u);
        x += 16 * u;
        x += Text(g, "DR", font, Print, x, y) + gap;
        using (var mate = new Pen(MatePencil, 1.6f * u))
            g.DrawEllipse(mate, x, mid - 5 * u, 10 * u, 10 * u);
        x += 14 * u;
        x += Text(g, "MATE", font, Print, x, y) + gap;
        using (var red = new Pen(RedPencil, 1.6f * u))
        {
            g.DrawLine(red, x, mid - 5 * u, x + 10 * u, mid + 5 * u);
            g.DrawLine(red, x, mid + 5 * u, x + 10 * u, mid - 5 * u);
        }
        x += 14 * u;
        x += Text(g, "CONTACT", font, Print, x, y) + gap;
        const string honesty = "NOT LIVE: RADIO REPLIES ONLY";
        float width = Measure(g, honesty, font).Width;
        if (x + width <= right)
            Text(g, honesty, font, Color.FromArgb(170, Print), right - width, y);
    }

    /// <summary>A pencil stroke: the line drawn twice with a slight, fixed wobble, the second pass lighter.</summary>
    private static void Pencil(Graphics g, Pen pen, PointF p, PointF q, float u, int seed)
    {
        float wobble = 0.9f * u;
        PointF Mid(int salt) => new((p.X + q.X) / 2 + Jitter(seed, salt) * wobble, (p.Y + q.Y) / 2 + Jitter(seed, salt + 1) * wobble);
        g.DrawCurve(pen, [p, Mid(1), q], 0.5f);
        using var second = (Pen)pen.Clone();
        second.Color = Color.FromArgb(pen.Color.A / 3, pen.Color);
        second.Width = pen.Width * 0.7f;
        var p2 = new PointF(p.X + Jitter(seed, 5) * wobble, p.Y + Jitter(seed, 6) * wobble);
        var q2 = new PointF(q.X + Jitter(seed, 7) * wobble, q.Y + Jitter(seed, 8) * wobble);
        g.DrawCurve(second, [p2, Mid(3), q2], 0.5f);
    }

    /// <summary>A hand-drawn ring: a closed curve whose radius wanders a few per cent, fixed by its seed.</summary>
    private static void PencilCircle(Graphics g, Pen pen, PointF c, float r, float u, int seed)
    {
        const int n = 14;
        var points = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            double t = i * 2 * Math.PI / n;
            float rr = r * (1 + 0.035f * Jitter(seed, i)) + 0.4f * u * Jitter(seed, i + 40);
            points[i] = new PointF(c.X + (float)Math.Cos(t) * rr, c.Y + (float)Math.Sin(t) * rr);
        }
        g.DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);
    }

    /// <summary>Diagonal pencil hatching inside a rectangle or ellipse.</summary>
    private static void Hatch(Graphics g, Color colour, RectangleF area, bool ellipse, float spacing, float u)
    {
        var state = g.Save();
        using (var clip = new GraphicsPath())
        {
            if (ellipse)
                clip.AddEllipse(area);
            else
                clip.AddRectangle(area);
            g.SetClip(clip, CombineMode.Intersect);
        }
        using var pen = new Pen(colour, Math.Max(0.8f, 0.9f * u));
        for (float d = -area.Height; d < area.Width; d += spacing)
            g.DrawLine(pen, area.Left + d, area.Bottom, area.Left + d + area.Height, area.Top);
        g.Restore(state);
    }

    /// <summary>A fixed number in [-1, 1] for a seed and salt, so a stroke's wobble is the same on every redraw.</summary>
    private static float Jitter(int seed, int salt)
    {
        unchecked
        {
            uint h = (uint)seed * 2654435761u ^ (uint)(salt + 1) * 2246822519u;
            h ^= h >> 15;
            h *= 3266489917u;
            h ^= h >> 13;
            return (h & 0xFFFF) / 32767.5f - 1;
        }
    }

    private static int Seed(double a, double b) => HashCode.Combine(Math.Round(a), Math.Round(b));

    private static int Alpha(double share) => (int)Math.Round(255 * Math.Clamp(share, 0, 1));

    private static SizeF Measure(Graphics g, string text, Font font) => g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);

    /// <summary>Draws text at (x, y), top-left, and returns its width.</summary>
    private static float Text(Graphics g, string text, Font font, Color colour, float x, float y)
    {
        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, x, y, StringFormat.GenericTypographic);
        return Measure(g, text, font).Width;
    }

    private static void Centred(Graphics g, string text, Font font, Color colour, float x, float y) =>
        Text(g, text, font, colour, x - Measure(g, text, font).Width / 2, y);

    /// <summary>Pencilled text kept inside the map: pushed left or up when it would run off the edge.</summary>
    private static void Note(Graphics g, string text, Font font, Color colour, float x, float y, RectangleF map)
    {
        var size = Measure(g, text, font);
        Text(g, text, font, colour, Math.Clamp(x, map.Left, Math.Max(map.Left, map.Right - size.Width)), Math.Clamp(y, map.Top, Math.Max(map.Top, map.Bottom - size.Height)));
    }

    /// <summary>Cuts a footer line to the sheet's width with an ellipsis.</summary>
    private static string Fit(Graphics g, string text, Font font, float width)
    {
        if (Measure(g, text, font).Width <= width)
            return text;
        for (int n = text.Length - 1; n > 0; n--)
        {
            string cut = text[..n].TrimEnd() + "…";
            if (Measure(g, cut, font).Width <= width)
                return cut;
        }
        return "…";
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = 2 * radius;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string? Installed(string name)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) ? name : null;
    }

    /// <summary>Saves a render to a PNG, for checking the look without IL-2 (Diag kneeboard-test).</summary>
    public static string? Save(KneeboardPicture picture, int screenHeight, string path)
    {
        using var bitmap = Render(picture, screenHeight);
        if (bitmap is null)
            return null;
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }
}
