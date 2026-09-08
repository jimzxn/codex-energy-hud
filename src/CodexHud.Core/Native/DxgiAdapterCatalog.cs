using System.Runtime.InteropServices;

namespace CodexHud.Core.Native;

internal sealed record GpuAdapter(string Name, uint LuidLow, int LuidHigh, ulong DedicatedMemory)
{
    internal string LuidToken => $"luid_0x{unchecked((uint)LuidHigh):x8}_0x{LuidLow:x8}";
}

internal static class DxgiAdapterCatalog
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { internal uint Low; internal int High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal uint VendorId, DeviceId, SubsystemId, Revision;
        internal UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        internal Luid AdapterLuid;
        internal uint Flags;
    }

    [ComImport, Guid("770AAE78-F26F-4DBA-A829-253C83D1B387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr data);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr window, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr window);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr description, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IAdapter1? adapter);
        [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool IsCurrent();
    }

    [ComImport, Guid("29038F61-3839-4626-91FD-086879011A05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr data);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(IntPtr description);
        [PreserveSig] int CheckInterfaceSupport(ref Guid iid, out long version);
        [PreserveSig] int GetDesc1(out AdapterDescription description);
    }

    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IFactory1? factory);

    internal static GpuAdapter? FindPreferred()
    {
        var iid = typeof(IFactory1).GUID;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        if (factory is null) return null;
        try
        {
            GpuAdapter? preferred = null;
            for (uint index = 0; index < 32; index++)
            {
                var status = factory.EnumAdapters1(index, out var adapter);
                if (status == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                Marshal.ThrowExceptionForHR(status);
                if (adapter is null) continue;
                try
                {
                    Marshal.ThrowExceptionForHR(adapter.GetDesc1(out var description));
                    if ((description.Flags & 2) != 0) continue; // software adapter
                    var candidate = new GpuAdapter(description.Description.TrimEnd('\0'), description.AdapterLuid.Low,
                        description.AdapterLuid.High, description.DedicatedVideoMemory.ToUInt64());
                    if (preferred is null || candidate.DedicatedMemory > preferred.DedicatedMemory) preferred = candidate;
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
            return preferred;
        }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
