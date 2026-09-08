using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodexHud.Core;

/// <summary>The test executable doubles as a deterministic, credential-free app-server fixture.</summary>
public static class QuotaTransportTests
{
    public static int RunFixture()
    {
        var initialized = false;
        var reads = 0;
        var identityReads = 0;
        var scenario = Path.GetFileName(Environment.GetEnvironmentVariable("CODEX_HOME") ?? "");
        var hang = scenario == "quota-fixture-hang";
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            var root = input.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "initialized") { initialized = true; continue; }
            var id = root.GetProperty("id").GetInt64();
            if (hang) continue;
            if (method == "initialize") Write(new { id, result = new { userAgent = "quota-test-fixture" } });
            else if (method == "account/read" && initialized)
            {
                // Treat any attempt to refresh credentials or add another parameter as a failure.
                if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object
                    || parameters.EnumerateObject().Count() != 1 || !parameters.TryGetProperty("refreshToken", out var refresh)
                    || refresh.ValueKind != JsonValueKind.False) return 74;
                identityReads++;
                if (scenario == "quota-fixture-identity-hang") continue;
                if (scenario == "quota-fixture-identity-unavailable")
                    Write(new { id, error = new { code = -32601, message = "Identity method unsupported fixture" } });
                else if (scenario == "quota-fixture-identity-null")
                    Write(new { id, result = new { account = (object?)null, requiresOpenaiAuth = true } });
                else if (scenario == "quota-fixture-identity-no-email")
                    Write(new { id, result = new { account = new { type = "chatgpt", email = (string?)null, planType = "pro" } } });
                else
                {
                    var changed = (scenario is "quota-fixture-account-change" or "quota-fixture-account-change-failure"
                        or "quota-fixture-stable-id") && identityReads > 2
                        || (scenario is "quota-fixture-account-race" or "quota-fixture-account-race-failure") && identityReads > 1;
                    var email = changed ? "second@example.invalid" : "first@example.invalid";
                    if (scenario == "quota-fixture-stable-id")
                        Write(new { id, result = new { account = new { type = "chatgpt", id = "stable-fixture-account", email, planType = "pro" } } });
                    else Write(new { id, result = new { account = new { type = "chatgpt", email, planType = "pro" } } });
                }
            }
            else if (method == "account/rateLimits/read" && initialized)
            {
                reads++;
                var alwaysSucceeds = scenario is "quota-fixture-account-change" or "quota-fixture-account-race"
                    or "quota-fixture-stable-id" or "quota-fixture-identity-unavailable" or "quota-fixture-identity-null"
                    or "quota-fixture-identity-no-email" or "quota-fixture-identity-hang";
                if (reads == 1 || alwaysSucceeds)
                {
                    // More than a pipe buffer: coupled stderr/stdout consumption deadlocks here.
                    Console.Error.Write(new string('x', 131072));
                    Console.Error.Flush();
                    Write(new { id, result = new { rateLimits = new { primary = new { usedPercent = reads == 1 ? 12.5 : 25.0, windowDurationMins = 300 } } } });
                }
                else Write(new { id, error = new { code = -1, message = "Unauthorized login required fixture" } });
            }
            else return 73; // Fail on unexpected methods or incorrect handshake order.
        }
        return 0;

        static void Write(object message) { Console.WriteLine(JsonSerializer.Serialize(message)); Console.Out.Flush(); }
    }

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        var executable = Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly()!.Location) + ".exe");
        await Check("handshake, stderr drain, stale preservation, restart and disposal", async () =>
        {
            int? pid;
            await using (var provider = new QuotaProvider(executable))
            {
                var first = await provider.ReadAsync();
                pid = provider.OwnedProcessId;
                Require(first.Health == SampleHealth.Fresh && first.Windows.FirstOrDefault()?.RemainingPercent == 87.5);
                Require(first.AccountKey is { Length: 64 } && first.AccountKey.All(Uri.IsHexDigit));
                Require(pid.HasValue && Alive(pid.Value));
                var failed = await provider.ReadAsync();
                Require(failed.Health == SampleHealth.Stale && failed.ObservedAt == first.ObservedAt);
                Require(failed.Windows[0].RemainingPercent == 87.5 && provider.OwnedProcessId is null);
                Require(failed.AccountKey is null);
                Require(failed.Message!.Contains("登录") && !failed.Message.Contains("Unauthorized"));
                Require(pid is { } previousPid && !Alive(previousPid));
                var recovered = await provider.ReadAsync();
                Require(recovered.Health == SampleHealth.Fresh && recovered.ObservedAt >= first.ObservedAt);
                Require(recovered.AccountKey == first.AccountKey);
                pid = provider.OwnedProcessId;
            }
            Require(pid.HasValue && !Alive(pid.Value));
        });
        await Check("account change isolates fresh quota samples", async () =>
        {
            await using var provider = Fixture("account-change");
            var first = await provider.ReadAsync();
            var changed = await provider.ReadAsync();
            Require(first.AccountKey is not null && changed.AccountKey is not null && first.AccountKey != changed.AccountKey);
            Require(changed.Health == SampleHealth.Fresh && changed.Windows[0].RemainingPercent == 75);
        });
        await Check("account change does not reuse previous account's quota after failure", async () =>
        {
            await using var provider = Fixture("account-change-failure");
            var first = await provider.ReadAsync();
            Require(first.AccountKey is not null);
            var changed = await provider.ReadAsync();
            Require(changed.Health == SampleHealth.Unavailable && changed.Windows.Count == 0 && changed.AccountKey is null);
        });
        await Check("identity changing during quota read is never attributed", async () =>
        {
            await using var provider = Fixture("account-race");
            var raced = await provider.ReadAsync();
            Require(raced.Health == SampleHealth.Fresh && raced.Windows[0].RemainingPercent == 87.5 && raced.AccountKey is null);
            var stable = await provider.ReadAsync();
            Require(stable.Health == SampleHealth.Fresh && stable.AccountKey is not null);
        });
        await Check("ambiguous in-flight quota is not retained for the new account", async () =>
        {
            await using var provider = Fixture("account-race-failure");
            var raced = await provider.ReadAsync();
            Require(raced.Health == SampleHealth.Fresh && raced.AccountKey is null);
            var failed = await provider.ReadAsync();
            Require(failed.Health == SampleHealth.Unavailable && failed.Windows.Count == 0 && failed.AccountKey is null);
        });
        foreach (var scenario in new[] { "identity-unavailable", "identity-null", "identity-no-email", "identity-hang" })
        {
            await Check($"{scenario} preserves actual quota without attribution", async () =>
            {
                await using var provider = Fixture(scenario);
                var sample = await provider.ReadAsync();
                Require(sample.Health == SampleHealth.Fresh && sample.Windows[0].RemainingPercent == 87.5 && sample.AccountKey is null);
            });
        }
        await Check("stable account ID takes precedence over mutable email", async () =>
        {
            await using var provider = Fixture("stable-id");
            var first = await provider.ReadAsync();
            var changedEmail = await provider.ReadAsync();
            Require(first.AccountKey is not null && first.AccountKey == changedEmail.AccountKey);
        });
        await Check("missing CLI remains unavailable", async () =>
        {
            await using var provider = new QuotaProvider(Path.Combine(AppContext.BaseDirectory, "missing-codex-fixture.exe"));
            var sample = await provider.ReadAsync();
            Require(sample.Health == SampleHealth.Unavailable && sample.Windows.Count == 0 && provider.OwnedProcessId is null);
        });
        await Check("caller cancellation promptly releases stalled server", async () =>
        {
            await using var provider = new QuotaProvider(executable, Path.Combine(AppContext.BaseDirectory, "quota-fixture-hang"));
            using var cancellation = new CancellationTokenSource();
            var pending = provider.ReadAsync(cancellation.Token);
            var pid = await WaitForProcess(provider);
            cancellation.Cancel();
            await ExpectCancellation(pending);
            Require(provider.OwnedProcessId is null && !Alive(pid));
        });
        await Check("disposal cancels an in-flight read and is idempotent", async () =>
        {
            var provider = new QuotaProvider(executable, Path.Combine(AppContext.BaseDirectory, "quota-fixture-hang"));
            try
            {
                var pending = provider.ReadAsync();
                var pid = await WaitForProcess(provider);
                await provider.DisposeAsync();
                await ExpectCancellation(pending);
                Require(provider.OwnedProcessId is null && !Alive(pid));
                await provider.DisposeAsync();
                try { await provider.ReadAsync(); throw new InvalidOperationException("Expected disposed provider rejection."); }
                catch (ObjectDisposedException) { }
            }
            finally { await provider.DisposeAsync(); }
        });
        return failures;

        QuotaProvider Fixture(string scenario) => new(executable, Path.Combine(AppContext.BaseDirectory, "quota-fixture-" + scenario));

        async Task Check(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine($"PASS quota transport: {name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL quota transport: {name}: {ex.Message}"); }
        }
    }

    private static async Task<int> WaitForProcess(QuotaProvider provider)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (provider.OwnedProcessId is { } pid) return pid;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Fixture failed to start.");
    }

    private static async Task ExpectCancellation(Task<QuotaSnapshot> pending)
    {
        try { await pending.WaitAsync(TimeSpan.FromSeconds(8)); throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }
}
