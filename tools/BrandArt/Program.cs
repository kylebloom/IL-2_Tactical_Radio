using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Il2Radio.App;

// Run from anywhere inside the repository; paths are found from this file's project folder.
string root = FindRoot(AppContext.BaseDirectory);
string icoPath = Path.Combine(root, "src", "Il2Radio.App", "app.ico");
string images = Path.Combine(root, "docs", "images");
Directory.CreateDirectory(images);

WriteIcon(icoPath, [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]);
Console.WriteLine($"wrote {icoPath}");

using (var emblem = Render(256, 256, g => Brand.DrawEmblem(g, new RectangleF(0, 0, 256, 256))))
    emblem.Save(Path.Combine(images, "emblem.png"), ImageFormat.Png);
Console.WriteLine($"wrote {Path.Combine(images, "emblem.png")}");

// The header at twice its 96-DPI size, as wide as a README column shows it.
using (var banner = Render(1600, 192, g => Brand.PaintBanner(g, new Rectangle(0, 0, 1600, 192), "", 2f)))
    banner.Save(Path.Combine(images, "banner.png"), ImageFormat.Png);
Console.WriteLine($"wrote {Path.Combine(images, "banner.png")}");

static Bitmap Render(int width, int height, Action<Graphics> draw)
{
    var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.Clear(Color.Transparent);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    draw(g);
    return bitmap;
}

// An .ico of PNG frames (Windows Vista and later read them at every size).
static void WriteIcon(string path, int[] sizes)
{
    var frames = sizes.Select(size =>
    {
        using var bitmap = Render(size, size, g => Brand.DrawEmblem(g, new RectangleF(0, 0, size, size)));
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return (size, data: png.ToArray());
    }).ToList();

    using var file = new BinaryWriter(File.Create(path));
    file.Write((short)0);
    file.Write((short)1);
    file.Write((short)frames.Count);
    int offset = 6 + 16 * frames.Count;
    foreach (var (size, data) in frames)
    {
        file.Write((byte)(size >= 256 ? 0 : size));
        file.Write((byte)(size >= 256 ? 0 : size));
        file.Write((byte)0);
        file.Write((byte)0);
        file.Write((short)1);
        file.Write((short)32);
        file.Write(data.Length);
        file.Write(offset);
        offset += data.Length;
    }

    foreach (var (_, data) in frames)
        file.Write(data);
}

static string FindRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Il2Radio.slnx")))
            return dir.FullName;
    throw new InvalidOperationException("Run this inside the repository (Il2Radio.slnx not found above " + start + ").");
}
