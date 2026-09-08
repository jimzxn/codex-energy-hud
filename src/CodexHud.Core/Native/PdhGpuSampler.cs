using System.Runtime.InteropServices;
using static CodexHud.Core.Native.WindowsHardwareNative;

namespace CodexHud.Core.Native;

internal sealed record CounterReading(string Instance, double Value);
internal sealed record CounterReadResult(IReadOnlyList<CounterReading>? Values, bool Partial = false, string? Error = null);
internal sealed record PdhGpuRead(CounterReadResult Engines, CounterReadResult Memory, bool WarmingUp);

internal sealed class PdhGpuSampler : IDisposable
{
    private readonly SafePdhQuery _query;
    private readonly IntPtr _engineCounter;
    private readonly IntPtr _memoryCounter;
    private readonly string? _memoryError;
    private bool _hasBaseline;

    internal PdhGpuSampler()
    {
        var status = PdhOpenQuery(null, UIntPtr.Zero, out var pointer);
        if (status != 0) throw new InvalidOperationException($"PDH query 0x{status:X8}");
        _query = new SafePdhQuery(pointer);
        try
        {
            status = PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out _engineCounter);
            if (status != 0) throw new InvalidOperationException($"GPU Engine counter 0x{status:X8}");
            status = PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", UIntPtr.Zero, out _memoryCounter);
            if (status != 0)
            {
                _memoryCounter = IntPtr.Zero;
                _memoryError = $"GPU memory counter 0x{status:X8}";
            }
        }
        catch { _query.Dispose(); throw; }
    }

    internal PdhGpuRead Read()
    {
        var status = PdhCollectQueryData(_query);
        if (status != 0) throw new InvalidOperationException($"PDH sample 0x{status:X8}");
        var warming = !_hasBaseline;
        _hasBaseline = true;
        var engines = warming ? new CounterReadResult(null, Error: "等待第二次 GPU 采样") : ReadArray(_engineCounter);
        var memory = _memoryCounter == IntPtr.Zero
            ? new CounterReadResult(null, Error: _memoryError)
            : ReadArray(_memoryCounter);
        return new PdhGpuRead(engines, memory, warming);
    }

    private static CounterReadResult ReadArray(IntPtr counter)
    {
        for (var retry = 0; retry < 3; retry++)
        {
            uint bytes = 0;
            var status = PdhGetFormattedCounterArray(counter, PdhDouble | PdhNoCap100, ref bytes, out var count, IntPtr.Zero);
            if (status == 0 && bytes == 0) return new CounterReadResult([]);
            if (status != PdhMoreData || bytes == 0 || bytes > 32 * 1024 * 1024)
                return new CounterReadResult(null, Error: $"PDH counter 0x{status:X8}");
            var buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                status = PdhGetFormattedCounterArray(counter, PdhDouble | PdhNoCap100, ref bytes, out count, buffer);
                if (status == PdhMoreData) continue; // a process appeared between the two calls
                if (status != 0) return new CounterReadResult(null, Error: $"PDH values 0x{status:X8}");
                var itemSize = Marshal.SizeOf<PdhArrayItem>();
                if (count > bytes / itemSize) return new CounterReadResult(null, Error: "PDH 返回了无效数组长度");
                var result = new List<CounterReading>((int)count);
                var invalid = 0;
                for (var index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<PdhArrayItem>(IntPtr.Add(buffer, checked(index * itemSize)));
                    if ((item.Value.Status is PdhValidData or PdhNewData) && double.IsFinite(item.Value.Value) && item.Value.Value >= 0)
                        result.Add(new CounterReading(Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Value));
                    else invalid++;
                }
                if (result.Count == 0 && invalid > 0) return new CounterReadResult(null, Error: "GPU 样本暂不可用");
                return new CounterReadResult(result, invalid > 0);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return new CounterReadResult(null, Error: "GPU 进程列表正在变化");
    }

    public void Dispose() => _query.Dispose();
}
