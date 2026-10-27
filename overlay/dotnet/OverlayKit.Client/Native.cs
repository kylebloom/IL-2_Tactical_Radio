using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OverlayKit;

/// <summary>A named, pagefile-backed shared-memory section mapped into this process.</summary>
internal sealed unsafe class SharedSection : IDisposable
{
    private IntPtr _handle;

    public byte* Pointer { get; private set; }
    public long Size { get; }
    public bool Created { get; }

    private SharedSection(IntPtr handle, byte* pointer, long size, bool created)
    {
        _handle = handle;
        Pointer = pointer;
        Size = size;
        Created = created;
    }

    /// <summary>Creates the section, or opens it if it exists, with access for every interactive user.</summary>
    public static SharedSection CreateOrOpen(string name, long size)
    {
        IntPtr descriptor = IntPtr.Zero;
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(Protocol.MappingSddl, 1, out descriptor, IntPtr.Zero))
            throw new Win32Exception();
        try
        {
            var attributes = new Native.SecurityAttributes { Length = sizeof(Native.SecurityAttributes), Descriptor = descriptor };
            IntPtr handle = Native.CreateFileMappingW(new IntPtr(-1), ref attributes, Native.PAGE_READWRITE,
                (uint)(size >> 32), (uint)size, name);
            int error = Marshal.GetLastWin32Error();
            if (handle == IntPtr.Zero)
                throw new Win32Exception(error, $"CreateFileMapping {name}");
            return Map(handle, size, created: error != Native.ERROR_ALREADY_EXISTS);
        }
        finally
        {
            Native.LocalFree(descriptor);
        }
    }

    private static SharedSection Map(IntPtr handle, long size, bool created)
    {
        IntPtr view = Native.MapViewOfFile(handle, Native.FILE_MAP_READ | Native.FILE_MAP_WRITE, 0, 0, (UIntPtr)(ulong)size);
        if (view == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Native.CloseHandle(handle);
            throw new Win32Exception(error, "MapViewOfFile");
        }
        return new SharedSection(handle, (byte*)view, size, created);
    }

    public void Dispose()
    {
        if (Pointer != null)
            Native.UnmapViewOfFile((IntPtr)Pointer);
        if (_handle != IntPtr.Zero)
            Native.CloseHandle(_handle);
        Pointer = null;
        _handle = IntPtr.Zero;
    }
}

internal static class Native
{
    public const uint PAGE_READWRITE = 0x04;
    public const uint FILE_MAP_WRITE = 0x0002, FILE_MAP_READ = 0x0004;
    public const int ERROR_ALREADY_EXISTS = 183;

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileMappingW(IntPtr file, ref SecurityAttributes attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UnmapViewOfFile(IntPtr address);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    // Loading the host DLL into a game.
    public const uint PROCESS_CREATE_THREAD = 0x0002, PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_WRITE = 0x0020,
        PROCESS_VM_READ = 0x0010, PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    public const uint WAIT_OBJECT_0 = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsWow64Process(IntPtr process, out bool wow64);
}
