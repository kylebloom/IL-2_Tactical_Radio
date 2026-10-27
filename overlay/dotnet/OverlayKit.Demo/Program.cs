// OverlayKit.Demo --target <exe name | pid> [--seconds N] [--capture] [--no-load] [--dll path]
//
// Loads the host DLL into the target (unless --no-load), then shows a ticking box at the top-left of
// its frames. --capture saves one frame, overlay included, and prints where.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using OverlayKit;

string? Value(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
string target = Value("--target") ?? "OverlayKitTestHost";
double seconds = double.TryParse(Value("--seconds"), out double s) ? s : 30;

var process = int.TryParse(target, out int pid)
    ? Process.GetProcessById(pid)
    : Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)).FirstOrDefault()
      ?? throw new InvalidOperationException($"{target} is not running");
string exe = Path.GetFileName(process.MainModule?.FileName ?? process.ProcessName + ".exe").ToLowerInvariant();
Console.WriteLine($"target: {exe} ({process.Id})");

if (!args.Contains("--no-load"))
    Console.WriteLine(OverlayInjector.EnsureLoaded(process, Value("--dll")) ? "host DLL loaded" : "host DLL was already loaded");

var clock = Stopwatch.StartNew();
OverlayHost? host = null;
while (clock.Elapsed < TimeSpan.FromSeconds(5) && (host = Overlay.Host(exe)) is null)
    Thread.Sleep(100);
if (host is null)
{
    Console.WriteLine($"no frames reported from {exe} yet; see {Overlay.LogPath(process.Id)}");
    return 1;
}
Console.WriteLine($"host live: {host.Width}x{host.Height}, {host.Frames} frames");

using var layer = new OverlayLayer("OverlayKit demo", exe);
layer.Place(OverlayAnchor.TopLeft, 40, 40);
layer.Visible = true;
Console.WriteLine($"layer slot {layer.Slot}");

using var font = new Font("Arial", 22, FontStyle.Regular, GraphicsUnit.Pixel);
using var bitmap = new Bitmap(520, 70, PixelFormat.Format32bppPArgb);
bool capture = args.Contains("--capture");
string capturePath = Overlay.CapturePath(process.Id);
var captureAfter = DateTime.MinValue;
while (clock.Elapsed < TimeSpan.FromSeconds(seconds) && !process.HasExited)
{
    using (var g = Graphics.FromImage(bitmap))
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var box = new SolidBrush(Color.FromArgb(107, 0, 0, 0));
        g.FillRectangle(box, 0, 0, bitmap.Width, bitmap.Height);
        using var pen = new Pen(Color.FromArgb(230, 255, 190, 60), 3);
        g.DrawRectangle(pen, 1.5f, 1.5f, bitmap.Width - 3, bitmap.Height - 3);
        g.DrawString($"OverlayKit  {DateTime.Now:HH:mm:ss.f}", font, Brushes.WhiteSmoke, 16, 20);
    }
    var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
    unsafe
    {
        layer.Update(new ReadOnlySpan<byte>((void*)data.Scan0, data.Stride * data.Height), data.Width, data.Height, data.Stride);
    }
    bitmap.UnlockBits(data);

    if (capture && captureAfter == DateTime.MinValue && clock.Elapsed > TimeSpan.FromSeconds(1.5))
    {
        captureAfter = DateTime.Now;
        Overlay.RequestCapture();
    }
    if (capture && captureAfter != DateTime.MinValue && File.Exists(capturePath) && File.GetLastWriteTime(capturePath) >= captureAfter)
    {
        Console.WriteLine($"capture: {capturePath}");
        capture = false;
    }
    Thread.Sleep(100);
}
host = Overlay.Host(exe);
Console.WriteLine($"done; host {(host is null ? "not live" : $"{host.Frames} frames")}");
return 0;
