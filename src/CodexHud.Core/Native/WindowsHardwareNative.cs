using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexHud.Core.Native;

internal static class WindowsHardwareNative
{
    internal const uint PdhValidData = 0;
    internal const uint PdhNewData = 1;
    internal const uint PdhMoreData = 0x800007D2;
    internal const uint PdhNoInstance = 0x800007D1;
    internal const uint PdhNoData = 0x800007D5;
    internal const uint PdhDouble = 0x200;
    internal const uint PdhNoCap100 = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        internal uint Low;
        internal uint High;
        internal readonly long Ticks => unchecked((long)(((ulong)High << 32) | Low));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry
    {
        internal uint Size, Usage, Pid;
        internal UIntPtr DefaultHeap;
        internal uint ModuleId, Threads, ParentPid;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        internal ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMemoryCountersEx2
    {
        internal uint Size, PageFaultCount;
        internal UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool;
        internal UIntPtr QuotaPeakNonPagedPool, QuotaNonPagedPool, PagefileUsage, PeakPagefileUsage;
        internal UIntPtr PrivateUsage, PrivateWorkingSet;
        internal ulong SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhValue
    {
        internal uint Status;
        internal double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhArrayItem
    {
        internal IntPtr Name;
        internal PdhValue Value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeKernelHandle CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(SafeKernelHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(SafeKernelHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemoryCountersEx2 counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounter(SafePdhQuery query, string path, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(SafePdhQuery query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint counterType, out PdhValue value);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(IntPtr query);

    internal static IReadOnlyList<ProcessEntry> EnumerateProcesses()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new List<ProcessEntry>();
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
        if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do { result.Add(entry); } while (Process32Next(snapshot, ref entry));
        var error = Marshal.GetLastWin32Error();
        if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
        return result;
    }

    internal static SafeProcessHandle? OpenReadableProcess(int pid)
    {
        var handle = OpenProcess(0x1000 | 0x10, false, pid); // QUERY_LIMITED_INFORMATION | VM_READ
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        handle = OpenProcess(0x1000, false, pid);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    internal static string? ReadImagePath(SafeProcessHandle process)
    {
        var name = new StringBuilder(32768);
        uint length = (uint)name.Capacity;
        return QueryFullProcessImageName(process, 0, name, ref length) ? name.ToString() : null;
    }
}

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeKernelHandle() : base(true) { }
    protected override bool ReleaseHandle() => WindowsHardwareNative.CloseHandle(handle);
}

internal sealed class SafePdhQuery : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafePdhQuery(IntPtr value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => WindowsHardwareNative.PdhCloseQuery(handle) == 0;
}
