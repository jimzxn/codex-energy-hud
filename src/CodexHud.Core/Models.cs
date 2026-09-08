namespace CodexHud.Core;

public enum SampleHealth { Loading, Fresh, Partial, Stale, Unavailable }
public enum HardwareScope { System, Codex, Compare }
public sealed record MetricSample(double? Value, DateTimeOffset? ObservedAt, SampleHealth Health = SampleHealth.Fresh, string? Detail = null)
{
    public static MetricSample Missing(string? detail = null) => new(null, null, SampleHealth.Unavailable, detail);
}
public sealed record QuotaWindow(string Key, string LimitId, string Label, string Slot, int? WindowMinutes, double? RemainingPercent, DateTimeOffset? ResetsAt);
public sealed record ResetCreditSample(int? AvailableCount, DateTimeOffset? ObservedAt, SampleHealth Health)
{
    public static ResetCreditSample Missing { get; } = new(null, null, SampleHealth.Unavailable);
}
public sealed record QuotaSnapshot(DateTimeOffset ObservedAt, IReadOnlyList<QuotaWindow> Windows, SampleHealth Health, string? Message = null)
{
    public string? AccountKey { get; init; }
    public ResetCreditSample ResetCredits { get; init; } = ResetCreditSample.Missing;
}
public sealed record ProcessIdentity(int Pid, long CreationTime);
public sealed record HardwareSnapshot(DateTimeOffset ObservedAt, MetricSample SystemCpu, MetricSample CodexCpu,
    MetricSample SystemGpu, MetricSample CodexGpu, MetricSample SystemMemoryUsed, MetricSample SystemMemoryTotal,
    MetricSample CodexMemory, MetricSample GpuMemoryUsed, MetricSample GpuMemoryTotal,
    string GpuName, bool CodexPresent, int CodexProcessCount, SampleHealth Health, string? Message = null)
{
    public MetricSample SystemDiskReadBytesPerSecond { get; init; } = MetricSample.Missing("等待磁盘采样");
    public MetricSample SystemDiskWriteBytesPerSecond { get; init; } = MetricSample.Missing("等待磁盘采样");
}
public enum ActivityState { ExecutionEvidence, Unconfirmed, Completed, Interrupted, AwaitingApproval, AwaitingInput }
public sealed record TaskActivity(string Id, string Title, ActivityState State, DateTimeOffset? EvidenceAt, DateTimeOffset? StartedAt, string Source,
    DateTimeOffset? EndedAt = null)
{
    public TaskTokenUsage TokenUsage { get; init; } = TaskTokenUsage.Missing;
    public string? TurnId { get; init; }
    public string? AttentionId { get; init; }
}
public sealed record ActivitySnapshot(DateTimeOffset ObservedAt, bool AppPresent, IReadOnlyList<TaskActivity> Tasks,
    SampleHealth Health, string? Message = null)
{
    public SampleHealth LiveHealth { get; init; } = SampleHealth.Unavailable;
    public int LiveTaskCount { get; init; }
}

public interface IQuotaProvider : IAsyncDisposable
{
    int? OwnedProcessId { get; }
    Task<QuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}
public interface IHardwareProvider : IDisposable
{
    Task<HardwareSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    void ResetBaseline();
}
public interface IActivityProvider : IDisposable
{
    Task<ActivitySnapshot> ReadAsync(bool appPresent, CancellationToken cancellationToken = default);
}
