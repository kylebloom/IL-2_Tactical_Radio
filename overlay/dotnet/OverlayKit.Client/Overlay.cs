using System.Diagnostics;

namespace OverlayKit;

/// <summary>Where a layer sits: its corner (or centre) is placed against the same corner of the game's frame.</summary>
public enum OverlayAnchor
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
    Center = 4,
}

/// <summary>A game process the host DLL is drawing in, as it last reported itself.</summary>
/// <param name="Width">Back-buffer width in pixels: the size to render layers for.</param>
public sealed record OverlayHost(int ProcessId, string ExeName, int Width, int Height, TimeSpan SinceLastFrame, ulong Frames)
{
    /// <summary>The host presented a frame in the last two seconds (a paused or minimised game may not).</summary>
    public bool Live => SinceLastFrame < TimeSpan.FromSeconds(2);
}

/// <summary>The shared directory and what the hosts report through it.</summary>
public static unsafe class Overlay
{
    private static readonly object Gate = new();
    private static SharedSection? _directory;

    internal static DirectoryBlock* Directory
    {
        get
        {
            lock (Gate)
            {
                if (_directory is null)
                {
                    Protocol.CheckLayout();
                    var section = SharedSection.CreateOrOpen(Protocol.DirectoryName, sizeof(DirectoryBlock));
                    var dir = (DirectoryBlock*)section.Pointer;
                    if (section.Created)
                    {
                        dir->Version = Protocol.Version;
                        dir->HostCapacity = Protocol.MaxHosts;
                        dir->LayerCapacity = Protocol.MaxLayers;
                        Thread.MemoryBarrier();
                        dir->Magic = Protocol.Magic;
                    }
                    else
                    {
                        // A host may have created it a moment ago and still be initialising it.
                        for (int i = 0; i < 50 && Volatile.Read(ref dir->Magic) == 0; i++)
                            Thread.Sleep(10);
                        if (dir->Magic != Protocol.Magic || dir->Version != Protocol.Version)
                        {
                            section.Dispose();
                            throw new InvalidOperationException($"OverlayKit shared memory has an unknown version ({dir->Version})");
                        }
                    }
                    _directory = section;
                }
                return (DirectoryBlock*)_directory.Pointer;
            }
        }
    }

    /// <summary>The game processes with the host DLL loaded, newest frame first.</summary>
    public static IReadOnlyList<OverlayHost> Hosts()
    {
        var dir = Directory;
        ulong now = Native.GetTickCount64();
        var hosts = new List<OverlayHost>();
        for (int i = 0; i < Protocol.MaxHosts; i++)
        {
            var h = DirectoryBlock.Host(dir, i);
            uint pid = h->Pid;
            if (pid == 0 || h->FrameCount == 0 || !Running((int)pid))
                continue;
            hosts.Add(new OverlayHost((int)pid, Ascii.Read(h->ExeName, 32), (int)h->Width, (int)h->Height,
                TimeSpan.FromMilliseconds(now - Math.Min(now, h->LastFrameTick)), h->FrameCount));
        }
        return hosts.OrderBy(h => h.SinceLastFrame).ToList();
    }

    /// <summary>The live host in a process with this exe name (e.g. "il-2.exe"), if any.</summary>
    public static OverlayHost? Host(string exeName) =>
        Hosts().FirstOrDefault(h => h.Live && string.Equals(h.ExeName, exeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Asks every host to save its next frame, overlay included, to <see cref="CapturePath"/>. Works in
    /// exclusive fullscreen, where screen captures see nothing.
    /// </summary>
    public static void RequestCapture() => Interlocked.Increment(ref *(int*)&Directory->CaptureRequest);

    /// <summary>Where the host in process <paramref name="pid"/> saves a capture (the game's %TEMP%, normally the same as ours).</summary>
    public static string CapturePath(int pid) => Path.Combine(Path.GetTempPath(), "OverlayKit", $"capture-{pid}.bmp");

    /// <summary>The host's log for process <paramref name="pid"/>.</summary>
    public static string LogPath(int pid) => Path.Combine(Path.GetTempPath(), "OverlayKit", $"host-{pid}.log");

    internal static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // exists, but not ours to query
        }
    }
}
