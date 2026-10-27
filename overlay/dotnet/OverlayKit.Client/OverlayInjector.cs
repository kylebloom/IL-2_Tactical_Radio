using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OverlayKit;

/// <summary>
/// Loads the host DLL (overlaykit64.dll) into a 64-bit Direct3D 11 game with LoadLibraryW on a remote
/// thread. The DLL pins itself and stays until the game exits; loading it again is a no-op. This is
/// the one step that touches the game process, so callers should ask the player first.
/// </summary>
public static class OverlayInjector
{
    public const string DllName = "overlaykit64.dll";

    /// <summary>
    /// A copy of the host DLL a game can load: the one built into this library, written once under
    /// %LOCALAPPDATA%\OverlayKit with its hash in the name (a loaded DLL is locked, so a new build gets
    /// a new file), else overlaykit64.dll beside the application.
    /// </summary>
    public static string HostDllPath()
    {
        using var embedded = typeof(OverlayInjector).Assembly.GetManifestResourceStream(DllName);
        if (embedded is null)
        {
            string beside = Path.Combine(AppContext.BaseDirectory, DllName);
            return File.Exists(beside) ? beside : throw new FileNotFoundException($"{DllName} is neither built into OverlayKit.Client nor beside the application (run overlay\\build.ps1)", beside);
        }
        var bytes = new byte[embedded.Length];
        embedded.ReadExactly(bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverlayKit");
        string path = Path.Combine(dir, $"overlaykit64-{hash}.dll");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(dir);
            string temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, bytes);
            try { File.Move(temp, path); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temp); } // another client won the race
        }
        return path;
    }

    /// <summary>Whether an OverlayKit host DLL is already loaded in <paramref name="process"/>.</summary>
    public static bool IsLoaded(Process process)
    {
        try
        {
            process.Refresh();
            return process.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.StartsWith("overlaykit64", StringComparison.OrdinalIgnoreCase));
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Loads the host DLL into <paramref name="process"/> unless it is already there. Returns false when
    /// it was already loaded. Throws <see cref="Win32Exception"/> when the process can't be opened
    /// (a game running as administrator needs an elevated client) or the load fails.
    /// </summary>
    public static bool EnsureLoaded(Process process, string? dllPath = null)
    {
        if (IsLoaded(process))
            return false;
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Loading the 64-bit host DLL needs a 64-bit client");
        dllPath = Path.GetFullPath(dllPath ?? HostDllPath());

        IntPtr handle = Native.OpenProcess(Native.PROCESS_CREATE_THREAD | Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_WRITE
            | Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION, false, process.Id);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {process.ProcessName} ({process.Id})");
        try
        {
            if (Native.IsWow64Process(handle, out bool wow64) && wow64)
                throw new PlatformNotSupportedException($"{process.ProcessName} is a 32-bit process; OverlayKit's host is 64-bit only");

            byte[] path = Encoding.Unicode.GetBytes(dllPath + "\0");
            IntPtr remote = Native.VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)path.Length, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (remote == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx");
            try
            {
                if (!Native.WriteProcessMemory(handle, remote, path, (UIntPtr)path.Length, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory");
                // kernel32 sits at the same address in every process of this boot session.
                IntPtr loadLibrary = Native.GetProcAddress(Native.GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
                IntPtr thread = Native.CreateRemoteThread(handle, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out _);
                if (thread == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread");
                try
                {
                    if (Native.WaitForSingleObject(thread, 10_000) != Native.WAIT_OBJECT_0)
                        throw new TimeoutException($"Loading {DllName} into {process.ProcessName} did not finish in 10 s");
                    Native.GetExitCodeThread(thread, out uint module);
                    if (module == 0)
                        throw new Win32Exception($"LoadLibrary of {dllPath} failed inside {process.ProcessName} (see {Overlay.LogPath(process.Id)})");
                }
                finally
                {
                    Native.CloseHandle(thread);
                }
            }
            finally
            {
                Native.VirtualFreeEx(handle, remote, UIntPtr.Zero, Native.MEM_RELEASE);
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }
        return true;
    }
}
