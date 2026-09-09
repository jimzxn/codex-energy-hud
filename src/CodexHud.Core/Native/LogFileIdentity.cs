using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexHud.Core.Native;

/// <summary>Handle-based identity keeps replacement files separate from append-only log updates.</summary>
internal static class LogFileIdentity
{
    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string Read(SafeFileHandle handle, out long writeTime)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot determine log identity.");
            writeTime = ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
            return $"{info.VolumeSerialNumber}:{info.FileIndexHigh}:{info.FileIndexLow}:{info.CreationTime.dwHighDateTime}:{info.CreationTime.dwLowDateTime}";
        }
        if (OperatingSystem.IsMacOS())
        {
            bool referenced = false;
            try
            {
                handle.DangerousAddRef(ref referenced);
                int descriptor = handle.DangerousGetHandle().ToInt32();
                MacStat info = default;
                // Darwin's x64 ABI preserves the old symbol; arm64 exposes only 64-bit inodes.
                int result = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => FStatMacX64(descriptor, out info),
                    Architecture.Arm64 => FStatMacArm64(descriptor, out info),
                    _ => throw new IOException("Unsupported macOS log identity architecture.")
                };
                if (result != 0) throw new IOException("Cannot determine log identity.");
                writeTime = unchecked(info.ModificationSeconds * 1_000_000_000 + info.ModificationNanoseconds);
                return $"{info.Device}:{info.Inode}:{info.BirthSeconds}:{info.BirthNanoseconds}";
            }
            finally { if (referenced) handle.DangerousRelease(); }
        }
        throw new IOException("Log file identity is not supported on this operating system.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    // Darwin 64-bit struct stat: the layout is shared by macOS arm64 and x86_64.
    // Native declarations: https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stat.h
    // Symbol aliases: https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/cdefs.h
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacStat
    {
        [FieldOffset(0)] public int Device;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(48)] public long ModificationSeconds;
        [FieldOffset(56)] public long ModificationNanoseconds;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public long BirthNanoseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FStatMacX64(int descriptor, out MacStat information);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatMacArm64(int descriptor, out MacStat information);
}
