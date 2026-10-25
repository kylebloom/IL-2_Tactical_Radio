using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Il2Radio.Diag;

/// <summary>
/// Watches the loudness of IL-2's own audio session (Il-2.exe on the default output device) with
/// the Windows audio-session peak meter, to guess when IL-2's AI radio is talking. Only reads the
/// meter Windows keeps for the mixer; nothing touches the game.
///
/// IL-2 mixes radio, engine and guns into one session, so radio can't be told apart directly.
/// The heuristic: the engine is a steady floor, a transmission rises clearly above it. A slow
/// baseline follows the floor; the channel counts as busy while the short-term peak is
/// <see cref="Ratio"/> times above it, and for <see cref="Hang"/> after (the pauses between words).
/// Gunfire also counts as busy, which is no bad thing for holding a reply. UNVERIFIED against real
/// IL-2 chatter: the monitor shows the levels so the threshold can be tuned with --radio-wait-ratio.
/// </summary>
internal sealed class Il2AudioMeter : IDisposable
{
    public const string ProcessName = "Il-2";
    public static readonly TimeSpan Hang = TimeSpan.FromSeconds(0.8);

    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private IAudioMeterInformation? _meter;
    private int _meterPid;
    private DateTime _nextLookupUtc;
    private DateTime _busyUntilUtc;
    private double _baseline = -1;
    private double _peak;

    private readonly string _processName;

    public Il2AudioMeter(double ratio = 1.8, double floor = 0.04, string processName = ProcessName)
    {
        _processName = processName;
        Ratio = ratio;
        Floor = floor;
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
                return _meter is null
                    ? $"IL-2 audio: no {_processName}.exe audio session yet (replies are not held)"
                    : $"IL-2 audio: peak {_peak:F2}, baseline {_baseline:F2}, ratio {Ratio:F1} -> {(DateTime.UtcNow < _busyUntilUtc ? "BUSY, holding replies" : "clear")}";
        }
    }

    private void Sample()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            try
            {
                if (_meter is null || !Alive(_meterPid))
                {
                    if (now < _nextLookupUtc)
                        return;
                    _nextLookupUtc = now.AddSeconds(3);
                    Release();
                    (_meter, _meterPid) = FindSession(_processName);
                    if (_meter is null)
                        return;
                }

                _meter.GetPeakValue(out float value);
                _peak = value;
            }
            catch (COMException)
            {
                Release(); // device changed or session closed; look again
                return;
            }

            // Follow the floor quickly downwards and slowly upwards (~10 s), so a transmission barely moves it.
            _baseline = _baseline < 0 ? _peak : _peak < _baseline ? _baseline + (_peak - _baseline) * 0.2 : _baseline + (_peak - _baseline) * 0.005;
            if (_peak >= Floor && _peak > _baseline * Ratio)
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

    /// <summary>The peak meter of Il-2.exe's session on the default render device, if it has one.</summary>
    private static (IAudioMeterInformation?, int) FindSession(string processName)
    {
        var pids = Process.GetProcessesByName(processName).Select(p => { using (p) return p.Id; }).ToHashSet();
        if (pids.Count == 0)
            return (null, 0);

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0)
                return (null, 0);
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object managerObject);
            var manager = (IAudioSessionManager2)managerObject;
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out var control);
                if (control is IAudioSessionControl2 control2 && control2.GetProcessId(out uint pid) == 0 && pids.Contains((int)pid))
                    return ((IAudioMeterInformation)control, (int)pid);
                Marshal.ReleaseComObject(control);
            }
            return (null, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private void Release()
    {
        if (_meter is not null)
            Marshal.ReleaseComObject(_meter);
        _meter = null;
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
