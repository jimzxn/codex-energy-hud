using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexHud.Core;

public enum EstimateState { Calibrating, Estimated, Variable, Incomplete, Stale, Unavailable }

public sealed record QuotaTokenEstimate(string QuotaKey, EstimateState State, double? RemainingTokens,
    double? LowerTokens, double? UpperTokens, int Segments, double ObservedDrop,
    DateTimeOffset? From, DateTimeOffset? Through, string Detail)
{
    public double PendingDrop { get; init; }
    public string? ProgressReason { get; init; }
    public string? LastResetReason { get; init; }
    public DateTimeOffset? LastResetAt { get; init; }
}

/// <summary>
/// An empirical conversion from synchronized local token increments to quota percentage points.
/// Cached tokens remain part of input. This does not infer a subscription's fixed token capacity.
/// </summary>
public sealed class QuotaTokenEstimator
{
    private static readonly TimeSpan HistoryAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaximumAlignment = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FreshAge = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan TokenLagGrace = TimeSpan.FromMinutes(2);
    private const int MaximumSegments = 64;
    private const int MaximumWindows = 8;
    private const double MinimumDrop = 2;
    private const string Scope = "按本机近期用法估算；其他设备或云端用量可能影响比例。";
    private readonly object _gate = new();
    private readonly string? _storagePath;
    private readonly Dictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredWindow> _restored = new(StringComparer.Ordinal);
    private string? _accountHash;
    private string? _globalReason;
    private EstimateState _globalStatus = EstimateState.Calibrating;

    public QuotaTokenEstimator(string? storagePath = null)
    {
        _storagePath = storagePath;
        Load();
    }

    public void Observe(QuotaSnapshot quota, UsageLedgerSnapshot usage)
    {
        lock (_gate)
        {
            if (quota.Health is SampleHealth.Stale or SampleHealth.Unavailable or SampleHealth.Loading)
            {
                PauseAll(EstimateState.Stale, "额度读数未更新，等待重新校准。");
                return;
            }
            if (string.IsNullOrWhiteSpace(quota.AccountKey))
            {
                PauseAll(EstimateState.Incomplete, "账户身份未确认，暂停配对。");
                return;
            }
            var account = Hash(quota.AccountKey);
            if (_accountHash is not null && _accountHash != account)
            {
                _windows.Clear();
                _restored.Clear();
                _globalReason = "账户已变化，重新校准。";
            }
            _accountHash = account;
            var currentKeys = quota.Windows.Select(window => window.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var pair in _windows.Where(pair => !currentKeys.Contains(pair.Key)))
                Pause(pair.Value, EstimateState.Unavailable, "额度周期已消失，请重新选择。", quota.ObservedAt);
            foreach (var window in quota.Windows.Where(Supported))
            {
                bool identityComplete = window.WindowMinutes is > 0 && window.ResetsAt.HasValue;
                var identity = WindowHash(window);
                if (_windows.TryGetValue(window.Key, out var known) && (known.ObservedAt is null || quota.ObservedAt > known.ObservedAt))
                {
                    bool changed = identityComplete && (known.WindowHash != identity || window.ResetsAt <= quota.ObservedAt);
                    bool rebound = Percent(window.RemainingPercent) && window.RemainingPercent > known.Remaining;
                    if (changed || rebound)
                    {
                        Reset(known, EstimateState.Calibrating, changed
                            ? "额度周期已变化，开始校准。" : "额度回升，开始重新校准。", quota.ObservedAt);
                        if (identityComplete) known.WindowHash = identity;
                    }
                }
                var key = Hash(window.Key);
                if (_restored.TryGetValue(key, out var pending) && (pending.ObservedAt is null || quota.ObservedAt > pending.ObservedAt)
                    && (identityComplete && (pending.WindowHash != identity || window.ResetsAt <= quota.ObservedAt)
                        || Percent(window.RemainingPercent) && window.RemainingPercent > pending.Remaining))
                    _restored.Remove(key);
            }
            if (usage.Health != SampleHealth.Fresh || !Valid(usage.Counts)
                || string.IsNullOrWhiteSpace(usage.Epoch))
            {
                PauseAll(EstimateState.Incomplete, "本机 Token 采样不完整，等待连续记录后重新校准。");
                return;
            }
            if (AbsSeconds(quota.ObservedAt, usage.ObservedAt) > MaximumAlignment.TotalSeconds)
            {
                PauseAll(EstimateState.Incomplete, "额度与 Token 时间未对齐，重新校准。");
                return;
            }
            _globalStatus = EstimateState.Calibrating;
            _globalReason = null;

            foreach (var window in quota.Windows.Where(Supported).Take(MaximumWindows))
            {
                // Apply the saved watermark before creating an empty live state that could
                // overwrite a newer candidate history from another period on the next Save.
                if (_restored.TryGetValue(Hash(window.Key), out var candidate) && candidate.AccountHash == account
                    && candidate.ObservedAt is { } savedAt && quota.ObservedAt <= savedAt)
                {
                    _globalStatus = EstimateState.Stale;
                    _globalReason = "额度记录尚未超过已保存的采样时间，等待新读数。";
                    continue;
                }
                if (!_windows.TryGetValue(window.Key, out var state))
                {
                    if (_windows.Count >= MaximumWindows)
                        _windows.Remove(_windows.MinBy(pair => pair.Value.ObservedAt).Key);
                    state = new();
                    _windows[window.Key] = state;
                }
                ObserveWindow(state, window, quota.ObservedAt, usage, account);
            }
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
                return Empty(window.Key, _globalStatus, _globalReason ?? "等待同步采样。");
            Prune(state, now);
            if (state.ObservedAt is null || state.Status is EstimateState.Stale or EstimateState.Incomplete or EstimateState.Unavailable)
                return Result(window.Key, state, state.Status, state.Reason);
            if (now - state.ObservedAt.Value > FreshAge || state.ObservedAt.Value - now > MaximumAlignment)
                return Result(window.Key, state, EstimateState.Stale, "采样已过期，等待新的同步读数。")
                    with { PendingDrop = 0, ProgressReason = "采样已过期，等待新的同步读数。" };
            if (state.WindowHash != WindowHash(window) || window.ResetsAt <= now)
                return Result(window.Key, state, EstimateState.Calibrating, "额度已重置，等待重新校准。")
                    with { PendingDrop = 0, ProgressReason = "额度已重置，等待重新校准。" };
            if (!Percent(window.RemainingPercent) || window.RemainingPercent != state.Remaining)
                return Result(window.Key, state, EstimateState.Incomplete, "等待当前额度与 Token 配对。");
            if (state.NeedsConfirmation || state.Segments.Count < 3 || TotalDrop(state.Segments) < 6)
                return Result(window.Key, state, EstimateState.Calibrating, state.Reason + " 校准需至少 3 段有效变化、累计下降 6 个百分点。");

            var drop = TotalDrop(state.Segments);
            var rate = state.Segments.Sum(segment => segment.Tokens) / drop;
            var rates = state.Segments.Select(segment => segment.Tokens / segment.Drop).ToArray();
            var lowRate = rates.Min();
            var highRate = rates.Max();
            var variance = state.Segments.Sum(segment => segment.Drop * Math.Pow(segment.Tokens / segment.Drop - rate, 2)) / drop;
            var variable = state.UsageVaried || highRate / lowRate > 2 || Math.Sqrt(variance) / rate > .35;
            var remaining = state.Remaining!.Value;
            var detail = Scope + (variable ? "分段比例波动较大。" : "")
                + "范围取近期有效分段的最小值与最大值，并非置信区间。"
                + (state.ModelHash is null ? "模型/速度信息未齐全。" : "");
            return new(window.Key, variable ? EstimateState.Variable : EstimateState.Estimated,
                rate * remaining, lowRate * remaining, highRate * remaining,
                state.Segments.Count, drop, state.Segments[0].From, state.ObservedAt, detail)
            { PendingDrop = state.PendingDrop, ProgressReason = state.Reason,
                LastResetReason = state.LastResetReason, LastResetAt = state.LastResetAt };
        }
    }

    public void Invalidate(string reason)
    {
        lock (_gate)
        {
            PauseAll(EstimateState.Incomplete, string.IsNullOrWhiteSpace(reason) ? "采样中断，重新校准。" : reason);
        }
    }

    private void ObserveWindow(WindowState state, QuotaWindow window, DateTimeOffset time,
        UsageLedgerSnapshot usage, string account)
    {
        // Check before identity handling: an old snapshot from a different period is still old.
        if (state.ObservedAt is { } previous && time <= previous)
        {
            if (time < previous) Pause(state, EstimateState.Stale, "额度记录时间回退，等待新的采样。", time);
            return;
        }
        if (!Percent(window.RemainingPercent) || window.WindowMinutes is null or <= 0 || window.ResetsAt is null)
        {
            Pause(state, EstimateState.Incomplete, "额度周期、重置时间或百分比缺失，等待完整读数。", time);
            return;
        }
        if (window.ResetsAt <= time)
        {
            Reset(state, EstimateState.Calibrating, "等待重置后的额度读数。", time);
            return;
        }
        var identity = WindowHash(window);
        var model = string.IsNullOrWhiteSpace(usage.ModelMix) ? null : Hash(usage.ModelMix);
        if (state.WindowHash != identity || state.AccountHash != account)
        {
            Reset(state, EstimateState.Calibrating, "新的额度周期，开始校准。", time);
            state.WindowHash = identity;
            state.AccountHash = account;
            TryRestore(state, window.Key, account, identity, model, time);
        }
        TryRestore(state, window.Key, account, identity, model, time);
        // Keep the watermark through a pause: a replay cannot establish a new baseline.
        if (time == state.ObservedAt) return;
        if (time < state.ObservedAt)
        {
            Pause(state, EstimateState.Stale, "额度记录时间回退，等待新的采样。", time);
            return;
        }
        Prune(state, time);
        if (window.RemainingPercent > state.Remaining)
            Reset(state, EstimateState.Calibrating, "额度回升或重置，重新校准。", time);
        TrackModel(state, model);
        if (state.Anchor is null)
        {
            Baseline(state, window, time, usage, model);
            return;
        }
        var interruption = time - state.ObservedAt > MaximumGap || state.Epoch != usage.Epoch;
        if (interruption || Regressed(state.LastCounts!, usage.Counts))
        {
            Pause(state, EstimateState.Calibrating, "采样中断，已保留有效分段。", time);
            Baseline(state, window, time, usage, model);
            return;
        }
        state.ObservedAt = time;
        state.Remaining = window.RemainingPercent;
        state.LastCounts = usage.Counts;
        var anchor = state.Anchor;
        var drop = anchor.Remaining - window.RemainingPercent!.Value;
        var delta = Difference(anchor.Counts, usage.Counts);
        state.PendingDrop = Math.Clamp(drop, 0, 100);
        if (delta.Cached > delta.Input || delta.Output > delta.Tokens || delta.Input > delta.Tokens)
        {
            Pause(state, EstimateState.Incomplete, "Token 明细增量不一致，等待重新对齐。", time);
            return;
        }
        if (drop > 0 && delta.Tokens == 0)
        {
            state.UnmatchedAt ??= time;
            state.Status = EstimateState.Incomplete;
            state.Reason = "额度变化尚未匹配到本机 Token，等待记录更新。";
            if (time - state.UnmatchedAt > TokenLagGrace)
            {
                Pause(state, EstimateState.Calibrating, "额度与本机 Token 未匹配，已保留有效分段。", time);
                Baseline(state, window, time, usage, model);
            }
            return;
        }
        state.UnmatchedAt = null;
        state.Status = EstimateState.Calibrating;
        state.Reason = state.NeedsConfirmation ? "等待一段新的有效变化，确认保留样本。" : "正在累计有效变化。";
        if (time - anchor.Time > TimeSpan.FromHours(2))
        {
            Pause(state, EstimateState.Calibrating, "本段跨度超过两小时，已保留有效分段。", time);
            Baseline(state, window, time, usage, model);
            return;
        }
        if (drop < MinimumDrop || delta.Tokens <= 0) return;
        var segment = new Segment(anchor.Time, time, drop, delta.Tokens, delta.Input, delta.Cached, delta.Output);
        if (state.Segments.Count > 0 && StructureChanged(state.Segments, segment))
            state.UsageVaried = true;
        state.Segments.Add(segment);
        state.NeedsConfirmation = false;
        state.PendingDrop = 0;
        state.Reason = "有效分段已保存，继续采样。";
        Prune(state, time);
        state.Anchor = new(time, window.RemainingPercent.Value, usage.Counts);
    }

    private static void Baseline(WindowState state, QuotaWindow window, DateTimeOffset time,
        UsageLedgerSnapshot usage, string? model)
    {
        state.Anchor = new(time, window.RemainingPercent!.Value, usage.Counts);
        state.LastCounts = usage.Counts;
        state.ObservedAt = time;
        state.Remaining = window.RemainingPercent;
        state.Epoch = usage.Epoch;
        TrackModel(state, model);
        state.PendingDrop = 0;
        state.Reason = state.NeedsConfirmation ? "采样已重新对齐，等待一段新的有效变化。" : "正在累计有效变化。";
        state.Status = EstimateState.Calibrating;
        state.UnmatchedAt = null;
    }

    private static void TrackModel(WindowState state, string? model)
    {
        // A ten-minute idle profile expiring to null is not a change in token accounting.
        if (model is null) return;
        if (state.ModelHash is not null && state.ModelHash != model) state.UsageVaried = true;
        state.ModelHash = model;
    }

    private void TryRestore(WindowState state, string key, string account, string identity, string? model, DateTimeOffset time)
    {
        if (!_restored.Remove(Hash(key), out var stored)) return;
        if (stored.AccountHash != account || stored.WindowHash != identity) return;
        state.Segments.AddRange(stored.Segments);
        state.ObservedAt = stored.ObservedAt;
        state.Remaining = stored.Remaining;
        state.ModelHash = stored.ModelHash;
        state.UsageVaried = stored.UsageVaried;
        state.LastResetAt = stored.LastResetAt;
        state.LastResetReason = stored.LastResetReason;
        state.NeedsConfirmation = state.Segments.Count > 0;
        TrackModel(state, model);
        Prune(state, time);
    }

    private void PauseAll(EstimateState status, string reason)
    {
        _globalReason = reason;
        _globalStatus = status;
        foreach (var state in _windows.Values) Pause(state, status, reason);
        // Closed history stays on disk even while its live account/ledger is unavailable.
        Save();
    }

    private static void Pause(WindowState state, EstimateState status, string reason, DateTimeOffset? time = null)
    {
        if (state.Anchor is not null || state.LastResetReason != reason)
        {
            state.LastResetAt = time ?? DateTimeOffset.UtcNow;
            state.LastResetReason = reason;
        }
        state.Anchor = null;
        state.LastCounts = null;
        state.UnmatchedAt = null;
        state.PendingDrop = 0;
        state.NeedsConfirmation = state.Segments.Count > 0;
        state.Status = status;
        state.Reason = reason;
    }

    private static void Reset(WindowState state, EstimateState status, string reason, DateTimeOffset? time = null)
    {
        state.Segments.Clear();
        state.UsageVaried = false;
        state.ModelHash = null;
        state.Remaining = null;
        state.ObservedAt = null;
        Pause(state, status, reason, time);
    }

    private static bool Supported(QuotaWindow window) => string.Equals(window.LimitId, "codex", StringComparison.OrdinalIgnoreCase);
    private static bool Percent(double? value) => value is { } number && double.IsFinite(number) && number is >= 0 and <= 100;
    private static bool Valid(TokenCounts counts) => counts.TotalTokens is >= 0 && counts.InputTokens is >= 0
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
        var input = previous.Sum(segment => segment.Input);
        var cached = previous.Sum(segment => segment.Cached);
        var outputShare = previous.Sum(segment => segment.Output) / previous.Sum(segment => segment.Tokens);
        return Math.Abs(next.Output / next.Tokens - outputShare) > .20
            || input > 0 && next.Input > 0 && Math.Abs(next.Cached / next.Input - cached / input) > .25;
    }
    private static double AbsSeconds(DateTimeOffset a, DateTimeOffset b) => Math.Abs((a - b).TotalSeconds);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string WindowHash(QuotaWindow window) => Hash($"{window.LimitId.ToLowerInvariant()}|{window.Slot}|{window.WindowMinutes}|{window.ResetsAt?.UtcTicks}");
    private static void Prune(WindowState state, DateTimeOffset time)
    {
        state.Segments.RemoveAll(segment => time - segment.From > HistoryAge || segment.Through - time > MaximumAlignment);
        if (state.Segments.Count > MaximumSegments) state.Segments.RemoveRange(0, state.Segments.Count - MaximumSegments);
    }
    private static QuotaTokenEstimate Empty(string key, EstimateState status, string detail) =>
        new(key, status, null, null, null, 0, 0, null, null, detail + " " + Scope) { ProgressReason = detail };
    private static QuotaTokenEstimate Result(string key, WindowState state, EstimateState status, string detail) =>
        new(key, status, null, null, null, state.Segments.Count, TotalDrop(state.Segments),
            state.Segments.Count > 0 ? state.Segments[0].From : state.Anchor?.Time, state.ObservedAt, detail + " " + Scope)
        { PendingDrop = state.PendingDrop, ProgressReason = status == state.Status ? state.Reason : detail,
            LastResetReason = state.LastResetReason, LastResetAt = state.LastResetAt };

    // Each segment is a disjoint decline in one monotone 0..100 quota period. Floating-point
    // summation can yield 100.00000000000001 at its zero endpoint; preserve the known domain.
    private static double TotalDrop(IEnumerable<Segment> segments) => Math.Clamp(segments.Sum(segment => segment.Drop), 0, 100);

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_storagePath)) return;
        try
        {
            var info = new FileInfo(_storagePath);
            if (!info.Exists || info.Length > 512 * 1024) return;
            var data = JsonSerializer.Deserialize<StoredHistory>(File.ReadAllText(_storagePath));
            // Version 1 used a different collection scope and must not seed the corrected ratio.
            if (data is not { Version: 2 } || data.Windows is null) return;
            foreach (var window in data.Windows.Take(MaximumWindows))
            {
                if (window is null || !HashValue(window.KeyHash) || !HashValue(window.AccountHash) || !HashValue(window.WindowHash)
                    || window.ModelHash is not null && !HashValue(window.ModelHash) || window.Segments is null
                    || window.Remaining is not null && !Percent(window.Remaining)
                    || window.LastResetReason is { Length: > 256 } || window.Reason is { Length: > 256 }) continue;
                var valid = window.Segments.TakeLast(MaximumSegments).Where(ValidSegment).OrderBy(segment => segment.From).ToArray();
                if (valid.Length != window.Segments.Count || valid.Sum(segment => segment.Drop) > 100 + 1e-9
                    || valid.Zip(valid.Skip(1), (a, b) => a.Through <= b.From).Any(ok => !ok)
                    || valid.Length > 0 && (window.Remaining is null || window.ObservedAt is null
                        || valid.Any(segment => segment.Through > window.ObservedAt))) continue;
                _restored[window.KeyHash] = window with { Segments = valid.ToList() };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            _globalReason = "历史校准记录不可用，重新采样。";
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_storagePath)) return;
        string? temporary = null;
        try
        {
            var path = Path.GetFullPath(_storagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var windows = _windows.Where(pair => pair.Value.AccountHash is not null && pair.Value.WindowHash is not null).Take(MaximumWindows)
                .Select(pair => new StoredWindow(Hash(pair.Key), pair.Value.AccountHash!, pair.Value.WindowHash!,
                    pair.Value.ModelHash, pair.Value.Segments.ToList())
                { Remaining = pair.Value.Remaining, ObservedAt = pair.Value.ObservedAt,
                    UsageVaried = pair.Value.UsageVaried, LastResetAt = pair.Value.LastResetAt,
                    LastResetReason = pair.Value.LastResetReason, Reason = pair.Value.Reason,
                    Status = pair.Value.Status.ToString(), PendingDrop = pair.Value.PendingDrop }).ToList();
            foreach (var pending in _restored.Values)
            {
                if (windows.Count >= MaximumWindows) break;
                if ((_accountHash is null || pending.AccountHash == _accountHash)
                    && windows.All(existing => existing.KeyHash != pending.KeyHash)) windows.Add(pending);
            }
            File.WriteAllText(temporary, JsonSerializer.Serialize(new StoredHistory(2, windows) { SavedAt = DateTimeOffset.UtcNow, GlobalReason = _globalReason }));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Persistence failure must not take down live telemetry. The next sample retries.
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool HashValue(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidSegment(Segment value) => value is not null && value.From < value.Through && value.Through - value.From <= TimeSpan.FromHours(2)
        && double.IsFinite(value.Drop) && value.Drop is >= MinimumDrop and <= 100
        && double.IsFinite(value.Tokens) && value.Tokens > 0 && value.Tokens <= long.MaxValue
        && double.IsFinite(value.Input) && value.Input >= 0 && value.Input <= value.Tokens
        && double.IsFinite(value.Output) && value.Output >= 0 && value.Output <= value.Tokens
        && double.IsFinite(value.Cached) && value.Cached >= 0 && value.Cached <= value.Input
        && Math.Abs(value.Input + value.Output - value.Tokens) <= Math.Max(1, value.Tokens * 1e-12);

    private sealed class WindowState
    {
        public string? AccountHash, WindowHash, ModelHash, Epoch;
        public DateTimeOffset? ObservedAt, UnmatchedAt;
        public double? Remaining;
        public Anchor? Anchor;
        public TokenCounts? LastCounts;
        public List<Segment> Segments { get; } = new();
        public EstimateState Status = EstimateState.Calibrating;
        public string Reason = "等待同步采样。";
        public bool NeedsConfirmation, UsageVaried;
        public double PendingDrop;
        public DateTimeOffset? LastResetAt;
        public string? LastResetReason;
    }
    private sealed record Anchor(DateTimeOffset Time, double Remaining, TokenCounts Counts);
    private sealed record Segment(DateTimeOffset From, DateTimeOffset Through, double Drop, double Tokens, double Input, double Cached, double Output);
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
    }
    private sealed record StoredHistory(int Version, List<StoredWindow> Windows)
    {
        public DateTimeOffset SavedAt { get; init; }
        public string? GlobalReason { get; init; }
    }
}
