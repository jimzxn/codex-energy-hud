using System.Text.Json;

namespace CodexHud.Core;

/// <summary>A locally observed quota period. Amounts belong to [StartedAt, EndsAt).</summary>
public sealed record UsagePeriod
{
    public string Id { get; init; } = "";
    public string WindowKey { get; init; } = "";
    public string Label { get; init; } = "";
    public string AccountKey { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
    public bool IsCurrent { get; init; }
    public string Reason { get; init; } = "Initial";
    public bool IsEstimated { get; init; }
    public DateTimeOffset? BoundaryEarliestAt { get; init; }
    public string? ResetEventId { get; init; }
    public bool IsPending { get; init; }
    public bool IsCorrected { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>
/// Read-only quota observations establish reporting boundaries independently of token collection.
/// A refill is provisional until a second healthy sample; its boundary remains the first observation.
/// </summary>
public sealed class BillingCycleTracker
{
    private static readonly TimeSpan DriftTolerance = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly string? _checkpointPath;
    private Checkpoint _state = new();
    private string? _detail;
    private DateTimeOffset? _retryNotBefore;

    public BillingCycleTracker(string? checkpointPath = null)
    {
        _checkpointPath = checkpointPath;
        Restore();
    }

    public string? Detail { get { lock (_gate) return _detail; } }
    public DateTimeOffset? RecheckAt
    {
        get
        {
            lock (_gate)
            {
                var due = CurrentAccount()?.Windows.Values
                    .SelectMany(w => new DateTimeOffset?[] { w.Pending?.RecheckAt, w.ManualMerge?.RecheckAt })
                    .Where(at => at.HasValue).Min();
                return due is { } at && _retryNotBefore is { } retry && retry > at ? retry : due;
            }
        }
    }

    public void Observe(QuotaSnapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot.Health is not (SampleHealth.Fresh or SampleHealth.Partial)
                || string.IsNullOrWhiteSpace(snapshot.AccountKey))
            {
                _retryNotBefore = DateTimeOffset.UtcNow + RecheckDelay;
                _detail = "额度或账户身份暂不可用，保留已有周期，暂停重置识别。";
                return;
            }
            var accountKey = snapshot.AccountKey;
            _state.CurrentAccount = accountKey;
            if (!_state.Accounts.TryGetValue(accountKey, out var account))
                _state.Accounts[accountKey] = account = new();
            if (snapshot.ObservedAt <= account.LastObservedAt)
            {
                _retryNotBefore = DateTimeOffset.UtcNow + RecheckDelay;
                _detail = "额度观测时间未前进；保留已有周期，等待新的健康读数。";
                return;
            }
            _retryNotBefore = null;
            var now = snapshot.ObservedAt;
            var cards = snapshot.ResetCredits;
            bool healthyCards = cards.Health == SampleHealth.Fresh && cards.AvailableCount is >= 0;
            bool cardDecrease = false;
            if (healthyCards && account.CreditCount is { } previousCount
                && account.CreditsObservedAt is { } previousAt && previousAt < now)
            {
                int drop = previousCount - cards.AvailableCount!.Value;
                int possibleExpiry = account.Credits.Where(c => c.ExpiresAt > previousAt && c.ExpiresAt <= now)
                    .Select(c => c.Id).Distinct(StringComparer.Ordinal).Count();
                cardDecrease = drop > possibleExpiry;
            }
            // A manual record and the later observed refill describe one event. Let the other
            // affected windows in this sample share its identity as well.
            var manualEvent = snapshot.Windows.Where(w => account.Windows.TryGetValue(w.Key, out var state)
                    && state.ManualMerge != null && w.ResetsAt > now
                    && (cardDecrease || w.RemainingPercent > state.LastRemaining + .000001
                        || w.ResetsAt > state.AcceptedResetAt + DriftTolerance))
                .Select(w => account.Windows[w.Key].Periods.FirstOrDefault(p =>
                    p.Id == account.Windows[w.Key].ManualMerge!.PeriodId && p.StartedAt <= now))
                .Where(p => p != null).OrderByDescending(p => p!.StartedAt).FirstOrDefault();
            string batchEvent = manualEvent?.ResetEventId ?? Guid.NewGuid().ToString("N");
            int accepted = 0;
            foreach (var window in snapshot.Windows)
            {
                if (string.IsNullOrWhiteSpace(window.Key) || window.WindowMinutes is null or <= 0
                    || window.RemainingPercent is not { } remaining || !double.IsFinite(remaining)
                    || remaining is < 0 or > 100 || window.ResetsAt is not { } reset) continue;
                if (!account.Windows.TryGetValue(window.Key, out var state))
                {
                    if (reset <= now) continue;
                    state = new() { Label = window.Label, AcceptedResetAt = reset, MetadataResetAt = reset,
                        LastRemaining = remaining, LastObservedAt = now };
                    account.Windows.Add(window.Key, state);
                    DateTimeOffset start;
                    try { start = reset.AddMinutes(-window.WindowMinutes.Value); }
                    catch (ArgumentOutOfRangeException) { start = now; }
                    if (start > now) start = now;
                    state.Periods.Add(new UsagePeriod { Id = Guid.NewGuid().ToString("N"), WindowKey = window.Key,
                        AccountKey = accountKey, Label = window.Label, StartedAt = start, EndsAt = reset,
                        IsEstimated = true, Reason = "Initial", ObservedAt = now });
                    accepted++;
                    continue;
                }
                if (now <= state.LastObservedAt) continue;
                state.Label = window.Label;
                if (state.ManualMerge is { } manual)
                {
                    int manualIndex = state.Periods.FindIndex(p => p.Id == manual.PeriodId);
                    if (manualIndex >= 0 && now >= state.Periods[manualIndex].StartedAt && reset > now)
                    {
                        // The user supplied the boundary before this window was polled. This first
                        // post-boundary reading updates its endpoint and baseline, never its start.
                        state.Periods[manualIndex] = state.Periods[manualIndex] with { EndsAt = reset };
                        state.AcceptedResetAt = state.MetadataResetAt = reset;
                        state.MetadataCorrected = false;
                        state.LastRemaining = remaining;
                        state.LastObservedAt = now;
                        state.ManualMerge = null;
                        accepted++;
                        continue;
                    }
                    if (manualIndex >= 0)
                    {
                        manual.RecheckAt = now + RecheckDelay;
                        continue;
                    }
                    state.ManualMerge = null;
                }
                if (state.Pending is { } pending)
                {
                    if (now < pending.RecheckAt) continue;
                    int index = state.Periods.FindIndex(p => p.Id == pending.PeriodId);
                    bool manuallyConfirmed = index >= 0 && state.Periods[index].IsCorrected;
                    bool sustained = remaining > pending.PreviousRemaining + .000001
                        || reset > pending.PreviousResetAt + DriftTolerance;
                    if (manuallyConfirmed && (reset <= now || now < state.Periods[index].StartedAt))
                    {
                        pending.RecheckAt = now + RecheckDelay;
                        continue;
                    }
                    if ((sustained || manuallyConfirmed) && reset > now)
                    {
                        if (index >= 0) state.Periods[index] = state.Periods[index] with
                        { IsPending = false, EndsAt = reset, Reason = cardDecrease ? "Card" : state.Periods[index].Reason };
                        state.Pending = null;
                        state.AcceptedResetAt = reset;
                        state.MetadataResetAt = reset;
                        state.MetadataCorrected = false;
                        state.LastRemaining = remaining;
                        state.LastObservedAt = now;
                        accepted++;
                        continue;
                    }
                    // A brief correction must not leave a false billable period behind.
                    RemovePending(state);
                }
                bool refill = remaining > state.LastRemaining + .000001;
                bool natural = state.AcceptedResetAt <= now && reset > now
                    && reset > state.AcceptedResetAt + DriftTolerance
                    && (!state.MetadataCorrected || Math.Abs((reset - state.MetadataResetAt).TotalSeconds) > 120);
                if (natural && !(refill && cardDecrease))
                {
                    DateTimeOffset start = state.AcceptedResetAt;
                    DateTimeOffset inferred;
                    try { inferred = reset.AddMinutes(-window.WindowMinutes.Value); }
                    catch (ArgumentOutOfRangeException) { inferred = start; }
                    bool skipped = inferred > start + DriftTolerance && inferred <= now;
                    if (skipped) start = inferred;
                    AddBoundary(state, new UsagePeriod { Id = Guid.NewGuid().ToString("N"), WindowKey = window.Key,
                        AccountKey = accountKey, Label = window.Label, StartedAt = start, EndsAt = reset,
                        Reason = "Natural", IsEstimated = skipped, ResetEventId = batchEvent, ObservedAt = now });
                    state.AcceptedResetAt = reset;
                    state.MetadataCorrected = false;
                }
                else if (refill && reset > now)
                {
                    string reason = cardDecrease ? "Card" : "Recovery";
                    var period = new UsagePeriod { Id = Guid.NewGuid().ToString("N"), WindowKey = window.Key,
                        AccountKey = accountKey, Label = window.Label, StartedAt = now, EndsAt = reset,
                        Reason = reason, IsEstimated = true, IsPending = true,
                        BoundaryEarliestAt = state.LastObservedAt, ResetEventId = batchEvent, ObservedAt = now };
                    var oldEnd = state.Periods.LastOrDefault()?.EndsAt;
                    if (!AddBoundary(state, period))
                    {
                        state.LastRemaining = remaining;
                        state.LastObservedAt = now;
                        accepted++;
                        continue;
                    }
                    state.Pending = new PendingBoundary { PeriodId = period.Id, RecheckAt = now + RecheckDelay,
                        PreviousRemaining = state.LastRemaining, PreviousResetAt = state.AcceptedResetAt,
                        PreviousEnd = oldEnd,
                        FirstRemaining = remaining, FirstResetAt = reset, FirstObservedAt = now };
                    accepted++;
                    continue;
                }
                else if (Math.Abs((reset - state.AcceptedResetAt).TotalSeconds) > 120)
                {
                    // A metadata correction alone cannot establish a reset. Never extend the old guard.
                    state.AcceptedResetAt = reset < state.AcceptedResetAt ? reset : state.AcceptedResetAt;
                    state.MetadataCorrected = true;
                }
                state.MetadataResetAt = reset;
                state.LastRemaining = remaining;
                state.LastObservedAt = now;
                accepted++;
            }
            if (healthyCards)
            {
                account.CreditCount = cards.AvailableCount;
                account.CreditsObservedAt = now;
                // Card detail rows can be omitted or capped. Retain known unexpired IDs so that
                // their later expiry is not misclassified when an intervening sample omits rows.
                account.Credits = account.Credits.Where(c => c.ExpiresAt == null || c.ExpiresAt > now)
                    .Concat(cards.Credits.Where(c => !string.IsNullOrWhiteSpace(c.Id))
                        .Select(c => new CreditExpiry { Id = c.Id, ExpiresAt = c.ExpiresAt }))
                    .GroupBy(c => c.Id, StringComparer.Ordinal).Select(group => group.Last()).TakeLast(4096).ToList();
            }
            if (account.Windows.Values.Any(w => w.Pending?.RecheckAt <= now || w.ManualMerge?.RecheckAt <= now))
                _retryNotBefore = DateTimeOffset.UtcNow + RecheckDelay;
            account.LastObservedAt = now;
            _detail = accepted == 0 ? "未取得完整的额度周期字段，保留已有周期。"
                : account.Windows.Values.Any(w => w.Pending != null)
                    ? "检测到额度恢复，已按首次发现时间分段；5 秒后复核，起点可在报告中校正。"
                    : "周期来自本机额度观测；重置卡与额度恢复时间为轮询估计，可在报告中校正。";
            Save();
        }
    }

    public IReadOnlyList<UsagePeriod> GetPeriods(string? windowKey)
    {
        lock (_gate)
        {
            var account = CurrentAccount();
            if (account == null) return [];
            return account.Windows.Where(pair => windowKey == null || pair.Key == windowKey)
                .SelectMany(pair => pair.Value.Periods.Select((period, index) => period with
                    { IsCurrent = index == pair.Value.Periods.Count - 1 }))
                .OrderByDescending(p => p.StartedAt).ToArray();
        }
    }

    public bool AddManualReset(string windowKey, DateTimeOffset at, out string? error)
    {
        lock (_gate)
        {
            error = null;
            if (CurrentAccount() is not { } account || !account.Windows.TryGetValue(windowKey, out var window))
            { error = "请先读取该账户的完整额度周期。"; return false; }
            if (window.Pending != null)
            { error = "请等待当前额度恢复复核，或直接校正待确认周期的起点。"; return false; }
            if (at > DateTimeOffset.UtcNow)
            { error = "重置时间不能晚于当前时间。"; return false; }
            int index = window.Periods.FindLastIndex(p => p.StartedAt < at && (p.EndsAt == null || at < p.EndsAt));
            if (index < 0 || window.Periods.Any(p => p.StartedAt == at))
            { error = "请选择已有周期内部的时间，不能与已有起点重合。"; return false; }
            var previous = window.Periods[index];
            var created = previous with { Id = Guid.NewGuid().ToString("N"), StartedAt = at, Reason = "Manual",
                IsEstimated = false, IsPending = false, IsCorrected = true, BoundaryEarliestAt = at,
                ResetEventId = account.Windows.Values.SelectMany(w => w.Periods)
                    .FirstOrDefault(p => p.Reason == "Manual" && p.StartedAt == at)?.ResetEventId
                    ?? Guid.NewGuid().ToString("N"), ObservedAt = DateTimeOffset.UtcNow };
            window.Periods[index] = previous with { EndsAt = at };
            window.Periods.Insert(index + 1, created);
            if (window.Periods[^1].StartedAt > window.LastObservedAt)
                window.ManualMerge = new ManualBoundary { PeriodId = window.Periods[^1].Id, RecheckAt = DateTimeOffset.UtcNow };
            _detail = "已补记重置时间，报告将按新的周期边界重新汇总。";
            Save();
            return true;
        }
    }

    public bool CorrectStart(string periodId, DateTimeOffset at, out string? error)
    {
        lock (_gate)
        {
            error = null;
            var account = CurrentAccount();
            var window = account?.Windows.Values.FirstOrDefault(w => w.Periods.Any(p => p.Id == periodId));
            if (window == null)
            { error = "未找到当前账户中的周期。"; return false; }
            int index = window.Periods.FindIndex(p => p.Id == periodId);
            var period = window.Periods[index];
            if (at > DateTimeOffset.UtcNow || period.EndsAt is { } end && at >= end
                || index > 0 && at <= window.Periods[index - 1].StartedAt)
            { error = "起点必须早于周期结束及当前时间，并晚于上一个周期起点。"; return false; }
            window.Periods[index] = period with { StartedAt = at, IsEstimated = false, IsPending = false,
                IsCorrected = true, BoundaryEarliestAt = at };
            if (index > 0) window.Periods[index - 1] = window.Periods[index - 1] with { EndsAt = at };
            // Keep an automatic candidate's read-only verification alive after an explicit
            // correction: it still needs the final endpoint, but can no longer roll back the
            // user-confirmed boundary. Its next healthy sample refreshes only endpoint/baseline.
            _detail = "周期起点已校正，后续轮询保留此次校正。";
            Save();
            return true;
        }
    }

    private AccountState? CurrentAccount() => _state.CurrentAccount is { } key
        && _state.Accounts.TryGetValue(key, out var account) ? account : null;

    private static bool AddBoundary(WindowState window, UsagePeriod period)
    {
        if (window.Periods.LastOrDefault() is { } previous)
        {
            if (period.StartedAt <= previous.StartedAt) return false;
            window.Periods[^1] = previous with { EndsAt = previous.EndsAt is { } oldEnd && oldEnd < period.StartedAt ? oldEnd : period.StartedAt };
        }
        window.Periods.Add(period);
        return true;
    }

    private static void RemovePending(WindowState window)
    {
        if (window.Pending is not { } pending) return;
        int index = window.Periods.FindIndex(p => p.Id == pending.PeriodId);
        if (index >= 0)
        {
            window.Periods.RemoveAt(index);
            if (index > 0) window.Periods[index - 1] = window.Periods[index - 1] with { EndsAt = pending.PreviousEnd };
        }
        window.Pending = null;
    }

    private void Restore()
    {
        if (_checkpointPath == null || !File.Exists(_checkpointPath)) return;
        try
        {
            if (new FileInfo(_checkpointPath).Length > 16 * 1024 * 1024) throw new JsonException();
            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(_checkpointPath));
            if (checkpoint == null || checkpoint.Version != 1 || checkpoint.Accounts == null
                || checkpoint.Accounts.Any(a => a.Value == null || a.Value.Windows == null
                    || a.Value.Credits == null || a.Value.Credits.Any(c => c == null) || a.Value.Windows.Any(w => w.Value == null
                        || w.Value.Periods == null || w.Value.Periods.Count == 0 || w.Value.Periods.Any(p => p == null
                            || p.AccountKey != a.Key || p.WindowKey != w.Key || string.IsNullOrEmpty(p.Id)
                            || p.EndsAt == null || p.EndsAt <= p.StartedAt)
                        || w.Value.Pending != null && !w.Value.Periods.Any(p => p.Id == w.Value.Pending.PeriodId && (p.IsPending || p.IsCorrected))
                        || w.Value.ManualMerge != null && !w.Value.Periods.Any(p => p.Id == w.Value.ManualMerge.PeriodId && p.Reason == "Manual")
                        || w.Value.ManualMerge != null && w.Value.Periods[^1].Id != w.Value.ManualMerge.PeriodId
                        || w.Value.Periods.Zip(w.Value.Periods.Skip(1)).Any(pair =>
                            pair.First.StartedAt >= pair.Second.StartedAt || pair.First.EndsAt > pair.Second.StartedAt))))
                throw new JsonException();
            _state = checkpoint;
            _detail = "已恢复周期记录，等待新的账户额度观测。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { _detail = "周期恢复文件不可用；将从新的额度观测建立记录。"; }
    }

    private void Save()
    {
        if (_checkpointPath == null) return;
        try
        {
            string target = Path.GetFullPath(_checkpointPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
            File.Move(temporary, target, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { _detail = "周期已更新，但保存失败；重启后可能无法恢复此次边界。"; }
    }

    private sealed class Checkpoint
    {
        public int Version { get; set; } = 1;
        public string? CurrentAccount { get; set; }
        public Dictionary<string, AccountState> Accounts { get; set; } = new(StringComparer.Ordinal);
    }
    private sealed class AccountState
    {
        public DateTimeOffset LastObservedAt { get; set; }
        public int? CreditCount { get; set; }
        public DateTimeOffset? CreditsObservedAt { get; set; }
        public List<CreditExpiry> Credits { get; set; } = [];
        public Dictionary<string, WindowState> Windows { get; set; } = new(StringComparer.Ordinal);
    }
    private sealed class CreditExpiry
    {
        public string Id { get; set; } = "";
        public DateTimeOffset? ExpiresAt { get; set; }
    }
    private sealed class WindowState
    {
        public string Label { get; set; } = "";
        public DateTimeOffset AcceptedResetAt { get; set; }
        public DateTimeOffset MetadataResetAt { get; set; }
        public bool MetadataCorrected { get; set; }
        public double LastRemaining { get; set; }
        public DateTimeOffset LastObservedAt { get; set; }
        public List<UsagePeriod> Periods { get; set; } = [];
        public PendingBoundary? Pending { get; set; }
        public ManualBoundary? ManualMerge { get; set; }
    }
    private sealed class ManualBoundary
    {
        public string PeriodId { get; set; } = "";
        public DateTimeOffset RecheckAt { get; set; }
    }
    private sealed class PendingBoundary
    {
        public string PeriodId { get; set; } = "";
        public DateTimeOffset RecheckAt { get; set; }
        public double PreviousRemaining { get; set; }
        public DateTimeOffset PreviousResetAt { get; set; }
        public DateTimeOffset? PreviousEnd { get; set; }
        public double FirstRemaining { get; set; }
        public DateTimeOffset FirstResetAt { get; set; }
        public DateTimeOffset FirstObservedAt { get; set; }
    }
}
