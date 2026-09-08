using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexHud.Core;

/// <summary>
/// Reads quota through an exclusively owned, hidden app-server connection. The only outbound
/// methods are initialize, initialized, account/read (refreshToken=false), and
/// account/rateLimits/read. No auth files are inspected; account metadata is only retained as a hash.
/// </summary>
public sealed class QuotaProvider : IQuotaProvider
{
    private readonly string? _codexExecutable;
    private readonly string _codexHome;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ServerConnection? _connection;
    private QuotaSnapshot? _lastSuccess;
    private string? _lastAccountKey;
    private int _ownedProcessId;
    private int _disposed;

    public QuotaProvider(string? codexExecutable = null, string? codexHome = null)
    {
        _codexExecutable = codexExecutable;
        _codexHome = CodexLocator.ResolveHome(codexHome);
    }

    public int? OwnedProcessId => Volatile.Read(ref _ownedProcessId) is var id && id > 0 ? id : null;

    public async Task<QuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var entered = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_connection is null || !_connection.IsAlive)
            {
                await StopServerAsync().ConfigureAwait(false);
                var executable = CodexLocator.ResolveExecutable(_codexExecutable);
                if (executable is null) return Failed("未找到 Codex 程序，请启动或安装 Codex 后重试。");
                _connection = ServerConnection.Start(executable, _codexHome);
                Volatile.Write(ref _ownedProcessId, _connection.ProcessId);
                await _connection.InitializeAsync(deadline.Token).ConfigureAwait(false);
            }
            var accountBefore = await ReadAccountKeyAsync(deadline.Token).ConfigureAwait(false);
            ObserveAccount(accountBefore);
            var result = await _connection.RequestAsync("account/rateLimits/read", null, deadline.Token).ConfigureAwait(false);
            var observedAt = DateTimeOffset.UtcNow;
            var accountAfter = await ReadAccountKeyAsync(deadline.Token).ConfigureAwait(false);
            ObserveAccount(accountAfter);
            var sample = Parse(result, observedAt) with
            {
                AccountKey = accountBefore is not null && accountBefore == accountAfter ? accountBefore : null
            };
            var accountSwitched = accountBefore is not null && accountAfter is not null && accountBefore != accountAfter;
            if (accountSwitched) sample = sample with { ResetCredits = ResetCreditSample.Missing };
            // A detected switch makes this in-flight read ambiguous; show its actual response,
            // but never retain it as a fallback for the account observed afterwards.
            _lastSuccess = accountSwitched
                ? null : sample;
            return sample;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            if (entered) await StopServerAsync().ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (entered) await StopServerAsync().ConfigureAwait(false);
            return Failed("额度查询超时，将自动重试。");
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception
            or InvalidOperationException or UnauthorizedAccessException or QuotaRpcException)
        {
            if (entered) await StopServerAsync().ConfigureAwait(false);
            return Failed(ex is QuotaRpcException { RequiresLogin: true }
                ? "请在 Codex 中确认登录，然后刷新额度。"
                : "额度暂不可用，请检查 Codex 登录和网络连接；将自动重试。");
        }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    private QuotaSnapshot Failed(string message) => _lastSuccess is { } previous
        ? previous with { Health = SampleHealth.Stale, Message = message, AccountKey = null,
            ResetCredits = previous.ResetCredits.AvailableCount.HasValue
                ? previous.ResetCredits with { Health = SampleHealth.Stale } : ResetCreditSample.Missing }
        : new(DateTimeOffset.UtcNow, Array.Empty<QuotaWindow>(), SampleHealth.Unavailable, message);

    private void ObserveAccount(string? accountKey)
    {
        if (accountKey is null) return;
        if (_lastAccountKey is not null && _lastAccountKey != accountKey) _lastSuccess = null;
        _lastAccountKey = accountKey;
    }

    private async Task<string?> ReadAccountKeyAsync(CancellationToken cancellationToken)
    {
        using var identityDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        identityDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var response = await _connection!.RequestAsync("account/read", new { refreshToken = false }, identityDeadline.Token).ConfigureAwait(false);
            return ParseAccountKey(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is QuotaRpcException or JsonException or InvalidOperationException or IOException) { return null; }
    }

    private static string? ParseAccountKey(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object) payload = result;
        if (!payload.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object) return null;
        var type = GetString(account, "type")?.Trim().ToLowerInvariant();
        if (type is not ("chatgpt" or "chatgptauthtokens")) return null;
        var stableId = GetString(account, "id") ?? GetString(account, "accountId") ?? GetString(account, "chatgptAccountId");
        var email = GetString(account, "email")?.Trim().ToLowerInvariant();
        if (stableId is null && email is null) return null;
        // Current local protocol exposes only type/email/planType. The fallback distinguishes
        // those identities, but cannot distinguish same-email workspaces without a workspace ID.
        // Never persist the metadata or include it in logs, errors, or quota messages.
        var fields = new[]
        {
            "codex-hud-account-v1", type, stableId is null ? "email" : "id", stableId ?? email,
            GetString(account, "planType")?.Trim().ToLowerInvariant(),
            GetString(account, "organizationId") ?? GetString(account, "orgId"),
            GetString(account, "workspaceId")
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fields);
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Accepts the account/rateLimits/read result, or a complete JSON-RPC response.</summary>
    public static QuotaSnapshot Parse(JsonElement payload, DateTimeOffset observedAt)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new JsonException("Quota response is not an object.");
        if (payload.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object) payload = result;
        var windows = new List<QuotaWindow>();
        if (payload.TryGetProperty("rateLimitsByLimitId", out var pools) && pools.ValueKind == JsonValueKind.Object)
        {
            foreach (var pool in pools.EnumerateObject()) AddPool(windows, pool.Value, pool.Name);
        }
        else if (payload.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            AddPool(windows, legacy, "codex");
        else if (!payload.TryGetProperty("rateLimits", out _) && !payload.TryGetProperty("rateLimitsByLimitId", out _))
            throw new JsonException("Quota fields are missing.");

        var ordered = windows.OrderBy(window => window.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(window => window.LimitId, StringComparer.Ordinal)
            .ThenBy(window => window.Slot == "primary" ? 0 : 1).ToArray();
        var available = ordered.Count(window => window.RemainingPercent.HasValue);
        var health = available == 0 ? SampleHealth.Unavailable
            : ordered.Any(window => !window.RemainingPercent.HasValue || !window.WindowMinutes.HasValue) ? SampleHealth.Partial : SampleHealth.Fresh;
        return new(observedAt, ordered, health,
            available == 0 ? "服务未返回可用额度，请确认 Codex 登录状态；缺失值显示为 —。"
            : health == SampleHealth.Partial ? "部分额度字段尚不可用。" : null)
        { ResetCredits = ParseResetCredits(payload, observedAt) };
    }

    private static ResetCreditSample ParseResetCredits(JsonElement payload, DateTimeOffset observedAt)
    {
        // This is an account-level reset inventory, not the paid balance in rateLimits.credits.
        // Detail rows may be omitted or capped: only availableCount is authoritative.
        if (payload.TryGetProperty("rateLimitResetCredits", out var resets) && resets.ValueKind == JsonValueKind.Object
            && resets.TryGetProperty("availableCount", out var count) && count.ValueKind == JsonValueKind.Number
            && count.TryGetInt32(out int available) && available >= 0)
            return new(available, observedAt, SampleHealth.Fresh);
        return ResetCreditSample.Missing;
    }

    private static void AddPool(List<QuotaWindow> windows, JsonElement pool, string fallbackId)
    {
        if (pool.ValueKind != JsonValueKind.Object) return;
        var limitId = GetString(pool, "limitId") ?? fallbackId;
        var name = GetString(pool, "limitName") ?? (limitId.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : limitId);
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (!pool.TryGetProperty(slot, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            double? remaining = null;
            if (window.TryGetProperty("usedPercent", out var usage) && usage.ValueKind == JsonValueKind.Number
                && usage.TryGetDouble(out var used) && double.IsFinite(used))
                remaining = Math.Clamp(100 - used, 0, 100);
            int? minutes = window.TryGetProperty("windowDurationMins", out var duration)
                && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var number) && number > 0 ? number : null;
            DateTimeOffset? resetsAt = null;
            if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var timestamp))
            {
                try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
                catch (ArgumentOutOfRangeException) { }
            }
            var key = $"{Uri.EscapeDataString(limitId)}/{slot}/{(minutes.HasValue ? minutes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown")}";
            var label = $"{name} · {FormatPeriod(minutes, slot)}";
            windows.Add(new(key, limitId, label, slot, minutes, remaining, resetsAt));
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    public static string FormatPeriod(int? minutes, string slot) => minutes switch
    {
        null => slot == "primary" ? "主周期（时长未知）" : "次周期（时长未知）",
        var m when m % 1440 == 0 => $"{m / 1440} 天",
        var m when m % 60 == 0 => $"{m / 60} 小时",
        var m => $"{m} 分钟"
    };

    private async ValueTask StopServerAsync()
    {
        var connection = _connection;
        _connection = null;
        // Keep the owned PID visible until its process is actually terminated.
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        Volatile.Write(ref _ownedProcessId, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopServerAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private sealed class QuotaRpcException(bool requiresLogin) : Exception("Quota request was rejected.")
    {
        public bool RequiresLogin { get; } = requiresLogin;
    }

    private sealed class ServerConnection : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly SafeFileHandle? _job;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Task _stdout;
        private readonly Task _stderr;
        private long _nextId;
        private int _disposed;
        public int ProcessId { get; }
        public bool IsAlive => Volatile.Read(ref _disposed) == 0 && !_stdout.IsCompleted && !_process.HasExited;

        private ServerConnection(Process process)
        {
            _process = process;
            ProcessId = process.Id;
            _job = ChildProcessJob.TryCreate(process);
            _stdout = ReadResponsesAsync();
            _stderr = DrainErrorsAsync();
        }

        public static ServerConnection Start(string executable, string codexHome)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("--listen");
            start.ArgumentList.Add("stdio://");
            start.Environment["CODEX_HOME"] = codexHome;
            var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new IOException("Unable to start quota service.");
                return new ServerConnection(process);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
                throw;
            }
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await RequestAsync("initialize", new { clientInfo = new { name = "codex_energy_hud", title = "Codex Energy HUD", version = "1.0.0" } }, cancellationToken).ConfigureAwait(false);
            await SendAsync(new { method = "initialized", @params = new { } }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            if (method is not ("initialize" or "account/read" or "account/rateLimits/read"))
                throw new InvalidOperationException("The quota connection accepts read-only methods only.");
            if (method == "account/read")
            {
                var identityParameters = JsonSerializer.SerializeToElement(parameters);
                if (identityParameters.ValueKind != JsonValueKind.Object || identityParameters.EnumerateObject().Count() != 1
                    || !identityParameters.TryGetProperty("refreshToken", out var refresh) || refresh.ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException("Account identity reads must not refresh credentials.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var id = Interlocked.Increment(ref _nextId);
            var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = response;
            try
            {
                if (parameters is null) await SendAsync(new { id, method }, cancellationToken).ConfigureAwait(false);
                else await SendAsync(new { id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
                return await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _pending.TryRemove(id, out _); }
        }

        private async Task SendAsync(object value, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var message = JsonSerializer.Serialize(value);
                await _process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
        }

        private async Task ReadResponsesAsync()
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.Length > 4 * 1024 * 1024) throw new IOException("Quota response exceeds the size limit.");
                    using var document = JsonDocument.Parse(line);
                    var message = document.RootElement;
                    if (message.ValueKind != JsonValueKind.Object) continue;
                    if (!message.TryGetProperty("id", out var id)) continue;
                    // Decline any server-initiated request. This client cannot supply credentials,
                    // approve tools, or act on other app-server methods.
                    if (message.TryGetProperty("method", out _))
                    {
                        await SendAsync(new { id = id.Clone(), error = new { code = -32601, message = "Read-only quota client does not support server requests." } }, _stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var responseId)
                        || !_pending.TryGetValue(responseId, out var response)) continue;
                    if (message.TryGetProperty("error", out var error))
                    {
                        var text = GetString(error, "message") ?? "";
                        var needsLogin = text.Contains("auth", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("login", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("401", StringComparison.Ordinal);
                        response.TrySetException(new QuotaRpcException(needsLogin));
                    }
                    else if (message.TryGetProperty("result", out var result)) response.TrySetResult(result.Clone());
                    else response.TrySetException(new IOException("Quota response is incomplete."));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                foreach (var response in _pending.Values) response.TrySetException(new IOException("Quota service disconnected."));
            }
        }

        private async Task DrainErrorsAsync()
        {
            // Drain independently to prevent pipe deadlock. Discard content: diagnostics can contain
            // account information, so raw server output is never saved or shown in the widget.
            var buffer = new char[4096];
            try
            {
                while (await _process.StandardError.ReadAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false) != 0) { }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            try { _process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            _job?.Dispose(); // KILL_ON_JOB_CLOSE also terminates children on abrupt widget exit.
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            try { await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or InvalidOperationException) { }
            _process.Dispose();
        }
    }

    private static class ChildProcessJob
    {
        public static SafeFileHandle? TryCreate(Process process)
        {
            if (!OperatingSystem.IsWindows()) return null;
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) { job.Dispose(); return null; }
            var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>())
                || !AssignProcessToJobObject(job, process.Handle))
            {
                job.Dispose();
                return null;
            }
            return job;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW", SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}

/// <summary>Selection is sticky: a removed selection is unresolved, never silently replaced.</summary>
public static class QuotaSelection
{
    public static QuotaWindow? GetDefault(IReadOnlyList<QuotaWindow> windows) =>
        windows.FirstOrDefault(window => window.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) && window.RemainingPercent.HasValue && window.WindowMinutes.HasValue)
        ?? windows.FirstOrDefault(window => window.RemainingPercent.HasValue && window.WindowMinutes.HasValue);

    public static QuotaWindow? ResolveSelected(IReadOnlyList<QuotaWindow> windows, string? selectedKey) =>
        string.IsNullOrWhiteSpace(selectedKey) ? GetDefault(windows) : windows.FirstOrDefault(window => window.Key == selectedKey);
}
