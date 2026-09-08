using CodexHud.Core;

public static class QuotaEstimateTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public static int Run()
    {
        var failures = 0;
        Check("three independent segments calibrate an empirical balance", () =>
        {
            var estimate = Trained().Get(Window(94), Start.AddMinutes(3));
            Require(estimate.State == EstimateState.Estimated && estimate.Segments == 3 && estimate.ObservedDrop == 6);
            Near(estimate.RemainingTokens, 47000);
            Near(estimate.LowerTokens, 47000);
            Near(estimate.UpperTokens, 47000);
            Require(estimate.From == Start && estimate.Through == Start.AddMinutes(3));
            Require(estimate.Detail.Contains("本机") && estimate.Detail.Contains("并非置信区间"));
        });
        Check("one large drop is not three independent observations", () =>
        {
            var estimator = new QuotaTokenEstimator();
            Observe(estimator, 0, 100, 0);
            Observe(estimator, 1, 94, 3000);
            var estimate = estimator.Get(Window(94), Start.AddMinutes(1));
            Require(estimate.State == EstimateState.Calibrating && estimate.Segments == 1 && estimate.RemainingTokens is null);
        });
        Check("no-drop tokens accumulate to the eventual quota endpoint", () =>
        {
            var estimator = new QuotaTokenEstimator();
            Observe(estimator, 0, 100, 0);
            Observe(estimator, 1, 100, 1000);
            Observe(estimator, 2, 100, 2000);
            Observe(estimator, 3, 98, 3000);
            Observe(estimator, 4, 96, 6000);
            Observe(estimator, 5, 94, 9000);
            Near(estimator.Get(Window(94), Start.AddMinutes(5)).RemainingTokens, 141000);
        });
        Check("fractional changes accumulate without overlapping segments", () =>
        {
            var estimator = new QuotaTokenEstimator();
            Observe(estimator, 0, 100, 0);
            for (var i = 1; i <= 12; i++) Observe(estimator, i, 100 - .5 * i, 500 * i);
            var estimate = estimator.Get(Window(94), Start.AddMinutes(12));
            Require(estimate.Segments == 3 && estimate.ObservedDrop == 6);
            Near(estimate.RemainingTokens, 94000);
        });
        Check("zero requires calibration and is preserved after calibration", () =>
        {
            var untrained = new QuotaTokenEstimator();
            Observe(untrained, 0, 0, 0);
            Require(untrained.Get(Window(0), Start).RemainingTokens is null);
            var trained = Trained();
            Observe(trained, 4, 0, 50000);
            var estimate = trained.Get(Window(0), Start.AddMinutes(4));
            Require(estimate.State == EstimateState.Estimated);
            Near(estimate.RemainingTokens, 0);
            Near(estimate.UpperTokens, 0);
        });
        Check("hundred percent and no decline do not divide by zero", () =>
        {
            var estimator = new QuotaTokenEstimator();
            for (var i = 0; i < 10; i++) Observe(estimator, i, 100, i * 1000);
            var estimate = estimator.Get(Window(100), Start.AddMinutes(9));
            Require(estimate.State == EstimateState.Calibrating && estimate.Segments == 0 && estimate.RemainingTokens is null);
        });
        Check("empirical segment spread exposes variable usage", () =>
        {
            var estimator = new QuotaTokenEstimator();
            Observe(estimator, 0, 100, 0);
            Observe(estimator, 1, 98, 1000);
            Observe(estimator, 2, 96, 6000);
            Observe(estimator, 3, 94, 8000);
            var estimate = estimator.Get(Window(94), Start.AddMinutes(3));
            Require(estimate.State == EstimateState.Variable);
            Near(estimate.RemainingTokens, 8000d / 6 * 94);
            Near(estimate.LowerTokens, 47000);
            Near(estimate.UpperTokens, 235000);
        });
        Check("a token lag pauses balance and can catch up without a zero ratio", () =>
        {
            var estimator = Trained();
            Observe(estimator, 4, 92, 3000);
            var paused = estimator.Get(Window(92), Start.AddMinutes(4));
            Require(paused.State == EstimateState.Incomplete && paused.RemainingTokens is null);
            Observe(estimator, 5, 92, 4000);
            Near(estimator.Get(Window(92), Start.AddMinutes(5)).RemainingTokens, 46000);
        });
        Check("unmatched account decline pauses without losing closed samples", () =>
        {
            var estimator = Trained();
            for (var minute = 4; minute <= 7; minute++) Observe(estimator, minute, 92, 3000);
            var estimate = estimator.Get(Window(92), Start.AddMinutes(7));
            Require(estimate.State == EstimateState.Calibrating && estimate.Segments == 3 && estimate.RemainingTokens is null);
        });
        Check("stale quota, incomplete ledger, and alignment gaps do not retain a balance", () =>
        {
            foreach (var mode in new[] { "quota", "ledger", "alignment", "missing", "account" })
            {
                var estimator = Trained();
                var quota = Snapshot(4, 92, mode == "quota" ? SampleHealth.Stale : SampleHealth.Fresh,
                    mode == "account" ? null : "account-A");
                var usage = Usage(4, 4000) with
                {
                    Health = mode == "ledger" ? SampleHealth.Partial : SampleHealth.Fresh,
                    ObservedAt = Start.AddMinutes(mode == "alignment" ? 3 : 4),
                    Counts = mode == "missing" ? Counts(4000) with { TotalTokens = null } : Counts(4000)
                };
                estimator.Observe(quota, usage);
                var estimate = estimator.Get(Window(92), Start.AddMinutes(4));
                Require(estimate.State == (mode == "quota" ? EstimateState.Stale : EstimateState.Incomplete));
                Require(estimate.Segments == 3 && estimate.RemainingTokens is null);
            }
        });
        Check("partial independent quota fields do not block a complete selected period", () =>
        {
            var estimator = Trained();
            estimator.Observe(Snapshot(4, 92, SampleHealth.Partial), Usage(4, 4000));
            Require(estimator.Get(Window(92), Start.AddMinutes(4)).State == EstimateState.Estimated);
        });
        Check("first incomplete snapshot stays incomplete", () =>
        {
            var estimator = new QuotaTokenEstimator();
            estimator.Observe(Snapshot(0, 100), Usage(0, 0) with { Health = SampleHealth.Partial });
            Require(estimator.Get(Window(100), Start).State == EstimateState.Incomplete);
        });
        Check("unknown pools cannot borrow the Codex conversion", () =>
        {
            var estimator = Trained();
            var other = Window(50) with { Key = "other/primary/300", LimitId = "other" };
            var estimate = estimator.Get(other, Start.AddMinutes(3));
            Require(estimate.State == EstimateState.Unavailable && estimate.RemainingTokens is null);
            Require(estimator.Get(null, Start).State == EstimateState.Unavailable);
        });
        Check("accounts reset, epochs pause, and same-pool model or speed changes keep valid segments", () =>
        {
            foreach (var mode in new[] { "account", "epoch", "model", "speed" })
            {
                var estimator = Trained();
                estimator.Observe(Snapshot(4, 92, account: mode == "account" ? "account-B" : "account-A"),
                    Usage(4, 4000) with { Epoch = mode == "epoch" ? "rebuilt" : "epoch-A",
                        ModelMix = mode is "model" or "speed" ? mode + "-changed" : "model-A/default" });
                var estimate = estimator.Get(Window(92), Start.AddMinutes(4));
                if (mode == "account") Require(estimate.State == EstimateState.Calibrating && estimate.Segments == 0 && estimate.RemainingTokens is null);
                else if (mode == "epoch") Require(estimate.State == EstimateState.Calibrating && estimate.Segments == 3 && estimate.RemainingTokens is null);
                else Require(estimate.State == EstimateState.Variable && estimate.Segments == 4 && estimate.RemainingTokens > 0);
            }
        });
        Check("input-output and cache structure shifts preserve samples and flag variation", () =>
        {
            foreach (var mode in new[] { "output", "cache" })
            {
                var estimator = Trained();
                var counts = mode == "output" ? new TokenCounts(2600, 1300, 0, 1400, 0, 4000)
                    : new TokenCounts(3200, 1200, 0, 800, 0, 4000);
                estimator.Observe(Snapshot(4, 92), Usage(4, 4000) with { Counts = counts });
                var estimate = estimator.Get(Window(92), Start.AddMinutes(4));
                Require(estimate.State == EstimateState.Variable && estimate.Segments == 4);
            }
        });
        Check("resets, balance increases, and duration changes are isolated", () =>
        {
            foreach (var mode in new[] { "reset", "increase", "duration" })
            {
                var estimator = Trained();
                var window = Window(mode == "increase" ? 99 : 92);
                if (mode == "reset") window = window with { ResetsAt = Start.AddHours(6) };
                if (mode == "duration") window = window with { WindowMinutes = 600, Key = "codex/primary/600" };
                estimator.Observe(new(Start.AddMinutes(4), [window], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(4, 4000));
                var estimate = estimator.Get(window, Start.AddMinutes(4));
                Require(estimate.State == EstimateState.Calibrating && estimate.Segments == 0);
            }
        });
        Check("periods keep separate histories from the same sampled tokens", () =>
        {
            var estimator = new QuotaTokenEstimator();
            for (var i = 0; i <= 3; i++)
            {
                var secondary = Window(100 - 4 * i) with { Key = "codex/secondary/10080", Slot = "secondary", WindowMinutes = 10080 };
                estimator.Observe(new(Start.AddMinutes(i), [Window(100 - 2 * i), secondary], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(i, 1000 * i));
            }
            Near(estimator.Get(Window(94), Start.AddMinutes(3)).RemainingTokens, 47000);
            Near(estimator.Get(Window(88) with { Key = "codex/secondary/10080", Slot = "secondary", WindowMinutes = 10080 }, Start.AddMinutes(3)).RemainingTokens, 22000);
        });
        Check("missing selected quota does not silently substitute another", () =>
        {
            var estimator = Trained();
            estimator.Observe(new(Start.AddMinutes(4), [], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(4, 4000));
            Require(estimator.Get(Window(94), Start.AddMinutes(4)).State == EstimateState.Unavailable);
        });
        Check("expired readings, replayed timestamps, and reset deadlines are gated", () =>
        {
            var estimator = Trained();
            Require(estimator.Get(Window(94), Start.AddMinutes(3).AddSeconds(151)).State == EstimateState.Stale);
            estimator.Observe(Snapshot(3, 94), Usage(3, 8000));
            Require(estimator.Get(Window(94), Start.AddMinutes(3).AddSeconds(151)).State == EstimateState.Stale);
            Require(estimator.Get(Window(94), Start.AddHours(5)).RemainingTokens is null);
            Require(estimator.Get(Window(93), Start.AddMinutes(3)).State == EstimateState.Incomplete);
        });
        Check("long gaps and regressing counters cannot span a new segment", () =>
        {
            foreach (var mode in new[] { "gap", "counts", "time" })
            {
                var estimator = Trained();
                var minute = mode == "gap" ? 7 : mode == "time" ? 2 : 4;
                Observe(estimator, minute, 92, mode == "counts" ? 2000 : 4000);
                var estimate = estimator.Get(Window(92), Start.AddMinutes(minute));
                Require(estimate.RemainingTokens is null && estimate.Segments == (mode == "time" ? 2 : 3));
                Require(estimate.State == (mode == "time" ? EstimateState.Stale : EstimateState.Calibrating));
            }
        });
        Check("invalid percent and inconsistent token deltas cannot calibrate", () =>
        {
            foreach (var percentage in new double?[] { null, double.NaN, double.PositiveInfinity, -1, 101 })
            {
                var estimator = Trained();
                var window = Window(92) with { RemainingPercent = percentage };
                estimator.Observe(new(Start.AddMinutes(4), [window], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(4, 4000));
                Require(estimator.Get(window, Start.AddMinutes(4)).RemainingTokens is null);
            }
            var inconsistent = Trained();
            inconsistent.Observe(Snapshot(4, 92), Usage(4, 4000) with { Counts = new(3200, 2200, 0, 800, 0, 4000) });
            Require(inconsistent.Get(Window(92), Start.AddMinutes(4)).State == EstimateState.Incomplete);
        });
        Check("explicit invalidation is durable until a new baseline", () =>
        {
            var estimator = Trained();
            estimator.Invalidate("采样中断");
            var estimate = estimator.Get(Window(94), Start.AddMinutes(3));
            Require(estimate.State == EstimateState.Incomplete && estimate.RemainingTokens is null && estimate.Detail.Contains("采样中断"));
            Observe(estimator, 4, 92, 4000);
            Require(estimator.Get(Window(92), Start.AddMinutes(4)).State == EstimateState.Calibrating);
        });
        Check("restart restores anonymous segments but never bridges downtime", () => WithStorage(path =>
        {
            var estimator = Trained(path);
            var json = File.ReadAllText(path);
            Require(!json.Contains("account-A") && !json.Contains("epoch-A") && !json.Contains("model-A"));
            var restarted = new QuotaTokenEstimator(path);
            restarted.Observe(Snapshot(4, 94), Usage(4, 0) with { Epoch = "restart" });
            var warming = restarted.Get(Window(94), Start.AddMinutes(4));
            Require(warming.State == EstimateState.Calibrating && warming.Segments == 3 && warming.RemainingTokens is null);
            restarted.Observe(Snapshot(5, 92), Usage(5, 1000) with { Epoch = "restart" });
            var estimate = restarted.Get(Window(92), Start.AddMinutes(5));
            Require(estimate.State == EstimateState.Estimated && estimate.Segments == 4 && estimate.ObservedDrop == 8);
            Near(estimate.RemainingTokens, 46000);
            Require(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any());
        }));
        Check("restored history rejects another account and retains changed-model samples pending confirmation", () => WithStorage(path =>
        {
            Trained(path);
            var account = new QuotaTokenEstimator(path);
            account.Observe(Snapshot(4, 94, account: "other"), Usage(4, 0));
            Require(account.Get(Window(94), Start.AddMinutes(4)).Segments == 0);
            Trained(path);
            var model = new QuotaTokenEstimator(path);
            model.Observe(Snapshot(4, 94), Usage(4, 0) with { ModelMix = "other" });
            Require(model.Get(Window(94), Start.AddMinutes(4)).Segments == 3);
            Require(model.Get(Window(94), Start.AddMinutes(4)).RemainingTokens is null);
            model.Observe(Snapshot(5, 92), Usage(5, 1000) with { ModelMix = "other" });
            Require(model.Get(Window(92), Start.AddMinutes(5)).State == EstimateState.Variable);
        }));
        Check("restart waits through missing ledger and model before confirming saved history", () => WithStorage(path =>
        {
            Trained(path);
            var restarted = new QuotaTokenEstimator(path);
            restarted.Observe(Snapshot(4, 94), Usage(4, 0) with { Health = SampleHealth.Partial, ModelMix = null });
            Require(restarted.Get(Window(94), Start.AddMinutes(4)).State == EstimateState.Incomplete);
            restarted.Observe(Snapshot(5, 94), Usage(5, 0) with { Epoch = "restart", ModelMix = null });
            Require(restarted.Get(Window(94), Start.AddMinutes(5)).RemainingTokens is null);
            // Persistence must still contain the saved segments while the model is unknown.
            var restartedAgain = new QuotaTokenEstimator(path);
            restartedAgain.Observe(Snapshot(6, 94), Usage(6, 0) with { Epoch = "restart-2", ModelMix = null });
            restartedAgain.Observe(Snapshot(7, 94), Usage(7, 0) with { Epoch = "restart-2" });
            var confirming = restartedAgain.Get(Window(94), Start.AddMinutes(7));
            Require(confirming.State == EstimateState.Calibrating && confirming.Segments == 3);
            restartedAgain.Observe(Snapshot(8, 92), Usage(8, 1000) with { Epoch = "restart-2" });
            Near(restartedAgain.Get(Window(92), Start.AddMinutes(8)).RemainingTokens, 46000);
        }));
        Check("bad, oversized, and null persistence safely start fresh", () => WithStorage(path =>
        {
            foreach (var json in new[] { "{", "{\"Version\":1,\"Windows\":[null]}", "null", new string('x', 600000) })
            {
                File.WriteAllText(path, json);
                var estimator = new QuotaTokenEstimator(path);
                Observe(estimator, 0, 100, 0);
                Require(estimator.Get(Window(100), Start).RemainingTokens is null);
            }
            var noFile = new QuotaTokenEstimator(Path.GetDirectoryName(path));
            Observe(noFile, 0, 100, 0); // An unwritable destination does not fail live sampling.
        }));
        Check("history older than 24 hours cannot supply an estimate", () =>
        {
            var estimator = Trained();
            Require(estimator.Get(Window(94), Start.AddHours(25)).Segments == 0);
        });
        Check("large cumulative values are differenced before conversion to double", () =>
        {
            var estimator = new QuotaTokenEstimator();
            var initial = long.MaxValue - 10000;
            for (var i = 0; i <= 3; i++) Observe(estimator, i, 100 - 2 * i, initial + i * 1000);
            Near(estimator.Get(Window(94), Start.AddMinutes(3)).RemainingTokens, 47000);
        });
        Check("random boundary samples always produce finite nonnegative estimates or unknown", () =>
        {
            var random = new Random(4901);
            var estimator = new QuotaTokenEstimator();
            long total = 0;
            double remaining = 100;
            for (var i = 0; i < 1000; i++)
            {
                total += random.Next(0, 10000);
                remaining = Math.Max(0, remaining - random.NextDouble() * 3);
                if (i % 70 == 0) remaining = 100;
                var quota = Snapshot(i, remaining);
                var usage = Usage(i, total);
                if (i % 113 == 0) usage = usage with { Health = SampleHealth.Partial };
                estimator.Observe(quota, usage);
                var estimate = estimator.Get(Window(remaining), Start.AddMinutes(i));
                foreach (var value in new[] { estimate.RemainingTokens, estimate.LowerTokens, estimate.UpperTokens })
                    if (value is not null && (!double.IsFinite(value.Value) || value < 0))
                        throw new InvalidOperationException($"iteration={i}; invalid value={value:R}; state={estimate.State}");
                if (estimate.Segments > 64 || estimate.ObservedDrop > 100)
                    throw new InvalidOperationException($"iteration={i}; segments={estimate.Segments}; drop={estimate.ObservedDrop:R}; remaining={remaining:R}");
            }
            estimator.Get(Window(100), DateTimeOffset.MinValue);
            estimator.Get(Window(100), DateTimeOffset.MaxValue);
        });
        Check("pending decline is visible without pretending it is a completed segment", () =>
        {
            var estimator = new QuotaTokenEstimator();
            Observe(estimator, 0, 100, 0);
            Observe(estimator, 1, 99, 500);
            var pending = estimator.Get(Window(99), Start.AddMinutes(1));
            Require(pending.Segments == 0 && pending.ObservedDrop == 0 && pending.PendingDrop == 1);
            Observe(estimator, 2, 98, 1000);
            var closed = estimator.Get(Window(98), Start.AddMinutes(2));
            Require(closed.Segments == 1 && closed.ObservedDrop == 2 && closed.PendingDrop == 0);
        });
        Check("pause ignores replays and confirms only a new complete segment", () =>
        {
            var estimator = Trained();
            estimator.Observe(Snapshot(4, 92), Usage(4, 4000) with { Health = SampleHealth.Partial });
            estimator.Observe(Snapshot(3, 94), Usage(3, 3000));
            var replay = estimator.Get(Window(94), Start.AddMinutes(3));
            Require(replay.State == EstimateState.Incomplete && replay.Segments == 3 && replay.RemainingTokens is null);
            Observe(estimator, 5, 90, 5000);
            Require(estimator.Get(Window(90), Start.AddMinutes(5)).RemainingTokens is null);
            Observe(estimator, 6, 89, 5500);
            Require(estimator.Get(Window(89), Start.AddMinutes(6)).PendingDrop == 1);
            Require(estimator.Get(Window(89), Start.AddMinutes(6)).RemainingTokens is null);
            Observe(estimator, 7, 88, 6000);
            var confirmed = estimator.Get(Window(88), Start.AddMinutes(7));
            Require(confirmed.State == EstimateState.Estimated && confirmed.Segments == 4 && confirmed.ObservedDrop == 8);
            Near(confirmed.RemainingTokens, 44000);
        });
        Check("an older higher balance cannot erase closed history during a pause", () =>
        {
            var estimator = Trained();
            estimator.Invalidate("暂停");
            Observe(estimator, 2, 96, 2000);
            var held = estimator.Get(Window(94), Start.AddMinutes(3));
            Require(held.State == EstimateState.Stale && held.Segments == 3 && held.RemainingTokens is null);
        });
        Check("idle profile expiry and mixed workloads do not erase progress", () =>
        {
            var estimator = new QuotaTokenEstimator();
            Observe(estimator, 0, 100, 0);
            Observe(estimator, 1, 98, 1000);
            estimator.Observe(Snapshot(2, 97), Usage(2, 1500) with { ModelMix = null });
            estimator.Observe(Snapshot(3, 96), Usage(3, 2000) with { ModelMix = "model-A/default | model-B/high" });
            estimator.Observe(Snapshot(4, 94), Usage(4, 3000) with { ModelMix = "model-A/default" });
            var estimate = estimator.Get(Window(94), Start.AddMinutes(4));
            Require(estimate.State == EstimateState.Variable && estimate.Segments == 3 && estimate.ObservedDrop == 6);
            Near(estimate.RemainingTokens, 47000);
        });
        Check("cache shifts during initial calibration still produce an honest variable estimate", () =>
        {
            var estimator = new QuotaTokenEstimator();
            var totals = new[] { new TokenCounts(0,0,0,0,0,0), new TokenCounts(800,400,0,200,0,1000),
                new TokenCounts(1600,400,0,400,0,2000), new TokenCounts(2400,1200,0,600,0,3000) };
            for (int i = 0; i < totals.Length; i++)
                estimator.Observe(Snapshot(i,100-i*2), Usage(i,i*1000) with { Counts = totals[i] });
            var result = estimator.Get(Window(94), Start.AddMinutes(3));
            Require(result.State == EstimateState.Variable && result.Segments == 3);
        });
        Check("paused history and reason survive repeated restarts without duplicating segments", () => WithStorage(path =>
        {
            var estimator = Trained(path);
            estimator.Invalidate("采样中断测试");
            var saved = File.ReadAllText(path);
            Require(saved.Contains("采样中断测试") || saved.Contains("\\u91C7"));
            for (int i = 4; i <= 5; i++)
            {
                estimator = new QuotaTokenEstimator(path);
                estimator.Observe(Snapshot(i,94), Usage(i,0) with { Epoch = "new-"+i });
                var warming = estimator.Get(Window(94), Start.AddMinutes(i));
                Require(warming.Segments == 3 && warming.RemainingTokens is null && warming.LastResetReason == "采样中断测试");
            }
            estimator.Observe(Snapshot(6,92), Usage(6,1000) with { Epoch = "new-5" });
            Require(estimator.Get(Window(92), Start.AddMinutes(6)).Segments == 4);
        }));
        Check("same-period quota rebound after restart rejects the old samples", () => WithStorage(path =>
        {
            Trained(path);
            var estimator = new QuotaTokenEstimator(path);
            estimator.Observe(Snapshot(4,99), Usage(4,0) with { Epoch = "new" });
            var result = estimator.Get(Window(99), Start.AddMinutes(4));
            Require(result.Segments == 0 && result.RemainingTokens is null);
        }));
        Check("period reset and rebound clear persisted history even while ledger is incomplete", () => WithStorage(path =>
        {
            foreach (string mode in new[] { "deadline", "identity", "rebound" })
            {
                var estimator = Trained(path);
                var window = Window(mode == "rebound" ? 99 : 92);
                var time = mode == "deadline" ? Start.AddHours(5) : Start.AddMinutes(4);
                if (mode == "identity") window = window with { ResetsAt = Start.AddHours(6) };
                estimator.Observe(new(time, [window], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(4,4000) with { ObservedAt = time, Health = SampleHealth.Partial });
                estimator = new QuotaTokenEstimator(path);
                estimator.Observe(new(time.AddMinutes(1), [window], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(5,0) with { ObservedAt = time.AddMinutes(1) });
                Require(estimator.Get(window, time.AddMinutes(1)).Segments == 0);
            }
        }));
        Check("old accounting format cannot seed the corrected collection scope", () => WithStorage(path =>
        {
            Trained(path);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"Version\":2", "\"Version\":1"));
            var estimator = new QuotaTokenEstimator(path);
            Observe(estimator, 4, 94, 0);
            Require(estimator.Get(Window(94), Start.AddMinutes(4)).Segments == 0);
        }));
        Check("zero-segment pause still persists a diagnostic reason", () => WithStorage(path =>
        {
            var estimator = new QuotaTokenEstimator(path);
            Observe(estimator, 0, 100, 0);
            estimator.Invalidate("记录暂不可读");
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var window = document.RootElement.GetProperty("Windows")[0];
            Require(window.GetProperty("Segments").GetArrayLength() == 0);
            Require(window.GetProperty("LastResetReason").GetString() == "记录暂不可读");
            Require(window.GetProperty("LastResetAt").ValueKind == System.Text.Json.JsonValueKind.String);
        }));
        Check("an older period snapshot cannot reset the current-period history", () =>
        {
            var estimator = Trained();
            var older = Window(96) with { ResetsAt = Start.AddHours(6) };
            estimator.Observe(new(Start.AddMinutes(2), [older], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(2,2000));
            var held = estimator.Get(Window(94), Start.AddMinutes(3));
            Require(held.State == EstimateState.Stale && held.Segments == 3);
        });
        Check("missing reset identity pauses and resumes without erasing closed samples", () => WithStorage(path =>
        {
            var estimator = Trained(path);
            var missing = Window(92) with { ResetsAt = null };
            estimator.Observe(new(Start.AddMinutes(4), [missing], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(4,4000));
            var paused = estimator.Get(missing, Start.AddMinutes(4));
            Require(paused.State == EstimateState.Incomplete && paused.Segments == 3 && paused.RemainingTokens is null);
            Require(paused.LastResetReason!.Contains("重置时间"));
            estimator = new QuotaTokenEstimator(path);
            Observe(estimator,5,92,0);
            Require(estimator.Get(Window(92), Start.AddMinutes(5)).Segments == 3);
            Require(estimator.Get(Window(92), Start.AddMinutes(5)).RemainingTokens is null);
            Observe(estimator,6,90,1000);
            Require(estimator.Get(Window(90), Start.AddMinutes(6)).Segments == 4);
            Near(estimator.Get(Window(90), Start.AddMinutes(6)).RemainingTokens,45000);
        }));
        Check("restart preserves newer saved history when the first snapshot is an older period", () => WithStorage(path =>
        {
            Trained(path);
            var estimator = new QuotaTokenEstimator(path);
            var older = Window(96) with { ResetsAt = Start.AddHours(6) };
            estimator.Observe(new(Start.AddMinutes(2), [older], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(2,2000));
            using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
                Require(saved.RootElement.GetProperty("Windows")[0].GetProperty("Segments").GetArrayLength() == 3);
            estimator = new QuotaTokenEstimator(path);
            Observe(estimator,4,94,0);
            var held = estimator.Get(Window(94), Start.AddMinutes(4));
            Require(held.Segments == 3 && held.RemainingTokens is null);
            Observe(estimator,5,92,1000);
            Near(estimator.Get(Window(92), Start.AddMinutes(5)).RemainingTokens,46000);
        }));
        return failures;

        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine($"PASS estimate: {name}"); }
            catch (Exception error) { failures++; Console.WriteLine($"FAIL estimate: {name}: {error.Message}"); }
        }
    }

    private static QuotaTokenEstimator Trained(string? path = null)
    {
        var estimator = new QuotaTokenEstimator(path);
        for (var i = 0; i <= 3; i++) Observe(estimator, i, 100 - 2 * i, i * 1000);
        return estimator;
    }
    private static QuotaWindow Window(double remaining) => new("codex/primary/300", "codex", "Codex · 5 小时", "primary", 300, remaining, Start.AddHours(5));
    private static QuotaSnapshot Snapshot(int minute, double remaining, SampleHealth health = SampleHealth.Fresh, string? account = "account-A") =>
        new(Start.AddMinutes(minute), [Window(remaining)], health) { AccountKey = account };
    private static UsageLedgerSnapshot Usage(int minute, long total) => new(Start.AddMinutes(minute), "epoch-A", Counts(total), SampleHealth.Fresh, 2, "model-A/default");
    private static TokenCounts Counts(long total)
    {
        var output = total / 5;
        var input = total - output;
        return new(input, input / 2, 0, output, 0, total);
    }
    private static void Observe(QuotaTokenEstimator estimator, int minute, double remaining, long total) => estimator.Observe(Snapshot(minute, remaining), Usage(minute, total));
    private static void Near(double? value, double expected) => Require(value is not null && Math.Abs(value.Value - expected) <= Math.Max(.000001, Math.Abs(expected) * 1e-10));
    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }
    private static void WithStorage(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-hud-estimate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(Path.Combine(directory, "history.json")); }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
