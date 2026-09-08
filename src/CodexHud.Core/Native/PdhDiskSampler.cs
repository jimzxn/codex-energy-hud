using static CodexHud.Core.Native.WindowsHardwareNative;

namespace CodexHud.Core.Native;

internal sealed record DiskRateReading(double? BytesPerSecond, string? Error = null);
internal sealed record PdhDiskRead(DiskRateReading Read, DiskRateReading Write, bool WarmingUp = false);

internal interface IDiskSampler : IDisposable
{
    PdhDiskRead Read();
}

/// <summary>One query interval for both physical-disk throughput counters. _Total aggregates disks only once.</summary>
internal sealed class PdhDiskSampler : IDiskSampler
{
    internal const string ReadCounterPath = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    internal const string WriteCounterPath = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
    private readonly SafePdhQuery _query;
    private readonly IntPtr _readCounter;
    private readonly IntPtr _writeCounter;
    private bool _hasBaseline;

    internal PdhDiskSampler()
    {
        var status = PdhOpenQuery(null, UIntPtr.Zero, out var pointer);
        if (status != 0) throw new InvalidOperationException($"磁盘 PDH query 0x{status:X8}");
        _query = new SafePdhQuery(pointer);
        try
        {
            status = PdhAddEnglishCounter(_query, ReadCounterPath, UIntPtr.Zero, out _readCounter);
            if (status != 0) throw new InvalidOperationException($"磁盘读取计数器 0x{status:X8}");
            status = PdhAddEnglishCounter(_query, WriteCounterPath, UIntPtr.Zero, out _writeCounter);
            if (status != 0) throw new InvalidOperationException($"磁盘写入计数器 0x{status:X8}");
        }
        catch { _query.Dispose(); throw; }
    }

    public PdhDiskRead Read()
    {
        var status = PdhCollectQueryData(_query);
        if (status != 0) throw new InvalidOperationException($"磁盘 PDH sample 0x{status:X8}");
        // Rate counters require two collections. A missing baseline must never become a false zero.
        if (!_hasBaseline)
        {
            _hasBaseline = true;
            return new PdhDiskRead(new(null), new(null), WarmingUp: true);
        }
        return new PdhDiskRead(ReadRate(_readCounter), ReadRate(_writeCounter));
    }

    private static DiskRateReading ReadRate(IntPtr counter)
    {
        var status = PdhGetFormattedCounterValue(counter, PdhDouble | PdhNoCap100, out _, out var value);
        return ValidateRate(status, value.Status, value.Value);
    }

    internal static DiskRateReading ValidateRate(uint functionStatus, uint counterStatus, double value) =>
        functionStatus == 0 && (counterStatus is PdhValidData or PdhNewData) && double.IsFinite(value) && value >= 0
            ? new DiskRateReading(value)
            : new DiskRateReading(null, $"磁盘 PDH 样本不可用 (0x{functionStatus:X8}/0x{counterStatus:X8})");

    public void Dispose() => _query.Dispose();
}
