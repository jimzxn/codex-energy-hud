using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexHud.Core;

public enum EstimateState { Calibrating, Estimated, Variable, Incomplete, Stale, Unavailable }

public sealed record QuotaCalibrationEvent(DateTimeOffset At, string Kind, string Reason,
    double? PreviousRemaining, double? Remaining, DateTimeOffset? AcceptedResetAt, DateTimeOffset? ReportedResetAt)
{
    public string? PeriodHash { get; init; }
    public long? LedgerSequence { get; init; }
}

public sealed record QuotaTokenEstimate(string QuotaKey, EstimateState State, double? RemainingTokens,
    double? LowerTokens, double? UpperTokens, int Segments, double ObservedDrop,
    DateTimeOffset? From, DateTimeOffset? Through, string Detail)
{
    public double PendingDrop { get; init; }
    public string? ProgressReason { get; init; }
    public string? LastResetReason { get; init; }
    public DateTimeOffset? LastResetAt { get; init; }
    public int RecoverySamples { get; init; }
    public int RecoveryRequired { get; init; }
    public IReadOnlyList<QuotaCalibrationEvent> Diagnostics { get; init; } = Array.Empty<QuotaCalibrationEvent>();
}

/// <summary>Pairs local token increments with quota declines; never bridges an unproved ledger gap.</summary>
public sealed class QuotaTokenEstimator
{
    private static readonly TimeSpan HistoryAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaximumAlignment = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FreshAge = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan TokenLagGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResetTolerance = TimeSpan.FromSeconds(120);
    private const int MaximumSegments = 64, MaximumWindows = 8, MaximumEvents = 64, MaximumArchives = 16;
    private const int MaximumFileBytes = 12 * 1024 * 1024, MaximumCheckpointBytes = 8 * 1024 * 1024;
    private const double MinimumDrop = 2;
    private const string Scope = "按本机近期用法估算；其他设备或云端用量可能影响比例。";
    private readonly object _gate = new();
    private readonly string? _storagePath;
    private readonly Dictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredWindow> _restored = new(StringComparer.Ordinal);
    private readonly List<ArchivedCalibration> _archives = new();
    private UsageLedgerCheckpoint? _ledgerCheckpoint;
    private string? _accountHash, _globalReason;
    private EstimateState _globalStatus = EstimateState.Calibrating;

    public QuotaTokenEstimator(string? storagePath = null) { _storagePath = storagePath; Load(); }
    public UsageLedgerCheckpoint? RestoredLedgerCheckpoint { get { lock (_gate) return _ledgerCheckpoint; } }

    public void Observe(QuotaSnapshot quota, UsageLedgerSnapshot usage)
    {
        lock (_gate)
        {
            if (quota.Health is SampleHealth.Stale or SampleHealth.Unavailable or SampleHealth.Loading)
            { PauseAll(EstimateState.Stale, "额度读数未更新，等待重新对齐。"); return; }
            if (string.IsNullOrWhiteSpace(quota.AccountKey))
            { PauseAll(EstimateState.Incomplete, "账户身份未确认，暂停配对。"); return; }
            var account = Hash(quota.AccountKey);
            if (_accountHash is not null && _accountHash != account)
            {
                foreach (var pair in _windows) Archive(pair.Key, pair.Value, quota.ObservedAt, "账户已变化");
                foreach (var pair in _restored) ArchiveStored(pair.Value, quota.ObservedAt, "账户已变化");
                _windows.Clear(); _restored.Clear(); _ledgerCheckpoint = null;
                _globalReason = "账户已变化，重新校准。";
            }
            _accountHash = account;
            var keys = quota.Windows.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var pair in _windows.Where(p => !keys.Contains(p.Key)))
                Pause(pair.Value, EstimateState.Unavailable, "额度周期已消失，请重新选择。", quota.ObservedAt);

            var checkpoint = MatchingCheckpoint(usage);
            string? invalid = usage.Health != SampleHealth.Fresh ? $"本机 Token 采样不完整（{usage.Health}），等待连续记录。"
                : !Valid(usage.Counts) ? "Token 数值字段缺失或不一致，暂停配对。"
                : string.IsNullOrWhiteSpace(usage.Epoch) ? "Token 账本标识缺失，暂停配对。"
                : usage.Checkpoint is not null && checkpoint is null ? "Token 检查点与本次快照不一致，暂停配对。"
                : AbsSeconds(quota.ObservedAt, usage.ObservedAt) > MaximumAlignment.TotalSeconds ? "额度与 Token 时间未对齐，等待同步读数。" : null;
            _globalStatus = invalid is null ? EstimateState.Calibrating : EstimateState.Incomplete;
            _globalReason = invalid;
            foreach (var window in quota.Windows.Where(Supported).Take(MaximumWindows))
            {
                if (_restored.TryGetValue(Hash(window.Key), out var candidate) && candidate.AccountHash == account
                    && candidate.ObservedAt is { } savedAt && quota.ObservedAt <= savedAt)
                { _globalStatus = EstimateState.Stale; _globalReason = "额度记录尚未超过已保存的采样时间，等待新读数。"; continue; }
                if (!_windows.TryGetValue(window.Key, out var state))
                {
                    if (_windows.Count >= MaximumWindows)
                    {
                        var oldest = _windows.MinBy(p => p.Value.ObservedAt);
                        Archive(oldest.Key, oldest.Value, quota.ObservedAt, "额度池超出保留上限"); _windows.Remove(oldest.Key);
                    }
                    state = Restore(window, account, quota.ObservedAt);
                    _windows[window.Key] = state;
                }
                var watermark = state.QuotaObservedAt ?? state.ObservedAt;
                if (watermark is { } previous && quota.ObservedAt <= previous)
                {
                    if (quota.ObservedAt < previous && state.Status is not EstimateState.Incomplete)
                        Pause(state, EstimateState.Stale, "额度记录时间回退，等待新的采样。", quota.ObservedAt);
                    continue;
                }
                state.QuotaObservedAt = quota.ObservedAt;
                // Quota boundaries are checked even when the token ledger is unavailable.
                if (!CheckPeriod(window.Key, state, window, quota.ObservedAt)) continue;
                if (invalid is not null) { Pause(state, EstimateState.Incomplete, invalid, quota.ObservedAt); continue; }
                if (checkpoint is not null && state.CheckpointGeneration == checkpoint.Generation
                    && checkpoint.Sequence <= state.CheckpointSequence)
                { Pause(state, EstimateState.Incomplete, "Token 检查点水位未前进，等待新的同步读数。", quota.ObservedAt); continue; }
                ObserveWindow(state, window, quota.ObservedAt, usage, checkpoint);
            }
            // Never read a checkpoint from a later background observation.
            if (checkpoint is not null && invalid is null && (_ledgerCheckpoint is null
                || checkpoint.Generation != _ledgerCheckpoint.Generation || checkpoint.Sequence > _ledgerCheckpoint.Sequence))
                _ledgerCheckpoint = checkpoint;
            Save();
        }
    }

    public QuotaTokenEstimate Get(QuotaWindow? window, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (window is null) return Empty("", EstimateState.Unavailable, "请选择可用额度周期。");
            if (!Supported(window)) return Empty(window.Key, EstimateState.Unavailable, "此额度池暂无对应的本机 Token 统计。");
            if (!_windows.TryGetValue(window.Key, out var state))
                return SavedHistory(window.Key, now) ?? Empty(window.Key, _globalStatus, _globalReason ?? "等待同步采样。");
            Prune(state, now);
            if (state.ObservedAt is null || state.Status is EstimateState.Stale or EstimateState.Incomplete or EstimateState.Unavailable)
                return Result(window.Key, state, state.Status, state.Reason);
            if (now - state.ObservedAt.Value > FreshAge || state.ObservedAt.Value - now > MaximumAlignment)
                return Result(window.Key, state, EstimateState.Stale, "采样已过期，等待新的同步读数。");
            if (state.Candidate is not null || state.IdentityHash != IdentityHash(window)
                || state.AcceptedResetAt <= now || window.ResetsAt is null || window.ResetsAt <= now || !AcceptedMetadata(state, window.ResetsAt.Value))
                return Result(window.Key, state, EstimateState.Calibrating, state.Candidate is null ? "周期边界待确认，暂停配对。" : state.Reason);
            if (!Percent(window.RemainingPercent) || window.RemainingPercent != state.Remaining)
                return Result(window.Key, state, EstimateState.Incomplete, "等待当前额度与 Token 配对。");
            if (state.NeedsConfirmation || state.Segments.Count < 3 || TotalDrop(state.Segments) < 6)
                return Result(window.Key, state, EstimateState.Calibrating, state.Reason);
            var drop = TotalDrop(state.Segments);
            var rate = state.Segments.Sum(s => s.Tokens) / drop;
            var rates = state.Segments.Select(s => s.Tokens / s.Drop).ToArray();
            var low = rates.Min(); var high = rates.Max();
            var variance = state.Segments.Sum(s => s.Drop * Math.Pow(s.Tokens / s.Drop - rate, 2)) / drop;
            var variable = state.UsageVaried || high / low > 2 || Math.Sqrt(variance) / rate > .35;
            var detail = Scope + (variable ? "分段比例波动较大。" : "") + "范围取近期有效分段的最小值与最大值，并非置信区间。"
                + (state.ModelHash is null ? "模型/速度信息未齐全。" : "");
            return Decorate(new(window.Key, variable ? EstimateState.Variable : EstimateState.Estimated,
                rate * state.Remaining!.Value, low * state.Remaining.Value, high * state.Remaining.Value,
                state.Segments.Count, drop, state.Segments[0].From, state.ObservedAt, detail), state, state.Reason);
        }
    }

    /// <summary>Displays retained samples without treating their account, period, or ledger as confirmed.</summary>
    public QuotaTokenEstimate GetSavedHistory(string? quotaKey, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(quotaKey)) return Empty("", EstimateState.Unavailable, "请选择可用额度周期。");
            return SavedHistory(quotaKey, now) ?? Empty(quotaKey, _globalStatus, _globalReason ?? "等待同步采样。");
        }
    }

    private QuotaTokenEstimate? SavedHistory(string key, DateTimeOffset now)
    {
        StoredWindow stored;
        bool active = _windows.TryGetValue(key, out var state);
        if (active)
        {
            if (state is null || state.AccountHash is null || state.WindowHash is null
                || _accountHash is not null && state.AccountHash != _accountHash) return null;
            stored = Store(key, state, _ledgerCheckpoint);
        }
        else
        {
            if (!_restored.TryGetValue(Hash(key), out var restored)
                || _accountHash is not null && restored.AccountHash != _accountHash) return null;
            stored = restored;
        }
        // Project a snapshot only: missing live quota must not consume a stored anchor, advance
        // recovery, or prune/overwrite either source. Apply the same age bounds as active history.
        var segments = stored.Segments.Where(s => now - s.From <= HistoryAge
            && s.Through - now <= MaximumAlignment).ToArray();
        string reason = active ? "已保留历史，等待可用额度与新采样确认。"
            : "已保存历史，等待账户、额度周期与新采样确认。";
        string detail = _globalReason is null ? reason : reason + " " + _globalReason;
        return new(key, _globalStatus == EstimateState.Stale ? EstimateState.Stale : EstimateState.Incomplete,
            null, null, null, segments.Length, TotalDrop(segments),
            segments.Length > 0 ? segments[0].From : null, stored.ObservedAt, detail + " " + Scope)
        {
            PendingDrop = segments.Length > 0 ? stored.PendingDrop : 0, ProgressReason = detail,
            LastResetReason = stored.LastResetReason, LastResetAt = stored.LastResetAt,
            RecoveryRequired = segments.Length > 0 ? 2 : 0,
            Diagnostics = (stored.Diagnostics ?? []).ToArray()
        };
    }

    public void Invalidate(string reason)
    { lock (_gate) PauseAll(EstimateState.Incomplete, string.IsNullOrWhiteSpace(reason) ? "采样中断，等待重新对齐。" : reason); }

    private bool CheckPeriod(string key, WindowState state, QuotaWindow window, DateTimeOffset time)
    {
        if (!Percent(window.RemainingPercent) || window.WindowMinutes is null or <= 0 || window.ResetsAt is null)
        { Pause(state, EstimateState.Incomplete, "额度周期、重置时间或百分比缺失，等待完整读数。", time); return false; }
        var reported = window.ResetsAt.Value;
        var identity = IdentityHash(window);
        if (state.IdentityHash is null)
        {
            state.IdentityHash = identity; state.AcceptedResetAt = reported; state.MetadataResetAt = reported;
            state.WindowHash = WindowHash(window); state.CycleId = Guid.NewGuid().ToString("N");
            Event(state, time, "baseline", "首次完整周期读数", window.RemainingPercent, reported);
        }
        else if (state.IdentityHash != identity) NewCycle(key, state, window, time, "额度池或周期长度已变化");
        var rebound = state.Remaining is { } remaining && window.RemainingPercent > remaining;
        var expired = state.AcceptedResetAt <= time;
        string? candidateKind = rebound ? "refill" : expired || reported <= time ? "deadline"
            : !AcceptedMetadata(state, reported) ? "metadata" : null;
        if (candidateKind is null)
        {
            if (state.Candidate is { } abandoned)
            {
                Event(state, time, "review-cancelled", "复核后读数回到已接受周期", window.RemainingPercent, reported);
                state.ContinuityBroken |= abandoned.Kind != "metadata";
                state.Candidate = null;
            }
            if (reported != state.MetadataResetAt)
                Event(state, time, "timestamp-drift", "重置时间轻微漂移，保留固定周期边界", window.RemainingPercent, reported);
            return true;
        }
        var pending = state.Candidate;
        bool same = pending is not null && pending.Kind == candidateKind && pending.ReportedResetAt == reported;
        if (!same) pending = new(candidateKind, reported, time, 1);
        else if (time > pending!.LastObservedAt) pending = pending with { LastObservedAt = time, Samples = pending.Samples + 1 };
        state.Candidate = pending;
        state.ContinuityBroken |= candidateKind != "metadata";
        Pause(state, EstimateState.Calibrating, candidateKind switch
        {
            "refill" => "额度回升待复核，已保留历史样本。",
            "metadata" => "重置时间变化待复核，已保留历史样本。",
            _ => "周期边界待复核，已保留历史样本。"
        }, time);
        Event(state, time, "boundary-review", state.Reason, window.RemainingPercent, reported);
        if (pending!.Samples < 2 || reported <= time) return false;
        if (candidateKind == "metadata")
        {
            // Metadata corrections never move the original guard later.
            state.AcceptedResetAt = state.AcceptedResetAt < reported ? state.AcceptedResetAt : reported;
            state.MetadataResetAt = reported; state.Candidate = null;
            Event(state, time, "metadata-confirmed", "连续读数确认元数据修正，保留有效样本", window.RemainingPercent, reported);
            return true;
        }
        if (candidateKind == "deadline" && state.MetadataResetAt != state.AcceptedResetAt && reported == state.MetadataResetAt)
            return false; // Corrected metadata alone cannot prove a new cycle.
        NewCycle(key, state, window, time, candidateKind == "refill" ? "连续读数确认额度回升" : "连续读数确认新额度周期");
        return true;
    }

    private static bool AcceptedMetadata(WindowState state, DateTimeOffset reset) => state.AcceptedResetAt is { } accepted
        && (AbsSeconds(reset, accepted) <= ResetTolerance.TotalSeconds || reset == state.MetadataResetAt);

    private void ObserveWindow(WindowState state, QuotaWindow window, DateTimeOffset time,
        UsageLedgerSnapshot usage, UsageLedgerCheckpoint? checkpoint)
    {
        var model = string.IsNullOrWhiteSpace(usage.ModelMix) ? null : Hash(usage.ModelMix);
        Prune(state, time); TrackModel(state, model);
        bool sameEpoch = state.EpochHash == Hash(usage.Epoch);
        bool sameCheckpoint = checkpoint is not null && state.CheckpointGeneration == checkpoint.Generation
            && checkpoint.Sequence > state.CheckpointSequence && sameEpoch;
        bool countersContinue = state.LastCounts is not null && !Regressed(state.LastCounts, usage.Counts);
        bool shortLiveGap = !state.RestoredAnchor && time - state.ObservedAt <= MaximumGap;
        bool generationCompatible = checkpoint is null || state.CheckpointGeneration is null || state.CheckpointGeneration == checkpoint.Generation;
        bool canContinue = !state.ContinuityBroken && generationCompatible && sameEpoch && countersContinue && (shortLiveGap || sameCheckpoint);
        if (state.Anchor is not null && !canContinue)
        {
            var reason = !sameEpoch ? "Token 账本已重建，隔离断档前未完成段。"
                : !countersContinue ? "Token 计数回退，隔离断档前未完成段。"
                : state.ContinuityBroken ? "周期复核期间连续性无法证明，隔离未完成段。"
                : "缺少连续检查点，隔离断档前未完成段。";
            Isolate(state, time, reason, state.ContinuityBroken ? null : window.RemainingPercent, window.ResetsAt); RequireRecovery(state);
        }
        if (state.Anchor is null)
        {
            Baseline(state, window, time, usage, checkpoint);
            RegisterHealthy(state, time);
            return;
        }
        state.Paused = false; state.RestoredAnchor = false;
        state.ObservedAt = time; state.Remaining = window.RemainingPercent; state.LastCounts = usage.Counts;
        SetCheckpoint(state, checkpoint);
        var anchor = state.Anchor;
        var drop = anchor.Remaining - window.RemainingPercent!.Value;
        var delta = Difference(anchor.Counts, usage.Counts);
        state.PendingDrop = Math.Clamp(drop, 0, 100);
        if (delta.Cached > delta.Input || delta.Output > delta.Tokens || delta.Input > delta.Tokens)
        { Pause(state, EstimateState.Incomplete, "Token 明细增量不一致，等待重新对齐。", time); state.ContinuityBroken = true; return; }
        if (drop > 0 && delta.Tokens == 0)
        {
            state.UnmatchedAt ??= time;
            if (state.NeedsConfirmation) RequireRecovery(state);
            state.Status = EstimateState.Incomplete;
            state.Reason = "额度变化尚未匹配到本机 Token，等待记录更新。";
            if (time - state.UnmatchedAt > TokenLagGrace)
            {
                Isolate(state, time, "额度下降未匹配到本机 Token，隔离未完成段。");
                RequireRecovery(state); Baseline(state, window, time, usage, checkpoint);
            }
            return;
        }
        state.UnmatchedAt = null;
        state.Status = EstimateState.Calibrating;
        if (time - anchor.Time > TimeSpan.FromHours(2))
        {
            Isolate(state, time, "本段跨度超过两小时，隔离未完成段。");
            RequireRecovery(state); Baseline(state, window, time, usage, checkpoint); RegisterHealthy(state, time); return;
        }
        RegisterHealthy(state, time);
        if (drop < MinimumDrop || delta.Tokens <= 0) return;
        var segment = new Segment(anchor.Time, time, drop, delta.Tokens, delta.Input, delta.Cached, delta.Output);
        if (state.Segments.Count > 0 && StructureChanged(state.Segments, segment)) state.UsageVaried = true;
        state.Segments.Add(segment); state.PendingDrop = 0;
        state.Reason = state.NeedsConfirmation ? RecoveryReason(state) : "有效分段已保存，继续采样。";
        Event(state, time, "segment", "有效分段已保存", window.RemainingPercent, window.ResetsAt);
        Prune(state, time);
        state.Anchor = new(time, window.RemainingPercent.Value, usage.Counts);
    }

    private static void Baseline(WindowState state, QuotaWindow window, DateTimeOffset time,
        UsageLedgerSnapshot usage, UsageLedgerCheckpoint? checkpoint)
    {
        state.Anchor = new(time, window.RemainingPercent!.Value, usage.Counts);
        state.LastCounts = usage.Counts; state.ObservedAt = time; state.Remaining = window.RemainingPercent;
        state.EpochHash = Hash(usage.Epoch); state.PendingDrop = 0; state.Status = EstimateState.Calibrating;
        state.UnmatchedAt = null; state.Paused = false; state.RestoredAnchor = false; state.ContinuityBroken = false;
        state.Reason = state.NeedsConfirmation ? RecoveryReason(state) : "正在累计有效变化。";
        SetCheckpoint(state, checkpoint);
    }
    private static void SetCheckpoint(WindowState state, UsageLedgerCheckpoint? checkpoint)
    { state.CheckpointGeneration = checkpoint?.Generation; state.CheckpointSequence = checkpoint?.Sequence ?? 0; }
    private static UsageLedgerCheckpoint? MatchingCheckpoint(UsageLedgerSnapshot usage)
    {
        var cp = usage.Checkpoint;
        return cp is not null && cp.Epoch == usage.Epoch && cp.Counts == usage.Counts && cp.ObservedAt == usage.ObservedAt
            && !string.IsNullOrWhiteSpace(cp.Generation) && cp.Sequence >= 0 && cp.HasValidChecksum() ? cp : null;
    }
    private static void TrackModel(WindowState state, string? model)
    { if (model is null) return; if (state.ModelHash is not null && state.ModelHash != model) state.UsageVaried = true; state.ModelHash = model; }
    private static void RequireRecovery(WindowState state)
    { state.NeedsConfirmation = state.Segments.Count > 0; state.RecoverySamples = 0; state.RecoveryObservedAt = null; }
    private static string RecoveryReason(WindowState state) => $"恢复确认 {state.RecoverySamples}/2，等待新的健康同步读数。";
    private static void RegisterHealthy(WindowState state, DateTimeOffset time)
    {
        if (state.NeedsConfirmation && (state.RecoveryObservedAt is null || time > state.RecoveryObservedAt))
        {
            if (state.RecoveryObservedAt is { } previous && time - previous > MaximumGap) state.RecoverySamples = 0;
            state.RecoveryObservedAt = time; state.RecoverySamples++;
            if (state.RecoverySamples >= 2)
            {
                state.NeedsConfirmation = false;
                Event(state, time, "recovered", "两次健康同步读数已确认历史样本", state.Remaining, state.MetadataResetAt);
            }
        }
        state.Reason = state.NeedsConfirmation ? RecoveryReason(state) : "正在累计有效变化。";
    }

    private WindowState Restore(QuotaWindow window, string account, DateTimeOffset time)
    {
        var state = new WindowState { AccountHash = account };
        if (!_restored.Remove(Hash(window.Key), out var stored)) return state;
        if (stored.AccountHash != account) { ArchiveStored(stored, time, "账户不匹配"); return state; }
        bool legacy = stored.IdentityHash is null || stored.AcceptedResetAt is null;
        if (legacy && stored.WindowHash != WindowHash(window))
        { ArchiveStored(stored, time, "旧版周期身份无法验证"); Event(state, time, "legacy-isolated", "旧版历史已归档，周期身份无法验证", window.RemainingPercent, window.ResetsAt); return state; }
        state.IdentityHash = stored.IdentityHash ?? IdentityHash(window);
        state.AcceptedResetAt = stored.AcceptedResetAt ?? window.ResetsAt;
        state.MetadataResetAt = stored.MetadataResetAt ?? state.AcceptedResetAt;
        state.WindowHash = stored.WindowHash; state.CycleId = stored.CycleId ?? Guid.NewGuid().ToString("N");
        state.Segments.AddRange(stored.Segments);
        state.ObservedAt = stored.ObservedAt; state.QuotaObservedAt = stored.QuotaObservedAt ?? stored.ObservedAt;
        state.Remaining = stored.Remaining; state.ModelHash = stored.ModelHash; state.UsageVaried = stored.UsageVaried;
        state.LastResetAt = stored.LastResetAt; state.LastResetReason = stored.LastResetReason;
        state.Diagnostics.AddRange((stored.Diagnostics ?? []).TakeLast(MaximumEvents));
        state.Isolated.AddRange((stored.Isolated ?? []).TakeLast(MaximumEvents));
        state.Candidate = stored.Candidate;
        state.Status = EstimateState.Calibrating; RequireRecovery(state);
        var cp = _ledgerCheckpoint;
        if (stored.Anchor is { } anchor && ValidAnchor(anchor) && stored.LastCounts is { } last && Valid(last)
            && cp is not null && stored.CheckpointGeneration == cp.Generation && stored.CheckpointSequence <= cp.Sequence
            && stored.EpochHash == Hash(cp.Epoch) && !Regressed(last, cp.Counts)
            && stored.ObservedAt <= cp.ObservedAt && !Regressed(anchor.Counts, last)
            && anchor.Time <= stored.ObservedAt && anchor.Remaining >= stored.Remaining)
        {
            state.Anchor = anchor; state.LastCounts = last; state.EpochHash = stored.EpochHash;
            state.CheckpointGeneration = stored.CheckpointGeneration; state.CheckpointSequence = stored.CheckpointSequence;
            state.PendingDrop = stored.PendingDrop; state.RestoredAnchor = true; state.ContinuityBroken = stored.ContinuityBroken;
            Event(state, time, "anchor-restored", "未完成段检查点已载入，等待账本验证续接", window.RemainingPercent, window.ResetsAt);
        }
        else
        {
            if (stored.PendingDrop > 0)
            {
                const string reason = "恢复时缺少有效配对检查点，未完成段已隔离。";
                state.Isolated.Add(new(stored.ObservedAt ?? time, stored.PendingDrop, reason)
                { PreviousPendingDrop = stored.PendingDrop });
                Event(state, time, "fragment-isolated", reason, window.RemainingPercent, window.ResetsAt);
            }
            Event(state, time, "history-restored", legacy ? "旧版闭合段已保留；旧版未保存可恢复锚点" : "闭合段已保留，等待健康同步读数", window.RemainingPercent, window.ResetsAt);
        }
        state.Reason = state.NeedsConfirmation ? RecoveryReason(state) : "等待同步采样。";
        Prune(state, time); return state;
    }

    private void PauseAll(EstimateState status, string reason)
    { _globalReason = Short(reason); _globalStatus = status; foreach (var state in _windows.Values) Pause(state, status, reason); Save(); }
    private static void Pause(WindowState state, EstimateState status, string reason, DateTimeOffset? time = null)
    {
        reason = Short(reason);
        if (!state.Paused || state.LastResetReason != reason)
        {
            state.LastResetAt = time ?? DateTimeOffset.UtcNow; state.LastResetReason = reason;
            Event(state, state.LastResetAt.Value, "paused", reason, state.Remaining, state.MetadataResetAt);
        }
        state.Paused = true; state.UnmatchedAt = null; RequireRecovery(state);
        state.Status = status; state.Reason = reason;
    }
    private static void Isolate(WindowState state, DateTimeOffset time, string reason,
        double? observedRemaining = null, DateTimeOffset? reportedReset = null)
    {
        if (state.Anchor is { } anchor)
        {
            // This is a quantified unpaired decline, never an eligible token/quota sample.
            // No cross-period or refill arithmetic is permitted.
            bool comparable = Percent(observedRemaining) && state.AcceptedResetAt > time && observedRemaining <= anchor.Remaining;
            var drop = comparable ? Math.Max(state.PendingDrop, anchor.Remaining - observedRemaining!.Value) : state.PendingDrop;
            state.Isolated.Add(new(time, drop, Short(reason))
            { PreviousPendingDrop = state.PendingDrop, AdditionalUnpairedDrop = comparable ? Math.Max(0, drop - state.PendingDrop) : null });
            if (state.Isolated.Count > MaximumEvents) state.Isolated.RemoveAt(0);
            Event(state, time, "fragment-isolated", reason, observedRemaining ?? state.Remaining, reportedReset ?? state.MetadataResetAt);
        }
        state.Anchor = null; state.LastCounts = null; state.PendingDrop = 0; state.UnmatchedAt = null;
    }
    private void NewCycle(string key, WindowState state, QuotaWindow window, DateTimeOffset time, string reason)
    {
        Archive(key, state, time, reason); Isolate(state, time, reason);
        state.Segments.Clear(); state.UsageVaried = false; state.ModelHash = null;
        state.Remaining = null; state.ObservedAt = null; state.Candidate = null;
        state.IdentityHash = IdentityHash(window); state.AcceptedResetAt = window.ResetsAt; state.MetadataResetAt = window.ResetsAt;
        state.WindowHash = WindowHash(window); state.CycleId = Guid.NewGuid().ToString("N");
        state.NeedsConfirmation = false; state.RecoverySamples = 0; state.RecoveryObservedAt = null;
        state.ContinuityBroken = false; state.Paused = false; state.Status = EstimateState.Calibrating;
        state.LastResetReason = Short(reason); state.LastResetAt = time; state.Reason = reason;
        Event(state, time, "cycle-archived", reason, window.RemainingPercent, window.ResetsAt);
    }
    private void Archive(string key, WindowState state, DateTimeOffset time, string reason)
    {
        if (state.Segments.Count == 0) return;
        _archives.Add(new(Hash(key), state.AccountHash!, state.WindowHash, time, Short(reason), state.Segments.ToList()));
        if (_archives.Count > MaximumArchives) _archives.RemoveAt(0);
    }
    private void ArchiveStored(StoredWindow stored, DateTimeOffset time, string reason)
    {
        if (stored.Segments.Count == 0) return;
        _archives.Add(new(stored.KeyHash, stored.AccountHash, stored.WindowHash, time, Short(reason), stored.Segments));
        if (_archives.Count > MaximumArchives) _archives.RemoveAt(0);
    }
    private static void Event(WindowState state, DateTimeOffset at, string kind, string reason, double? remaining, DateTimeOffset? reported)
    {
        reason = Short(reason);
        if (state.Diagnostics.LastOrDefault() is { } last && last.Kind == kind && last.Reason == reason
            && last.Remaining == remaining && last.ReportedResetAt == reported) return;
        state.Diagnostics.Add(new(at, kind, reason, state.Remaining, remaining, state.AcceptedResetAt, reported)
        { PeriodHash = state.IdentityHash, LedgerSequence = state.CheckpointGeneration is null ? null : state.CheckpointSequence });
        if (state.Diagnostics.Count > MaximumEvents) state.Diagnostics.RemoveAt(0);
    }
    private static string Short(string reason) => reason.Length > 256 ? reason[..256] : reason;

    private static bool Supported(QuotaWindow window) => string.Equals(window.LimitId, "codex", StringComparison.OrdinalIgnoreCase);
    private static bool Percent(double? value) => value is { } number && double.IsFinite(number) && number is >= 0 and <= 100;
    private static bool Valid(TokenCounts? counts) => counts is not null && counts.TotalTokens is >= 0 && counts.InputTokens is >= 0
        && counts.CachedInputTokens is >= 0 && counts.OutputTokens is >= 0
        && counts.CachedInputTokens <= counts.InputTokens
        && (counts.CacheWriteInputTokens is null || counts.CacheWriteInputTokens >= 0 && counts.CacheWriteInputTokens <= counts.InputTokens)
        && (counts.ReasoningOutputTokens is null || counts.ReasoningOutputTokens >= 0 && counts.ReasoningOutputTokens <= counts.OutputTokens)
        && (decimal)counts.InputTokens.Value + counts.OutputTokens.Value == counts.TotalTokens.Value;
    private static bool Regressed(TokenCounts before, TokenCounts after) => after.TotalTokens < before.TotalTokens
        || after.InputTokens < before.InputTokens || after.CachedInputTokens < before.CachedInputTokens
        || after.OutputTokens < before.OutputTokens
        || before.CacheWriteInputTokens is { } write && after.CacheWriteInputTokens < write
        || before.ReasoningOutputTokens is { } reasoning && after.ReasoningOutputTokens < reasoning;
    private static (double Tokens, double Input, double Cached, double Output) Difference(TokenCounts before, TokenCounts after) =>
        ((double)((decimal)after.TotalTokens!.Value - before.TotalTokens!.Value),
            (double)((decimal)after.InputTokens!.Value - before.InputTokens!.Value),
            (double)((decimal)after.CachedInputTokens!.Value - before.CachedInputTokens!.Value),
            (double)((decimal)after.OutputTokens!.Value - before.OutputTokens!.Value));
    private static bool StructureChanged(List<Segment> previous, Segment next)
    {
        var input = previous.Sum(s => s.Input);
        var cached = previous.Sum(s => s.Cached);
        var outputShare = previous.Sum(s => s.Output) / previous.Sum(s => s.Tokens);
        return Math.Abs(next.Output / next.Tokens - outputShare) > .20
            || input > 0 && next.Input > 0 && Math.Abs(next.Cached / next.Input - cached / input) > .25;
    }
    private static double AbsSeconds(DateTimeOffset a, DateTimeOffset b) => Math.Abs((a - b).TotalSeconds);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string IdentityHash(QuotaWindow window) => Hash($"{window.LimitId.ToLowerInvariant()}|{window.Slot}|{window.WindowMinutes}");
    private static string WindowHash(QuotaWindow window) => Hash($"{window.LimitId.ToLowerInvariant()}|{window.Slot}|{window.WindowMinutes}|{window.ResetsAt?.UtcTicks}");
    private static void Prune(WindowState state, DateTimeOffset time)
    {
        state.Segments.RemoveAll(s => time - s.From > HistoryAge || s.Through - time > MaximumAlignment);
        if (state.Segments.Count > MaximumSegments) state.Segments.RemoveRange(0, state.Segments.Count - MaximumSegments);
        if (state.Segments.Count == 0) { state.NeedsConfirmation = false; state.RecoverySamples = 0; state.RecoveryObservedAt = null; }
    }
    private static QuotaTokenEstimate Empty(string key, EstimateState status, string detail) =>
        new(key, status, null, null, null, 0, 0, null, null, detail + " " + Scope) { ProgressReason = detail };
    private static QuotaTokenEstimate Result(string key, WindowState state, EstimateState status, string detail) => Decorate(
        new(key, status, null, null, null, state.Segments.Count, TotalDrop(state.Segments),
            state.Segments.Count > 0 ? state.Segments[0].From : state.Anchor?.Time, state.ObservedAt, detail + " " + Scope), state, detail);
    private static QuotaTokenEstimate Decorate(QuotaTokenEstimate result, WindowState state, string detail) => result with
    {
        PendingDrop = state.PendingDrop, ProgressReason = detail, LastResetReason = state.LastResetReason, LastResetAt = state.LastResetAt,
        RecoverySamples = state.NeedsConfirmation ? Math.Min(state.RecoverySamples, 2) : 0,
        RecoveryRequired = state.NeedsConfirmation ? 2 : 0, Diagnostics = state.Diagnostics.ToArray()
    };
    private static double TotalDrop(IEnumerable<Segment> segments) => Math.Clamp(segments.Sum(s => s.Drop), 0, 100);

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_storagePath)) return;
        try
        {
            var info = new FileInfo(_storagePath);
            if (!info.Exists || info.Length > MaximumFileBytes) return;
            using var document = JsonDocument.Parse(File.ReadAllText(_storagePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number is not (2 or 3)
                || !root.TryGetProperty("Windows", out var windows) || windows.ValueKind != JsonValueKind.Array) return;
            // Deserialize the optional checkpoint independently: malformed cursor metadata
            // must not discard valid closed segments.
            if (number == 3 && root.TryGetProperty("LedgerCheckpoint", out var checkpoint) && checkpoint.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    if (Encoding.UTF8.GetByteCount(checkpoint.GetRawText()) <= MaximumCheckpointBytes)
                    {
                        var cp = checkpoint.Deserialize<UsageLedgerCheckpoint>();
                        if (cp is not null && Valid(cp.Counts) && cp.Sequence >= 0 && !string.IsNullOrWhiteSpace(cp.Generation)
                            && !string.IsNullOrWhiteSpace(cp.Epoch) && cp.HasValidChecksum()) _ledgerCheckpoint = cp;
                    }
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException) { }
            }
            foreach (var item in windows.EnumerateArray().Take(MaximumWindows))
            {
                try
                {
                    var window = item.Deserialize<StoredWindow>();
                    if (window is null || !HashValue(window.KeyHash) || !HashValue(window.AccountHash) || !HashValue(window.WindowHash)
                        || window.ModelHash is not null && !HashValue(window.ModelHash) || window.Segments is null
                        || window.Remaining is not null && !Percent(window.Remaining) || !double.IsFinite(window.PendingDrop) || window.PendingDrop is < 0 or > 100
                        || window.LastResetReason is { Length: > 256 } || window.Reason is { Length: > 256 }) continue;
                    var valid = window.Segments.TakeLast(MaximumSegments).Where(ValidSegment).OrderBy(s => s.From).ToArray();
                    if (valid.Length != window.Segments.Count || valid.Sum(s => s.Drop) > 100 + 1e-9
                        || valid.Zip(valid.Skip(1), (a, b) => a.Through <= b.From).Any(ok => !ok)
                        || valid.Length > 0 && (window.Remaining is null || window.ObservedAt is null || valid.Any(s => s.Through > window.ObservedAt))) continue;
                    _restored[window.KeyHash] = window with { Segments = valid.ToList(),
                        Diagnostics = (window.Diagnostics ?? []).Where(e => e is not null && e.Reason is { Length: <= 256 } && e.Kind is { Length: <= 64 }).TakeLast(MaximumEvents).ToList(),
                        Isolated = (window.Isolated ?? []).Where(e => e is not null && double.IsFinite(e.Drop) && e.Drop is >= 0 and <= 100 && e.Reason is { Length: <= 256 }).TakeLast(MaximumEvents).ToList() };
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException) { }
            }
            if (root.TryGetProperty("Archives", out var archives) && archives.ValueKind == JsonValueKind.Array)
                foreach (var item in archives.EnumerateArray().TakeLast(MaximumArchives))
                    try
                    {
                        var archived = item.Deserialize<ArchivedCalibration>();
                        if (archived is not null && HashValue(archived.KeyHash) && HashValue(archived.AccountHash)
                            && archived.Reason is { Length: <= 256 } && archived.Segments is { Count: <= MaximumSegments }
                            && archived.Segments.All(ValidSegment)) _archives.Add(archived);
                    }
                    catch (JsonException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { _globalReason = "历史校准记录不可用，重新采样。"; }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_storagePath)) return;
        string? temporary = null;
        try
        {
            var path = Path.GetFullPath(_storagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var checkpoint = _ledgerCheckpoint;
            if (checkpoint is not null && JsonSerializer.SerializeToUtf8Bytes(checkpoint).Length > MaximumCheckpointBytes) checkpoint = null;
            var windows = _windows.Where(p => p.Value.AccountHash is not null && p.Value.WindowHash is not null).Take(MaximumWindows)
                .Select(p => Store(p.Key, p.Value, checkpoint)).ToList();
            foreach (var pending in _restored.Values)
            {
                if (windows.Count >= MaximumWindows) break;
                if ((_accountHash is null || pending.AccountHash == _accountHash) && windows.All(w => w.KeyHash != pending.KeyHash)) windows.Add(pending);
            }
            var data = new StoredHistory(3, windows) { SavedAt = DateTimeOffset.UtcNow, GlobalReason = _globalReason,
                LedgerCheckpoint = checkpoint, Archives = _archives.TakeLast(MaximumArchives).ToList() };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
            if (bytes.Length > MaximumFileBytes)
                bytes = JsonSerializer.SerializeToUtf8Bytes(data with { LedgerCheckpoint = null,
                    Windows = windows.Select(w => w with { Anchor = null, LastCounts = null }).ToList(), GlobalReason = "检查点超过保存预算，闭合样本已保留。" });
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException) { }
        finally
        { if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static StoredWindow Store(string key, WindowState state, UsageLedgerCheckpoint? checkpoint)
    {
        bool paired = checkpoint is not null && state.CheckpointGeneration == checkpoint.Generation
            && state.CheckpointSequence <= checkpoint.Sequence && state.EpochHash == Hash(checkpoint.Epoch)
            && state.LastCounts is { } last && !Regressed(last, checkpoint.Counts) && state.ObservedAt <= checkpoint.ObservedAt;
        return new(Hash(key), state.AccountHash!, state.WindowHash!, state.ModelHash, state.Segments.ToList())
        {
            Remaining = state.Remaining, ObservedAt = state.ObservedAt, QuotaObservedAt = state.QuotaObservedAt,
            UsageVaried = state.UsageVaried, LastResetAt = state.LastResetAt, LastResetReason = state.LastResetReason,
            Reason = state.Reason, Status = state.Status.ToString(), PendingDrop = state.PendingDrop,
            IdentityHash = state.IdentityHash, AcceptedResetAt = state.AcceptedResetAt, MetadataResetAt = state.MetadataResetAt, CycleId = state.CycleId,
            Anchor = paired ? state.Anchor : null, LastCounts = paired ? state.LastCounts : null,
            EpochHash = paired ? state.EpochHash : null, CheckpointGeneration = paired ? state.CheckpointGeneration : null,
            CheckpointSequence = paired ? state.CheckpointSequence : 0, ContinuityBroken = state.ContinuityBroken,
            Candidate = state.Candidate, Diagnostics = state.Diagnostics.ToList(), Isolated = state.Isolated.ToList()
        };
    }
    private static bool HashValue(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidAnchor(Anchor a) => Percent(a.Remaining) && Valid(a.Counts);
    private static bool ValidSegment(Segment s) => s is not null && s.From < s.Through && s.Through - s.From <= TimeSpan.FromHours(2)
        && double.IsFinite(s.Drop) && s.Drop is >= MinimumDrop and <= 100 && double.IsFinite(s.Tokens) && s.Tokens > 0 && s.Tokens <= long.MaxValue
        && double.IsFinite(s.Input) && s.Input >= 0 && s.Input <= s.Tokens && double.IsFinite(s.Output) && s.Output >= 0 && s.Output <= s.Tokens
        && double.IsFinite(s.Cached) && s.Cached >= 0 && s.Cached <= s.Input
        && Math.Abs(s.Input + s.Output - s.Tokens) <= Math.Max(1, s.Tokens * 1e-12);

    private sealed class WindowState
    {
        public string? AccountHash, WindowHash, IdentityHash, CycleId, ModelHash, EpochHash, CheckpointGeneration;
        public DateTimeOffset? ObservedAt, QuotaObservedAt, UnmatchedAt, AcceptedResetAt, MetadataResetAt, RecoveryObservedAt;
        public double? Remaining;
        public Anchor? Anchor;
        public TokenCounts? LastCounts;
        public BoundaryCandidate? Candidate;
        public List<Segment> Segments { get; } = new();
        public List<QuotaCalibrationEvent> Diagnostics { get; } = new();
        public List<IsolatedFragment> Isolated { get; } = new();
        public EstimateState Status = EstimateState.Calibrating;
        public string Reason = "等待同步采样。";
        public bool NeedsConfirmation, UsageVaried, Paused, RestoredAnchor, ContinuityBroken;
        public int RecoverySamples;
        public long CheckpointSequence;
        public double PendingDrop;
        public DateTimeOffset? LastResetAt;
        public string? LastResetReason;
    }
    private sealed record Anchor(DateTimeOffset Time, double Remaining, TokenCounts Counts);
    private sealed record Segment(DateTimeOffset From, DateTimeOffset Through, double Drop, double Tokens, double Input, double Cached, double Output);
    private sealed record BoundaryCandidate(string Kind, DateTimeOffset ReportedResetAt, DateTimeOffset LastObservedAt, int Samples);
    private sealed record IsolatedFragment(DateTimeOffset At, double Drop, string Reason)
    {
        public double PreviousPendingDrop { get; init; }
        public double? AdditionalUnpairedDrop { get; init; }
    }
    private sealed record ArchivedCalibration(string KeyHash, string AccountHash, string? WindowHash, DateTimeOffset At, string Reason, List<Segment> Segments);
    private sealed record StoredWindow(string KeyHash, string AccountHash, string WindowHash, string? ModelHash, List<Segment> Segments)
    {
        public double? Remaining { get; init; }
        public DateTimeOffset? ObservedAt { get; init; }
        public bool UsageVaried { get; init; }
        public string? Reason { get; init; }
        public string? Status { get; init; }
        public double PendingDrop { get; init; }
        public DateTimeOffset? LastResetAt { get; init; }
        public string? LastResetReason { get; init; }
        public string? IdentityHash { get; init; }
        public string? CycleId { get; init; }
        public DateTimeOffset? AcceptedResetAt { get; init; }
        public DateTimeOffset? MetadataResetAt { get; init; }
        public DateTimeOffset? QuotaObservedAt { get; init; }
        public Anchor? Anchor { get; init; }
        public TokenCounts? LastCounts { get; init; }
        public string? EpochHash { get; init; }
        public string? CheckpointGeneration { get; init; }
        public long CheckpointSequence { get; init; }
        public bool ContinuityBroken { get; init; }
        public BoundaryCandidate? Candidate { get; init; }
        public List<QuotaCalibrationEvent>? Diagnostics { get; init; }
        public List<IsolatedFragment>? Isolated { get; init; }
    }
    private sealed record StoredHistory(int Version, List<StoredWindow> Windows)
    {
        public DateTimeOffset SavedAt { get; init; }
        public string? GlobalReason { get; init; }
        public UsageLedgerCheckpoint? LedgerCheckpoint { get; init; }
        public List<ArchivedCalibration> Archives { get; init; } = new();
    }
}
