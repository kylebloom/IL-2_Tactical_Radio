using System.Diagnostics;
using System.Management;
using System.Security.Principal;

namespace Il2Radio.Diag;

/// <summary>
/// Reads a file that another process holds open without read sharing (IL-2's ACMI while a track is
/// recording) by taking a Volume Shadow Copy snapshot of its volume, through the documented
/// Win32_ShadowCopy WMI class. The game process is never touched. Requires administrator rights.
/// </summary>
internal static class ShadowCopy
{
    /// <summary>Snapshots this app created and has not deleted yet; cleaned up on the next start.</summary>
    private static readonly string LedgerPath = Path.Combine(AppContext.BaseDirectory, "shadow-copies.pending");

    public sealed record Timings(TimeSpan Create, TimeSpan Read, TimeSpan Delete)
    {
        public override string ToString() =>
            $"snapshot {Create.TotalMilliseconds:F0} ms, read {Read.TotalMilliseconds:F0} ms, delete {Delete.TotalMilliseconds:F0} ms";
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Snapshots the volume containing <paramref name="path"/>, reads bytes from <paramref name="offset"/>
    /// to the end of the file as it was at snapshot time, then deletes the snapshot.
    /// </summary>
    public static (byte[] Bytes, Timings Timings) ReadFrom(string path, long offset)
    {
        string full = Path.GetFullPath(path);
        string volume = Path.GetPathRoot(full) ?? throw new ArgumentException($"No volume root in {full}");

        var sw = Stopwatch.StartNew();
        string shadowId = Create(volume);
        var create = sw.Elapsed;
        try
        {
            sw.Restart();
            string device = DeviceObject(shadowId);
            string shadowPath = Path.Combine(device + Path.DirectorySeparatorChar, full[volume.Length..]);
            byte[] bytes;
            using (var fs = new FileStream(shadowPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long start = Math.Min(offset, fs.Length);
                fs.Position = start;
                bytes = new byte[fs.Length - start];
                fs.ReadExactly(bytes);
            }

            var read = sw.Elapsed;
            sw.Restart();
            Delete(shadowId);
            return (bytes, new Timings(create, read, sw.Elapsed));
        }
        catch
        {
            TryDelete(shadowId);
            throw;
        }
    }

    /// <summary>Deletes snapshots left behind by a previous run that was killed mid-read.</summary>
    public static int CleanUpLeftovers()
    {
        if (!File.Exists(LedgerPath))
            return 0;
        int removed = 0;
        foreach (string id in File.ReadAllLines(LedgerPath).Where(l => l.Length > 0))
            if (TryDelete(id))
                removed++;
        File.Delete(LedgerPath);
        return removed;
    }

    private static string Create(string volume)
    {
        using var shadowClass = new ManagementClass(@"root\cimv2", "Win32_ShadowCopy", null);
        using var inParams = shadowClass.GetMethodParameters("Create");
        inParams["Volume"] = volume;
        inParams["Context"] = "ClientAccessible";
        using var outParams = shadowClass.InvokeMethod("Create", inParams, null);

        uint result = (uint)outParams["ReturnValue"];
        if (result != 0)
            throw new InvalidOperationException($"Win32_ShadowCopy.Create failed with code {result} ({Describe(result)}).");

        string id = (string)outParams["ShadowID"];
        File.AppendAllLines(LedgerPath, [id]);
        return id;
    }

    private static string DeviceObject(string shadowId)
    {
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");
        foreach (ManagementObject shadow in searcher.Get())
            using (shadow)
                return (string)shadow["DeviceObject"]; // \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN
        throw new InvalidOperationException($"Shadow copy {shadowId} not found after creation.");
    }

    private static void Delete(string shadowId)
    {
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", $"SELECT * FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");
        foreach (ManagementObject shadow in searcher.Get())
            using (shadow)
                shadow.Delete();

        var remaining = File.Exists(LedgerPath) ? File.ReadAllLines(LedgerPath).Where(l => l != shadowId && l.Length > 0).ToArray() : [];
        if (remaining.Length == 0)
            File.Delete(LedgerPath);
        else
            File.WriteAllLines(LedgerPath, remaining);
    }

    private static bool TryDelete(string shadowId)
    {
        try
        {
            Delete(shadowId);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Describe(uint code) => code switch
    {
        1 => "access denied — run as administrator",
        2 => "invalid argument",
        3 => "volume not found",
        4 => "volume not supported",
        5 => "unsupported shadow copy context",
        6 => "insufficient storage",
        7 => "volume in use",
        8 => "maximum number of shadow copies reached",
        9 => "another shadow copy operation is in progress",
        10 => "shadow copy provider vetoed the operation",
        11 => "shadow copy provider not registered",
        12 => "shadow copy provider failure",
        _ => "unknown error",
    };
}
