using System.Runtime.InteropServices;
using System.Text;

namespace CodexHud.Core.Native;

/// <summary>IOKit block-driver byte counters, filtered to declared physical interconnects.</summary>
internal static class MacDiskSampler
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;

    internal sealed record Counters(long? ReadBytes, long? WriteBytes);
    internal sealed record Snapshot(IReadOnlyDictionary<ulong, Counters> Devices, bool Partial);

    // Counter names and classification are from Apple's public IOStorageFamily headers:
    // https://github.com/apple-oss-distributions/IOStorageFamily/blob/main/IOBlockStorageDriver.h
    // https://github.com/apple-oss-distributions/IOStorageFamily/blob/main/IOStorageProtocolCharacteristics.h
    internal static Snapshot Read(CancellationToken token)
    {
        var matching = IOServiceMatching("IOBlockStorageDriver");
        if (matching == IntPtr.Zero) throw new InvalidOperationException("IOKit 磁盘匹配器不可用");
        // IOServiceGetMatchingServices consumes matching, including on failure.
        var result = IOServiceGetMatchingServices(0, matching, out var iterator);
        if (result != 0) throw new InvalidOperationException($"IOKit 磁盘枚举失败（{result}）");
        var devices = new Dictionary<ulong, Counters>();
        var partial = false;
        try
        {
            uint entry;
            while ((entry = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var physical = IsPhysical(entry);
                    if (physical is null) { partial = true; continue; }
                    if (!physical.Value) continue;
                    if (IORegistryEntryGetRegistryEntryID(entry, out var id) != 0)
                    {
                        partial = true;
                        continue;
                    }
                    var statistics = CopyProperty(entry, "Statistics", searchParents: false);
                    try
                    {
                        if (statistics == IntPtr.Zero || CFGetTypeID(statistics) != CFDictionaryGetTypeID())
                        {
                            devices[id] = new Counters(null, null);
                            partial = true;
                            continue;
                        }
                        var read = ReadNumber(statistics, "Bytes (Read)");
                        var write = ReadNumber(statistics, "Bytes (Write)");
                        devices[id] = new Counters(read, write);
                        partial |= read is null || write is null;
                    }
                    finally { Release(statistics); }
                }
                finally { _ = IOObjectRelease(entry); }
            }
        }
        finally { _ = IOObjectRelease(iterator); }
        if (devices.Count == 0) throw new InvalidOperationException("没有可确认物理连接及读取计数的磁盘；不以虚拟磁盘替代");
        return new Snapshot(devices, partial);
    }

    private static bool? IsPhysical(uint entry)
    {
        var protocol = CopyProperty(entry, "Protocol Characteristics", searchParents: true);
        try
        {
            if (protocol == IntPtr.Zero || CFGetTypeID(protocol) != CFDictionaryGetTypeID()) return null;
            var interconnect = ReadString(protocol, "Physical Interconnect");
            var location = ReadString(protocol, "Physical Interconnect Location");
            if (interconnect == "Virtual Interface" || location is "File" or "RAM") return false;
            if (string.IsNullOrEmpty(interconnect)) return null;
            return location is "Internal" or "External" or "Internal/External" ? true : null;
        }
        finally { Release(protocol); }
    }

    private static IntPtr CopyProperty(uint entry, string name, bool searchParents)
    {
        var key = CreateString(name);
        try
        {
            return searchParents
                ? IORegistryEntrySearchCFProperty(entry, "IOService", key, IntPtr.Zero, 3) // Recursive | Parents
                : IORegistryEntryCreateCFProperty(entry, key, IntPtr.Zero, 0);
        }
        finally { Release(key); }
    }

    private static long? ReadNumber(IntPtr dictionary, string name)
    {
        var key = CreateString(name);
        try
        {
            var value = CFDictionaryGetValue(dictionary, key); // borrowed from dictionary
            return value != IntPtr.Zero && CFGetTypeID(value) == CFNumberGetTypeID()
                && CFNumberGetValue(value, 4, out var number) && number >= 0 ? number : null; // kCFNumberSInt64Type
        }
        finally { Release(key); }
    }

    private static string? ReadString(IntPtr dictionary, string name)
    {
        var key = CreateString(name);
        try
        {
            var value = CFDictionaryGetValue(dictionary, key);
            if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID()) return null;
            var buffer = new byte[256];
            if (!CFStringGetCString(value, buffer, buffer.Length, Utf8)) return null;
            var end = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, end >= 0 ? end : buffer.Length);
        }
        finally { Release(key); }
    }

    private static IntPtr CreateString(string value)
    {
        var result = CFStringCreateWithCString(IntPtr.Zero, value, Utf8);
        return result != IntPtr.Zero ? result : throw new InvalidOperationException("CoreFoundation 字符串分配失败");
    }

    private static void Release(IntPtr value)
    {
        if (value != IntPtr.Zero) CFRelease(value);
    }

    [DllImport(IOKit)] private static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(IOKit)] private static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);
    [DllImport(IOKit)] private static extern uint IOIteratorNext(uint iterator);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint value);
    [DllImport(IOKit)] private static extern int IORegistryEntryGetRegistryEntryID(uint entry, out ulong id);
    [DllImport(IOKit)] private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
    [DllImport(IOKit)] private static extern IntPtr IORegistryEntrySearchCFProperty(uint entry, [MarshalAs(UnmanagedType.LPUTF8Str)] string plane, IntPtr key, IntPtr allocator, uint options);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(IntPtr value);
    [DllImport(CoreFoundation)] private static extern nuint CFDictionaryGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFNumberGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr value, nint type, out long number);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr value, [Out] byte[] buffer, nint size, uint encoding);
}
