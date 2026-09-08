using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexHud.Core;

public enum EstimateState { Calibrating, Estimated, Variable, Incomplete, Stale, Unavailable }

public sealed record QuotaTokenEstimate(string QuotaKey, EstimateState State, double? RemainingTokens,
    double? LowerTokens, double? UpperTokens, int Segments, double ObservedDrop,
    DateTimeOffset? From, DateTimeOffset? Through, string Detail);

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
                BreakAll(EstimateState.Stale, "额度读数未更新，等待重新校准。", _windows.Count == 0);
                return;
            }
            if (string.IsNullOrWhiteSpace(quota.AccountKey))
            {
                BreakAll(EstimateState.Incomplete, "账户身份未确认，暂停配对。", _windows.Count == 0);
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
            if (usage.Health != SampleHealth.Fresh || !Valid(usage.Counts)
                || string.IsNullOrWhiteSpace(usage.Epoch))
            {
                BreakAll(EstimateState.Incomplete, "本机 Token 采样不完整，等待连续记录后重新校准。", _windows.Count == 0);
                return;
            }
            if (AbsSeconds(quota.ObservedAt, usage.ObservedAt) > MaximumAlignment.TotalSeconds)
            {
                BreakAll(EstimateState.Incomplete, "额度与 Token 时间未对齐，重新校准。", _windows.Count == 0);
                return;
            }
            _globalStatus = EstimateState.Calibrating;

            var currentKeys = quota.Windows.Select(window => window.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var pair in _windows.Where(pair => !currentKeys.Contains(pair.Key)))
                Reset(pair.Value, EstimateState.Unavailable, "额度周期已消失，请重新选择。");

            foreach (var window in quota.Windows.Where(Supported).Take(MaximumWindows))
            {
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
                return Result(window.Key, state, EstimateState.Stale, "采样已过期，等待新的同步读数。");
            if (state.WindowHash != WindowHash(window) || window.ResetsAt <= now)
                return Result(window.Key, state, EstimateState.Calibrating, "额度已重置，等待重新校准。");
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
            var variable = highRate / lowRate > 2 || Math.Sqrt(variance) / rate > .35;
            var remaining = state.Remaining!.Value;
            var detail = Scope + (variable ? "分段比例波动较大。" : "")
                + "范围取近期有效分段的最小值与最大值，并非置信区间。"
                + (state.ModelHash is null ? "模型/速度信息未齐全。" : "");
            return new(window.Key, variable ? EstimateState.Variable : EstimateState.Estimated,
                rate * remaining, lowRate * remaining, highRate * remaining,
                state.Segments.Count, drop, state.Segments[0].From, state.ObservedAt, detail);
        }
    }

    public void Invalidate(string reason)
    {
        lock (_gate)
        {
            BreakAll(EstimateState.Incomplete, string.IsNullOrWhiteSpace(reason) ? "采样中断，重新校准。" : reason);
        }
    }

    private void ObserveWindow(WindowState state, QuotaWindow window, DateTimeOffset time,
        UsageLedgerSnapshot usage, string account)
    {
        if (!Percent(window.RemainingPercent) || window.WindowMinutes is null or <= 0)
        {
            Reset(state, EstimateState.Incomplete, "额度周期或百分比缺失，等待完整读数。");
            return;
        }
        if (window.ResetsAt <= time)
        {
            Reset(state, EstimateState.Calibrating, "等待重置后的额度读数。");
            return;
        }
        var identity = WindowHash(window);
        var model = string.IsNullOrWhiteSpace(usage.ModelMix) ? null : Hash(usage.ModelMix);
        if (state.WindowHash != identity || state.AccountHash != account)
        {
            Reset(state, EstimateState.Calibrating, "新的额度周期，开始校准。");
            state.WindowHash = identity;
            state.AccountHash = account;
            TryRestore(state, window.Key, account, identity, model);
            state.ModelHash = model;
        }
        Prune(state, time);
        if (state.Anchor is null)
        {
            TryRestore(state, window.Key, account, identity, model);
            Baseline(state, window, time, usage, model);
            return;
        }
        if (time == state.ObservedAt) return; // Replaying a quota reading cannot make it fresh.
        if (time < state.ObservedAt)
        {
            Reset(state, EstimateState.Stale, "额度记录时间回退，等待新的采样。");
            return;
        }
        var interruption = time - state.ObservedAt > MaximumGap || state.Epoch != usage.Epoch;
        var changed = state.ModelHash != model;
        if (interruption || changed || Regressed(state.LastCounts!, usage.Counts))
        {
            Reset(state, EstimateState.Calibrating, changed ? "模型或速度模式改变，重新校准。"
                : "采样中断或累计记录重建，重新校准。");
            TryRestore(state, window.Key, account, identity, model);
            Prune(state, time);
            Baseline(state, window, time, usage, model);
            return;
        }
        if (window.RemainingPercent > state.Remaining)
        {
            Reset(state, EstimateState.Calibrating, "额度回升或重置，重新校准。");
            Baseline(state, window, time, usage, model);
            return;
        }
        state.ObservedAt = time;
        state.Remaining = window.RemainingPercent;
        state.LastCounts = usage.Counts;
        var anchor = state.Anchor;
        var drop = anchor.Remaining - window.RemainingPercent!.Value;
        var delta = Difference(anchor.Counts, usage.Counts);
        if (delta.Cached > delta.Input || delta.Output > delta.Tokens || delta.Input > delta.Tokens)
        {
            Reset(state, EstimateState.Incomplete, "Token 明细增量不一致，重新校准。");
            return;
        }
        if (drop > 0 && delta.Tokens == 0)
        {
            state.UnmatchedAt ??= time;
            state.Status = EstimateState.Incomplete;
            state.Reason = "额度变化尚未匹配到本机 Token，等待记录更新。";
            if (time - state.UnmatchedAt > TokenLagGrace)
            {
                Reset(state, EstimateState.Calibrating, "额度与本机 Token 未匹配，重新校准。");
                Baseline(state, window, time, usage, model);
            }
            return;
        }
        state.UnmatchedAt = null;
        state.Status = EstimateState.Calibrating;
        if (time - anchor.Time > TimeSpan.FromHours(2))
        {
            Reset(state, EstimateState.Calibrating, "本段变化跨度过长，重新校准。");
            Baseline(state, window, time, usage, model);
            return;
        }
        if (drop < MinimumDrop || delta.Tokens <= 0) return;
        var segment = new Segment(anchor.Time, time, drop, delta.Tokens, delta.Input, delta.Cached, delta.Output);
        if (state.Segments.Count > 0 && StructureChanged(state.Segments, segment))
        {
            Reset(state, EstimateState.Calibrating, "输入、缓存或输出结构改变，重新校准。");
            Baseline(state, window, time, usage, model);
            return;
        }
        state.Segments.Add(segment);
        state.NeedsConfirmation = false;
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
        state.ModelHash = model;
        state.Status = EstimateState.Calibrating;
        state.UnmatchedAt = null;
    }

    private void TryRestore(WindowState state, string key, string account, string identity, string? model)
    {
        var keyHash = Hash(key);
        if (!_restored.TryGetValue(keyHash, out var stored)) return;
        if (stored.AccountHash != account || stored.WindowHash != identity)
        {
            _restored.Remove(keyHash);
            return;
        }
        // On startup a fresh ledger can precede its first model-bearing token record.
        // Keep the candidate history until the model is known, without using its estimate.
        if (model is null && stored.ModelHash is not null) return;
        _restored.Remove(keyHash);
        if (stored.ModelHash != model) return;
        state.Segments.AddRange(stored.Segments);
        state.NeedsConfirmation = state.Segments.Count > 0;
    }

    private void BreakAll(EstimateState status, string reason, bool preservePending = false)
    {
        _globalReason = reason;
        _globalStatus = status;
        if (!preservePending) _restored.Clear();
        foreach (var state in _windows.Values) Reset(state, status, reason);
        Save();
    }

    private static void Reset(WindowState state, EstimateState status, string reason)
    {
        state.Segments.Clear();
        state.Anchor = null;
        state.LastCounts = null;
        state.UnmatchedAt = null;
        state.NeedsConfirmation = false;
        state.Status = status;
        state.Reason = reason;
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
        new(key, status, null, null, null, 0, 0, null, null, detail + " " + Scope);
    private static QuotaTokenEstimate Result(string key, WindowState state, EstimateState status, string detail) =>
        new(key, status, null, null, null, state.Segments.Count, TotalDrop(state.Segments),
            state.Segments.Count > 0 ? state.Segments[0].From : state.Anchor?.Time, state.ObservedAt, detail + " " + Scope);

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
            if (data is not { Version: 1 } || data.Windows is null) return;
            foreach (var window in data.Windows.Take(MaximumWindows))
            {
                if (window is null || !HashValue(window.KeyHash) || !HashValue(window.AccountHash) || !HashValue(window.WindowHash)
                    || window.ModelHash is not null && !HashValue(window.ModelHash) || window.Segments is null) continue;
                var valid = window.Segments.TakeLast(MaximumSegments).Where(ValidSegment).OrderBy(segment => segment.From).ToArray();
                if (valid.Length != window.Segments.Count || valid.Sum(segment => segment.Drop) > 100 + 1e-9
                    || valid.Zip(valid.Skip(1), (a, b) => a.Through <= b.From).Any(ok => !ok)) continue;
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
            var windows = _windows.Where(pair => pair.Value.Segments.Count > 0).Take(MaximumWindows)
                .Select(pair => new StoredWindow(Hash(pair.Key), pair.Value.AccountHash!, pair.Value.WindowHash!,
                    pair.Value.ModelHash, pair.Value.Segments.ToList())).ToList();
            foreach (var pending in _restored.Values)
            {
                if (windows.Count >= MaximumWindows) break;
                if ((_accountHash is null || pending.AccountHash == _accountHash)
                    && windows.All(existing => existing.KeyHash != pending.KeyHash)) windows.Add(pending);
            }
            File.WriteAllText(temporary, JsonSerializer.Serialize(new StoredHistory(1, windows)));
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
        public bool NeedsConfirmation;
    }
    private sealed record Anchor(DateTimeOffset Time, double Remaining, TokenCounts Counts);
    private sealed record Segment(DateTimeOffset From, DateTimeOffset Through, double Drop, double Tokens, double Input, double Cached, double Output);
    private sealed record StoredWindow(string KeyHash, string AccountHash, string WindowHash, string? ModelHash, List<Segment> Segments);
    private sealed record StoredHistory(int Version, List<StoredWindow> Windows);
}
