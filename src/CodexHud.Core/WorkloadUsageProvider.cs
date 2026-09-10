using CodexHud.Core.Native;

namespace CodexHud.Core;

/// <summary>Observed arrivals in one completed second. Cache hits are a subset of input.</summary>
public sealed record UsageTick(DateTimeOffset TickStart, TokenCounts? Counts, SampleHealth Health);
public sealed record TaskUsageSeries(string ThreadId, string Title, bool IsSubagent, bool IsArchived,
    IReadOnlyList<UsageTick> History);
public sealed record WorkloadUsageSnapshot(DateTimeOffset ObservedAt, IReadOnlyList<UsageTick> OverallHistory,
    IReadOnlyList<TaskUsageSeries> Tasks, SampleHealth Health, string? Detail = null)
{
    public int IndexedThreads { get; init; }
    public int FilesChecked { get; init; }
    public int FilesRead { get; init; }
    public int ReadBytes { get; init; }
    public int BacklogFiles { get; init; }
    public bool IndexDiscoveryPerformed { get; init; }
}

/// <summary>
/// A separate workload observer: never shares consuming cursors, model filters, or epochs with
/// quota calibration. Only numeric response metadata is retained. History is arrival time, not
/// an estimate of when the model generated its tokens, and is never persisted.
/// </summary>
public sealed class WorkloadUsageProvider
{
    internal const int HistorySeconds = UsageSmoothing.RetainedSeconds;
    private static readonly TokenCounts Zero = new(0, 0, 0, 0, 0, 0);
    private readonly string _home;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _readBudget;
    private readonly int _maximumThreads;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<Frame> _history = new();
    private readonly DateTimeOffset _started;
    private Frame _open;
    private DateTimeOffset? _lastRead;
    private DateTimeOffset _nextDiscovery = DateTimeOffset.MinValue;
    private bool _discovered;
    private bool _indexComplete;
    private bool _indexAvailable;
    private int _roundRobin;

    public WorkloadUsageProvider(string codexHome) : this(codexHome, () => DateTimeOffset.UtcNow) { }

    internal WorkloadUsageProvider(string codexHome, Func<DateTimeOffset> clock,
        int readBudget = 4 * 1024 * 1024, int maximumThreads = 4096)
    {
        _home = CodexLocator.ResolveHome(Normalize(codexHome));
        _clock = clock;
        _readBudget = Math.Max(1, readBudget);
        _maximumThreads = Math.Max(1, maximumThreads);
        _started = clock();
        _open = new(Tick(_started)) { Unknown = true };
    }

    public async Task<WorkloadUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Read(cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private WorkloadUsageSnapshot Read(CancellationToken cancellationToken)
    {
        var now = _clock();
        bool clockGap = _lastRead is { } before && (now < before || now - before > TimeSpan.FromMilliseconds(1750));
        if (clockGap) _open.Unknown = true;
        Advance(now, clockGap);
        int budget = _readBudget, checkedFiles = 0, readFiles = 0, backlog = 0;
        bool discovery = now >= _nextDiscovery || !_discovered;
        try
        {
            if (discovery)
            {
                _nextDiscovery = now.AddSeconds(5);
                try { Discover(now); }
                catch (Exception ex) when (SourceException(ex))
                {
                    _indexAvailable = false;
                    _indexComplete = false;
                }
            }
            if (!_indexAvailable || !_indexComplete) _open.Unknown = true;
            var entries = _entries.Values.Where(entry => entry.Indexed).ToArray();
            int start = entries.Length == 0 ? 0 : _roundRobin % entries.Length;
            int nextStart = start;
            bool exhausted = false;
            for (int index = 0; index < entries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int position = (start + index) % entries.Length;
                var entry = entries[position];
                checkedFiles++;
                try
                {
                    var info = new FileInfo(entry.Path);
                    info.Refresh();
                    if (!info.Exists)
                    {
                        if (!entry.HistoricalUnavailable) Gap(entry);
                        continue;
                    }
                    if (entry.HistoricalUnavailable)
                    {
                        entry.HistoricalUnavailable = false;
                        entry.InitialBaseline = true;
                        Gap(entry);
                    }
                    long length = info.Length;
                    long writeTime = info.LastWriteTimeUtc.Ticks;
                    bool changed = !entry.Initialized || entry.Pending || entry.Length != length
                        || entry.WriteTime != writeTime || now >= entry.NextAudit;
                    if (!changed) continue;
                    if (budget <= 0)
                    {
                        if (!exhausted) { nextStart = position; exhausted = true; }
                        entry.Pending = true;
                        backlog++;
                        Gap(entry);
                        continue;
                    }
                    readFiles++;
                    if (!entry.Initialized)
                    {
                        var initial = entry.Cursor!.Initialize(entry.Path, _started, entry.InitialBaseline, ref budget);
                        if (initial != LedgerReadState.Complete) Gap(entry);
                        entry.Initialized = initial != LedgerReadState.Incomplete;
                        if (!entry.Initialized) { entry.Pending = true; backlog++; continue; }
                    }
                    var result = entry.Cursor!.Read(entry.Path, _started, now, ref budget);
                    var arrived = _clock();
                    Advance(arrived, false);
                    if (result.State != LedgerReadState.Complete) Gap(entry);
                    entry.Pending = result.State == LedgerReadState.Incomplete;
                    if (entry.Pending) backlog++;
                    entry.Length = length;
                    entry.WriteTime = writeTime;
                    entry.NextAudit = now.AddSeconds(5);
                    foreach (var increment in result.Increments)
                    {
                        // There is deliberately no quota-pool/model/provider filter here.
                        try { _open.Add(entry.Id, increment.Counts); }
                        catch (OverflowException) { Gap(entry); }
                    }
                }
                catch (Exception ex) when (SourceException(ex))
                {
                    entry.Pending = true;
                    backlog++;
                    Gap(entry);
                }
            }
            _roundRobin = entries.Length == 0 ? 0 : exhausted ? nextStart : (start + 1) % entries.Length;
        }
        catch (OperationCanceledException)
        {
            _open.Unknown = true;
            throw;
        }
        now = _clock();
        Advance(now, false);
        _lastRead = now;
        return Snapshot(now) with
        {
            FilesChecked = checkedFiles, FilesRead = readFiles, ReadBytes = _readBudget - budget,
            BacklogFiles = backlog, IndexDiscoveryPerformed = discovery
        };
    }

    private void Discover(DateTimeOffset now)
    {
        string? database = Directory.EnumerateFiles(_home, "state_*.sqlite")
            .Select(path => (Path: path, Version: int.TryParse(Path.GetFileNameWithoutExtension(path)[6..], out int v) ? v : -1))
            .Where(item => item.Version >= 0).OrderByDescending(item => item.Version).FirstOrDefault().Path;
        if (database is null) throw new IOException("Local workload index unavailable.");
        using var state = new SqliteReader(database);
        var columns = state.Columns("threads");
        if (!columns.Contains("id") || !columns.Contains("rollout_path")) throw new IOException("Unsupported workload index.");
        string Column(string name) => columns.Contains(name) ? name : "NULL AS " + name;
        string updated = columns.Contains("updated_at_ms") ? "updated_at_ms" : columns.Contains("updated_at") ? "updated_at" : "id";
        string filter = columns.Contains("thread_source") ? " WHERE thread_source IS NULL OR thread_source <> 'guardian_review'" : "";
        var rows = state.Query($"SELECT id, rollout_path, {updated} AS workload_updated, {Column("title")}, {Column("name")}, {Column("source")}, "
            + $"{Column("thread_source")}, {Column("archived")}, {Column("model")}, {Column("reasoning_effort")}, {Column("model_provider")} "
            + $"FROM threads{filter} ORDER BY {updated} DESC,id LIMIT {_maximumThreads + 1}");
        bool first = !_discovered;
        foreach (var entry in _entries.Values) entry.Indexed = false;
        _indexComplete = rows.Count <= _maximumThreads;
        foreach (var row in rows.Take(_maximumThreads))
        {
            string id = row.Text("id");
            if (id.Length is 0 or > 256) { _indexComplete = false; continue; }
            string? path = LocalPath(row.Text("rollout_path"));
            // Foreign-host paths are not part of a local workload monitor.
            if (path is null) continue;
            if (!_entries.TryGetValue(id, out var entry))
            {
                entry = new(id, path, Tick(now)) { InitialBaseline = first,
                    HistoricalUnavailable = first && !File.Exists(path) };
                _entries.Add(id, entry);
            }
            else if (!string.Equals(entry.Path, path, LogFileIdentity.PathComparison))
            {
                entry.Path = path;
                entry.Pending = true;
            }
            if (entry.Cursor is null)
            {
                entry.Cursor = new(id);
                entry.InitialBaseline = true;
                entry.RetiredAt = null;
                Gap(entry);
            }
            string stamp = path + "|" + row.Text("workload_updated");
            if (entry.HistoricalUnavailable && entry.IndexStamp is { } oldStamp && oldStamp != stamp)
                entry.HistoricalUnavailable = false;
            entry.IndexStamp = stamp;
            entry.Indexed = true;
            entry.Title = CleanTitle(string.IsNullOrWhiteSpace(row.Text("name")) ? row.Text("title") : row.Text("name"));
            entry.IsSubagent = row.Text("thread_source") == "subagent"
                || row.Text("source").Contains("subagent", StringComparison.OrdinalIgnoreCase);
            entry.IsArchived = row.Text("archived") == "1";
            entry.Cursor!.SetIndexProfile(row.Text("model"), row.Text("reasoning_effort"), row.Text("model_provider"));
        }
        // Keep recent row identity beside its numeric history, but release retired file cursors.
        foreach (var entry in _entries.Values.Where(entry => !entry.Indexed).ToArray())
        {
            entry.RetiredAt ??= Tick(now);
            entry.Cursor = null;
            entry.Initialized = false;
            entry.Pending = false;
            if (!_open.Values.ContainsKey(entry.Id) && !_history.Any(frame => frame.Values.ContainsKey(entry.Id)))
                _entries.Remove(entry.Id);
        }
        // A pathological replacement of the entire index every few seconds must remain bounded.
        foreach (var entry in _entries.Values.Where(entry => !entry.Indexed).OrderByDescending(entry => entry.RetiredAt)
            .Skip(_maximumThreads).ToArray())
        {
            _entries.Remove(entry.Id);
            _open.Unknown = true;
        }
        _discovered = true;
        _indexAvailable = true;
    }

    private WorkloadUsageSnapshot Snapshot(DateTimeOffset now)
    {
        var frames = _history.ToArray();
        var overall = frames.Select(frame => frame.Overall()).ToArray();
        var tasks = new TaskUsageSeries[_entries.Count];
        int index = 0;
        var idleHistories = new Dictionary<(DateTimeOffset FirstSeen, bool Missing), IReadOnlyList<UsageTick>>();
        foreach (var entry in _entries.Values)
        {
            // Share authoritative idle zero histories rather than inventing zeros in the UI.
            bool hasHistory = frames.Any(frame => frame.Values.ContainsKey(entry.Id));
            IReadOnlyList<UsageTick> history;
            if (hasHistory || entry.RetiredAt is not null)
                history = frames.Where(frame => frame.Start >= entry.FirstSeen).Select(frame =>
                    entry.RetiredAt is { } retired && frame.Start >= retired
                        ? new UsageTick(frame.Start, null, SampleHealth.Partial) : frame.ForTask(entry.Id)).ToArray();
            else
            {
                var key = (entry.FirstSeen, entry.HistoricalUnavailable);
                if (!idleHistories.TryGetValue(key, out history!))
                {
                    history = frames.Where(frame => frame.Start >= entry.FirstSeen).Select(frame =>
                        new UsageTick(frame.Start, frame.Unknown || entry.HistoricalUnavailable ? null : Zero,
                            entry.HistoricalUnavailable ? SampleHealth.Unavailable
                            : frame.Unknown ? SampleHealth.Partial : SampleHealth.Fresh)).ToArray();
                    idleHistories.Add(key, history);
                }
            }
            tasks[index++] = new(entry.Id, entry.Title, entry.IsSubagent, entry.IsArchived, history);
        }
        var health = !_indexAvailable ? SampleHealth.Unavailable : _open.Overall().Health;
        return new(now, overall, tasks, health,
            health == SampleHealth.Fresh ? "每秒新观察到的本机响应 Token；响应记录到达时入账，cache hit 已含在输入中"
            : "部分用量尚不可完整观察；缺口不是零，已收到的响应记录仍保留")
        { IndexedThreads = _entries.Values.Count(entry => entry.Indexed) };
    }

    private void Advance(DateTimeOffset now, bool missed)
    {
        var target = Tick(now);
        if (target < _open.Start) { _open.Unknown = true; return; }
        // Bound work after sleep/resume regardless of how long the process was suspended.
        if (target - _open.Start > TimeSpan.FromSeconds(HistorySeconds))
        {
            _history.Clear();
            _open = new(target.AddSeconds(-HistorySeconds)) { Unknown = true };
            missed = true;
        }
        while (_open.Start < target)
        {
            if (missed) _open.Unknown = true;
            _history.Enqueue(_open);
            while (_history.Count > HistorySeconds) _history.Dequeue();
            _open = new(_open.Start.AddSeconds(1));
            if (_open.Start < target) _open.Unknown = true;
        }
    }

    private void Gap(Entry entry) => _open.MarkGap(entry.Id);
    private static DateTimeOffset Tick(DateTimeOffset at) => DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());
    private static string Normalize(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;
    private string? LocalPath(string raw)
    {
        try
        {
            string normalized = Normalize(raw);
            if (!Path.IsPathFullyQualified(normalized)) return null;
            string path = Path.GetFullPath(normalized);
            return new[] { "sessions", "archived_sessions" }.Any(directory => path.StartsWith(
                Path.Combine(_home, directory) + Path.DirectorySeparatorChar, LogFileIdentity.PathComparison))
                && path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
    private static string CleanTitle(string title)
    {
        string clean = new(title.Where(character => !char.IsControl(character)).Take(160).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "未命名任务" : clean.Trim();
    }
    private static bool SourceException(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException;

    private sealed class Entry(string id, string path, DateTimeOffset firstSeen)
    {
        public string Id { get; } = id;
        public string Path { get; set; } = path;
        public string Title { get; set; } = "未命名任务";
        public DateTimeOffset FirstSeen { get; } = firstSeen;
        public bool IsSubagent, IsArchived, Indexed, Initialized, InitialBaseline, HistoricalUnavailable, Pending;
        public long Length = -1, WriteTime = -1;
        public DateTimeOffset NextAudit = DateTimeOffset.MinValue;
        public string? IndexStamp;
        public DateTimeOffset? RetiredAt;
        public UsageLedgerCursor? Cursor { get; set; } = new(id);
    }

    private sealed class Frame(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; } = start;
        public bool Unknown;
        public Dictionary<string, (TokenCounts Counts, bool Gap)> Values { get; } = new(StringComparer.Ordinal);
        public void Add(string id, TokenCounts counts)
        {
            var previous = Values.GetValueOrDefault(id, (Zero, false));
            Values[id] = (UsageLedgerProvider.Add(previous.Item1, counts), previous.Item2);
        }
        public void MarkGap(string id)
        {
            var previous = Values.GetValueOrDefault(id, (Zero, false));
            Values[id] = (previous.Item1, true);
        }
        public UsageTick ForTask(string id)
        {
            var value = Values.GetValueOrDefault(id, (Zero, false));
            bool partial = Unknown || value.Item2;
            return new(Start, partial && value.Item1.TotalTokens == 0 ? null : value.Item1,
                partial ? SampleHealth.Partial : SampleHealth.Fresh);
        }
        public UsageTick Overall()
        {
            TokenCounts counts = Zero;
            bool partial = Unknown;
            foreach (var value in Values.Values)
            {
                partial |= value.Gap;
                try { counts = UsageLedgerProvider.Add(counts, value.Counts); }
                catch (OverflowException) { return new(Start, null, SampleHealth.Partial); }
            }
            return new(Start, partial && counts.TotalTokens == 0 ? null : counts,
                partial ? SampleHealth.Partial : SampleHealth.Fresh);
        }
    }
}
