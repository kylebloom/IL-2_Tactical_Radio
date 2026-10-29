using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2Radio.Core.Audio;

namespace Il2Radio.Windows;

/// <summary>
/// Watches the loudness of IL-2's own audio session (Il-2.exe on the default output device) with
/// the Windows audio-session peak meter, to guess when IL-2's AI radio is talking. Only reads the
/// meter Windows keeps for the mixer; nothing touches the game.
///
/// IL-2 mixes radio, engine and guns into one session, so radio can't be told apart by a level alone.
/// The engine is a steady floor. A transmission either jumps <see cref="Ratio"/> times above that
/// floor, or flutters (the peak rising and falling within half a second) the way speech does.
/// The floor follows a steady level and stays put while the peak is fluttering, so ongoing chatter
/// does not become the new floor. Every audio session of the process is watched; the loudest peak
/// counts. The channel stays busy for <see cref="Hang"/> after (the pauses between words). Gunfire
/// also counts as busy, which is no bad thing for holding a reply. The 2026-10-28 flight held two
/// replies in 55 minutes on the jump test alone: speech under a loud engine never cleared that bar.
/// </summary>
internal sealed class Il2AudioMeter : IDisposable
{
    public const string ProcessName = "Il-2";
    public static readonly TimeSpan Hang = TimeSpan.FromSeconds(1.2);

    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly List<(IAudioMeterInformation Meter, int Pid)> _meters = new();
    private readonly TransmissionDetector _detector;
    private DateTime _nextLookupUtc;
    private DateTime _busyUntilUtc;
    private double _peak;

    private readonly string _processName;

    public Il2AudioMeter(double ratio = 1.8, double floor = 0.04, string processName = ProcessName)
    {
        _processName = processName;
        Ratio = ratio;
        Floor = floor;
        _detector = new TransmissionDetector(ratio, floor);
        _timer = new System.Threading.Timer(_ => Sample(), null, 0, 50);
    }

    /// <summary>How far above the baseline a peak must rise to count as a transmission.</summary>
    public double Ratio { get; }

    /// <summary>Peaks below this never count, so a quiet game (paused, menus) is never busy.</summary>
    public double Floor { get; }

    /// <summary>True while IL-2 seems to be transmitting.</summary>
    public bool Busy
    {
        get { lock (_gate) return DateTime.UtcNow < _busyUntilUtc; }
    }

    /// <summary>One line for the monitor header.</summary>
    public string Status
    {
        get
        {
            lock (_gate)
                return _meters.Count == 0
                    ? $"IL-2 audio: no {_processName}.exe audio session yet (replies are not held)"
                    : $"IL-2 audio: peak {_peak:F2}, baseline {_detector.Baseline:F2}, ratio {Ratio:F1}, {_meters.Count} session(s) -> {(DateTime.UtcNow < _busyUntilUtc ? "BUSY, holding replies" : "clear")}";
        }
    }

    private void Sample()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            try
            {
                if (_meters.Count == 0 || _meters.Exists(m => !Alive(m.Pid)))
                {
                    if (_meters.Count == 0 && now < _nextLookupUtc)
                        return;
                    _nextLookupUtc = now.AddSeconds(3);
                    Release();
                    _meters.AddRange(FindSessions(_processName));
                    if (_meters.Count == 0)
                        return;
                }

                double peak = 0;
                foreach (var (meter, _) in _meters)
                {
                    meter.GetPeakValue(out float value);
                    if (value > peak)
                        peak = value;
                }

                _peak = peak;
            }
            catch (COMException)
            {
                Release(); // device changed or session closed; look again
                return;
            }

            if (_detector.Observe(_peak))
                _busyUntilUtc = now + Hang;
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The peak meters of every Il-2.exe session on the default render device.</summary>
    private static List<(IAudioMeterInformation Meter, int Pid)> FindSessions(string processName)
    {
        var found = new List<(IAudioMeterInformation, int)>();
        var pids = Process.GetProcessesByName(processName).Select(p => { using (p) return p.Id; }).ToHashSet();
        if (pids.Count == 0)
            return found;

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0)
                return found;
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object managerObject);
            var manager = (IAudioSessionManager2)managerObject;
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out var control);
                if (control is IAudioSessionControl2 control2 && control2.GetProcessId(out uint pid) == 0 && pids.Contains((int)pid))
                    found.Add(((IAudioMeterInformation)control, (int)pid));
                else
                    Marshal.ReleaseComObject(control);
            }
            return found;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private void Release()
    {
        foreach (var (meter, _) in _meters)
            Marshal.ReleaseComObject(meter);
        _meters.Clear();
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
            Release();
    }

    // Core Audio COM interop (mmdeviceapi.h, audiopolicy.h, endpointvolume.h). Method order follows the vtables.
    private const int ClsctxAll = 0x17;

    private enum EDataFlow { Render = 0 }

    private enum ERole { Multimedia = 1 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
        void GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);
        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid overrideValue, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }
}
