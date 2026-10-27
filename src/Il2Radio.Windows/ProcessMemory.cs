using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2Radio.Core.Memory;

namespace Il2Radio.Windows;

/// <summary>
/// Reads another process's memory from outside it (ReadProcessMemory). The handle is opened with
/// query and read rights only, so this class cannot write to the game or run code in it.
/// </summary>
internal sealed class ProcessMemory : IProcessMemory, IDisposable
{
    public const string Il2ProcessName = "Il-2";

    private const uint ProcessQueryInformation = 0x0400, ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000, MemImage = 0x1000000;
    private const uint PageNoAccess = 0x01, PageGuard = 0x100;
    private const uint PageReadWrite = 0x04, PageWriteCopy = 0x08, PageExecute = 0x10, PageExecuteRead = 0x20,
        PageExecuteReadWrite = 0x40, PageExecuteWriteCopy = 0x80;

    private readonly IntPtr _handle;
    private IReadOnlyList<MemoryRegion>? _regions;

    private ProcessMemory(Process process, IntPtr handle, IReadOnlyList<MemoryModule> modules)
    {
        Process = process;
        _handle = handle;
        Modules = modules;
    }

    public Process Process { get; }

    public IReadOnlyList<MemoryModule> Modules { get; }

    /// <summary>Listed once, on first use; call <see cref="RefreshRegions"/> after the game allocates more.</summary>
    public IReadOnlyList<MemoryRegion> Regions => _regions ??= QueryRegions();

    public void RefreshRegions() => _regions = null;

    /// <summary>The newest running process called <paramref name="name"/> (without ".exe"), or the one with <paramref name="pid"/>; null when none runs.</summary>
    /// <exception cref="InvalidOperationException">It runs but can't be opened (usually: needs administrator).</exception>
    public static ProcessMemory? Open(string name, int? pid = null)
    {
        if (!Environment.Is64BitProcess)
            throw new InvalidOperationException("Reading a 64-bit game's memory needs a 64-bit process.");
        var process = Process.GetProcessesByName(name).Where(p => pid is null || p.Id == pid).OrderByDescending(p => p.StartTime).FirstOrDefault();
        if (process is null)
            return null;

        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Cannot open {name}.exe (pid {process.Id}) for reading: Windows error {Marshal.GetLastWin32Error()}. Run from an administrator terminal.");

        var modules = new List<MemoryModule>();
        try
        {
            foreach (ProcessModule m in process.Modules)
                modules.Add(new MemoryModule(m.ModuleName, (ulong)m.BaseAddress, (ulong)m.ModuleMemorySize));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Module list unavailable: addresses are shown raw instead of module+offset.
        }
        return new ProcessMemory(process, handle, modules);
    }

    public unsafe int Read(ulong address, Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;
        fixed (byte* p = buffer)
        {
            if (!ReadProcessMemory(_handle, (IntPtr)(long)address, p, (nuint)buffer.Length, out nuint read))
                return 0;
            return (int)read;
        }
    }

    private List<MemoryRegion> QueryRegions()
    {
        var regions = new List<MemoryRegion>();
        ulong address = 0;
        int size = Marshal.SizeOf<MemoryBasicInformation>();
        while (VirtualQueryEx(_handle, (IntPtr)(long)address, out var info, (nuint)size) != 0)
        {
            ulong next = info.BaseAddress + info.RegionSize;
            uint protect = info.Protect;
            bool readable = info.State == MemCommit && (protect & PageGuard) == 0 && (protect & 0xFF) is not PageNoAccess and not PageExecute and not 0;
            if (readable)
            {
                uint access = protect & 0xFF;
                bool writable = access is PageReadWrite or PageWriteCopy or PageExecuteReadWrite or PageExecuteWriteCopy;
                bool executable = access is PageExecuteRead or PageExecuteReadWrite or PageExecuteWriteCopy;
                string? module = info.Type == MemImage ? Modules.FirstOrDefault(m => m.Contains(info.BaseAddress))?.Name : null;
                regions.Add(new MemoryRegion(info.BaseAddress, info.RegionSize, writable, executable, module));
            }
            if (next <= address)
                break;
            address = next;
        }
        return regions;
    }

    public void Dispose()
    {
        CloseHandle(_handle);
        Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadProcessMemory(IntPtr process, IntPtr address, byte* buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
