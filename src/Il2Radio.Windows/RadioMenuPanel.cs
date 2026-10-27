using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Il2Radio.Core.Radio;

namespace Il2Radio.Windows;

/// <summary>
/// Draws the radio menu as one panel in the style of IL-2's message line (<see cref="Il2ChatStyle"/>):
/// the path as a dim title, a rule, one row per entry ("1.  Flight ›"), and the keys that leave it.
/// Used by <see cref="InGameChat"/>, which shows it as a layer of its own above the radio log.
/// </summary>
internal static class RadioMenuPanel
{
    /// <summary>A little darker than a chat line's box, so the entries read over a bright sky.</summary>
    private const int PanelAlpha = 150;

    /// <summary>Renders <paramref name="view"/> onto a premultiplied bitmap sized to its text.</summary>
    public static Bitmap Render(RadioMenuView view, Font font)
    {
        var rows = new List<(string Text, int Rgb)> { (view.Title, Il2ChatStyle.SystemRgb) };
        rows.AddRange(view.Entries.Select(e => ($"{e.Digit}.  {e.Label}{(e.IsSubmenu ? "  ›" : "")}", Il2ChatStyle.TextRgb)));
        if (view.Footer.Length > 0)
            rows.Add((view.Footer, Il2ChatStyle.SystemRgb));

        float cap = font.Size * Il2ChatStyle.CapHeightEm;
        float pad = cap * Il2ChatStyle.PaddingCaps;
        float row = cap * Il2ChatStyle.BoxHeightCaps;
        float rule = Math.Max(1, cap * 0.12f);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap };

        float textWidth, lineHeight;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
        {
            textWidth = rows.Max(r => g.MeasureString(r.Text, font, PointF.Empty, format).Width);
            lineHeight = font.GetHeight(g);
        }

        var bitmap = new Bitmap((int)Math.Ceiling(textWidth + 2 * pad), (int)Math.Ceiling(row * rows.Count + rule), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var box = new SolidBrush(Color.FromArgb(PanelAlpha, Color.FromArgb(Il2ChatStyle.BoxRgb))))
                g.FillRectangle(box, 0, 0, bitmap.Width, bitmap.Height);

            float y = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                // Text as a filled path, like the chat: it antialiases correctly onto a transparent bitmap.
                using var path = new GraphicsPath();
                path.AddString(rows[i].Text, font.FontFamily, (int)font.Style, font.Size, new PointF(pad, y + (row - lineHeight) / 2), format);
                using var fill = new SolidBrush(Color.FromArgb(rows[i].Rgb | unchecked((int)0xFF000000)));
                g.FillPath(fill, path);
                y += row;
                if (i == 0)
                {
                    using var line = new SolidBrush(Color.FromArgb(90, Color.FromArgb(Il2ChatStyle.TextRgb)));
                    g.FillRectangle(line, pad, y, bitmap.Width - 2 * pad, rule);
                    y += rule;
                }
            }
        }
        return bitmap;
    }
}
