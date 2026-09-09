using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexHud.Core.Native;

/// <summary>Read-only Darwin APIs. No task_for_pid, elevated helper, shell, or private GPU counters.</summary>
internal static class MacHardwareNative
{
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const string ProcLibrary = "/usr/lib/libproc.dylib";

    // Public XNU ABI: mach/host_info.h, mach/vm_statistics.h, sys/proc_info.h.
    // https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/proc_info.h
    // Explicit layouts leave unused native fields in place without marshalling fixed character arrays.
    [StructLayout(LayoutKind.Sequential)]
    internal struct CpuTicks
    {
        internal uint User, System, Idle, Nice;
    }

    [StructLayout(LayoutKind.Explicit, Size = 152)]
    private struct VmStatistics64
    {
        [FieldOffset(0)] internal uint FreePages;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct ProcessEntry
    {
        [FieldOffset(0)] internal uint Pid;
        [FieldOffset(4)] internal uint ParentPid;
        [FieldOffset(16)] private ulong _nameLow;
        [FieldOffset(24)] private ulong _nameHigh;
        [FieldOffset(36)] internal uint UserId;

        internal readonly string Name
        {
            get
            {
                Span<byte> bytes = stackalloc byte[16];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, _nameLow);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], _nameHigh);
                var end = bytes.IndexOf((byte)0);
                return Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes);
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 232)]
    private struct ProcessTaskAllInfo
    {
        [FieldOffset(12)] internal uint Pid;
        [FieldOffset(16)] internal uint ParentPid;
        [FieldOffset(120)] internal ulong StartedSeconds;
        [FieldOffset(128)] internal ulong StartedMicroseconds;
        [FieldOffset(144)] internal ulong ResidentBytes;
        [FieldOffset(152)] internal ulong UserTime;
        [FieldOffset(160)] internal ulong SystemTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimebaseInfo
    {
        internal uint Numerator, Denominator;
    }

    internal sealed record ProcessObservation(ProcessIdentity Identity, int ParentPid, ulong CpuTime, ulong ResidentBytes);

    internal sealed class Host : IDisposable
    {
        private uint _host;
        private readonly uint _task;

        internal Host()
        {
            // mach_task_self() is a C macro referring to this exported global, not a callable function.
            var library = NativeLibrary.Load(SystemLibrary);
            try { _task = unchecked((uint)Marshal.ReadInt32(NativeLibrary.GetExport(library, "mach_task_self_"))); }
            finally { NativeLibrary.Free(library); }
            _host = mach_host_self();
            if (_host == 0 || _task == 0) { Dispose(); throw new InvalidOperationException("Mach host port 不可用"); }
        }

        internal CpuTicks ReadCpu()
        {
            uint count = 4;
            CheckMach(host_statistics(_host, 3, out var info, ref count), "系统 CPU");
            if (count < 4) throw new InvalidOperationException("系统 CPU 计数不完整");
            return info;
        }

        internal (double Used, double Total) ReadMemory()
        {
            var total = ReadUInt64Sysctl("hw.memsize");
            // REV1 is supported before the newer swapped_count extension; count is in 32-bit words.
            uint count = 38;
            CheckMach(host_statistics64(_host, 4, out var info, ref count), "系统内存");
            CheckMach(host_page_size(_host, out var pageSize), "系统页大小");
            if (count < 4 || pageSize == 0 || total == 0) throw new InvalidOperationException("系统内存计数不完整");
            // free_count already includes speculative pages. Subtract it exactly once.
            var free = Math.Min((double)total, (double)info.FreePages * pageSize);
            return (total - free, total);
        }

        public void Dispose()
        {
            if (_host == 0) return;
            if (_task != 0) _ = mach_port_deallocate(_task, _host);
            _host = 0;
        }
    }

    internal static (IReadOnlyList<ProcessEntry> Entries, HashSet<int> Unreadable) ReadProcessTable(CancellationToken token)
    {
        var userId = geteuid(); // Scope discovery to the current desktop user (PROC_UID_ONLY).
        var required = proc_listpids(4, userId, null, 0);
        if (required <= 0) throw LastError("进程列表");
        int[]? pids = null;
        var returned = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var count = checked(required / sizeof(int) + 256);
            if (count > 262144) throw new InvalidOperationException("进程列表超出采样上限");
            pids = new int[count];
            returned = proc_listpids(4, userId, pids, checked(pids.Length * sizeof(int)));
            if (returned <= 0) throw LastError("进程列表");
            if (returned < pids.Length * sizeof(int)) break;
            required = checked(returned * 2);
            if (attempt == 2) throw new InvalidOperationException("进程列表持续变化，稍后重试");
        }
        var entries = new List<ProcessEntry>();
        var unreadable = new HashSet<int>();
        foreach (var pid in pids!.Take(returned / sizeof(int)).Where(pid => pid > 0).Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (proc_pidinfo_short(pid, 13, 0, out var entry, 64) == 64 && entry.Pid == pid)
                entries.Add(entry);
            else if (Marshal.GetLastPInvokeError() != 3) unreadable.Add(pid); // ESRCH: already exited, not denied.
        }
        return (entries, unreadable);
    }

    internal static ProcessObservation? ReadProcess(int pid)
    {
        if (proc_pidinfo_all(pid, 2, 0, out var info, 232) != 232 || info.Pid != pid
            || info.StartedSeconds == 0 || info.StartedMicroseconds >= 1_000_000) return null;
        var creation = checked((long)info.StartedSeconds * 1_000_000 + (long)info.StartedMicroseconds);
        return new ProcessObservation(new ProcessIdentity(pid, creation), checked((int)info.ParentPid),
            checked(info.UserTime + info.SystemTime), info.ResidentBytes);
    }

    internal static string? ReadProcessPath(int pid)
    {
        var bytes = new byte[4096];
        var length = proc_pidpath(pid, bytes, (uint)bytes.Length);
        if (length <= 0 || length >= bytes.Length) return null;
        var end = Array.IndexOf(bytes, (byte)0, 0, length);
        return Encoding.UTF8.GetString(bytes, 0, end >= 0 ? end : length);
    }

    internal static double CpuNanosecondsPerTick()
    {
        CheckMach(mach_timebase_info(out var info), "Mach 时间单位");
        if (info.Numerator == 0 || info.Denominator == 0) throw new InvalidOperationException("Mach 时间单位不可用");
        // proc_taskinfo.pti_total_* uses Mach absolute time (not nanoseconds on Apple Silicon).
        // https://github.com/apple-oss-distributions/xnu/blob/main/osfmk/kern/bsd_kern.c
        return (double)info.Numerator / info.Denominator;
    }

    internal static int LogicalCpuCount()
    {
        nuint size = sizeof(int);
        if (sysctlbyname_int("hw.logicalcpu", out var value, ref size, IntPtr.Zero, 0) != 0
            || size != sizeof(int) || value <= 0) throw LastError("逻辑 CPU 数量");
        return value;
    }

    private static ulong ReadUInt64Sysctl(string name)
    {
        nuint size = sizeof(ulong);
        if (sysctlbyname_ulong(name, out var value, ref size, IntPtr.Zero, 0) != 0 || size != sizeof(ulong))
            throw LastError(name);
        return value;
    }

    private static Win32Exception LastError(string operation) => new(Marshal.GetLastPInvokeError(), operation + " 读取失败");
    private static void CheckMach(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"{operation} 读取失败（Mach {result}）");
    }

    [DllImport(SystemLibrary)] private static extern uint geteuid();
    [DllImport(SystemLibrary)] private static extern uint mach_host_self();
    [DllImport(SystemLibrary)] private static extern int mach_port_deallocate(uint task, uint name);
    [DllImport(SystemLibrary)] private static extern int host_statistics(uint host, int flavor, out CpuTicks info, ref uint count);
    [DllImport(SystemLibrary)] private static extern int host_statistics64(uint host, int flavor, out VmStatistics64 info, ref uint count);
    [DllImport(SystemLibrary)] private static extern int host_page_size(uint host, out nuint pageSize);
    [DllImport(SystemLibrary)] private static extern int mach_timebase_info(out TimebaseInfo info);
    [DllImport(SystemLibrary, EntryPoint = "sysctlbyname", SetLastError = true)]
    private static extern int sysctlbyname_ulong([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out ulong oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);
    [DllImport(SystemLibrary, EntryPoint = "sysctlbyname", SetLastError = true)]
    private static extern int sysctlbyname_int([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out int oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);
    [DllImport(ProcLibrary, SetLastError = true)]
    private static extern int proc_listpids(uint type, uint typeInfo, [Out] int[]? buffer, int bufferSize);
    [DllImport(ProcLibrary, EntryPoint = "proc_pidinfo", SetLastError = true)]
    private static extern int proc_pidinfo_short(int pid, int flavor, ulong arg, out ProcessEntry info, int size);
    [DllImport(ProcLibrary, EntryPoint = "proc_pidinfo", SetLastError = true)]
    private static extern int proc_pidinfo_all(int pid, int flavor, ulong arg, out ProcessTaskAllInfo info, int size);
    [DllImport(ProcLibrary, SetLastError = true)]
    private static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint bufferSize);
}
