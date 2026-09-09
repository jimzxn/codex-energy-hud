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
        Check("metadata changes and refills are reviewed before history is archived", () =>
        {
            foreach (var mode in new[] { "metadata", "increase", "duration" })
            {
                var estimator = Trained();
                var window = Window(mode == "increase" ? 99 : 92);
                if (mode == "metadata") window = window with { ResetsAt = Start.AddHours(6) };
                if (mode == "duration") window = window with { WindowMinutes = 600, Key = "codex/primary/600" };
                estimator.Observe(new(Start.AddMinutes(4), [window], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(4, 4000));
                var held = estimator.Get(window, Start.AddMinutes(4));
                Require(held.State == EstimateState.Calibrating && held.Segments == (mode == "duration" ? 0 : 3));
                if (mode == "duration") continue;
                estimator.Observe(new(Start.AddMinutes(5), [window], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(5, 4000));
                var confirmed = estimator.Get(window, Start.AddMinutes(5));
                Require(confirmed.Segments == (mode == "increase" ? 0 : 4));
                Require(confirmed.RemainingTokens is null);
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
            Require(confirming.State == EstimateState.Estimated && confirming.Segments == 3 && confirming.RecoveryRequired == 0);
            restartedAgain.Observe(Snapshot(8, 92), Usage(8, 1000) with { Epoch = "restart-2" });
            Near(restartedAgain.Get(Window(92), Start.AddMinutes(8)).RemainingTokens, 46000);
        }));
        Check("saved history is visible before the first quota sample without consuming its checkpoint", () => WithStorage(path =>
        {
            var original = new QuotaTokenEstimator(path);
            for (var i = 0; i <= 3; i++) original.Observe(Snapshot(i, 100 - 2 * i), CheckpointUsage(i, i * 1000));
            original.Observe(Snapshot(4, 93), CheckpointUsage(4, 3500));
            var saved = File.ReadAllText(path);
            var restarted = new QuotaTokenEstimator(path);
            var checkpoint = restarted.RestoredLedgerCheckpoint;
            for (var i = 0; i < 3; i++)
            {
                var preview = restarted.GetSavedHistory(Window(93).Key, Start.AddMinutes(5));
                Require(preview.State == EstimateState.Incomplete && preview.Segments == 3 && preview.ObservedDrop == 6);
                Require(preview.From == Start && preview.Through == Start.AddMinutes(4) && preview.PendingDrop == 1);
                Require(preview.RemainingTokens is null && preview.LowerTokens is null && preview.UpperTokens is null);
                Require(preview.RecoverySamples == 0 && preview.RecoveryRequired == 2 && preview.ProgressReason!.Contains("已保存历史"));
                Require(restarted.Get(Window(93), Start.AddMinutes(5)).Segments == 3);
                Require(ReferenceEquals(checkpoint, restarted.RestoredLedgerCheckpoint) && File.ReadAllText(path) == saved);
            }
            restarted.Observe(Snapshot(5, 93), CheckpointUsage(5, 3500));
            Require(restarted.Get(Window(93), Start.AddMinutes(5)).RecoverySamples == 1);
            restarted.Observe(Snapshot(6, 93), CheckpointUsage(6, 3500));
            var restored = restarted.Get(Window(93), Start.AddMinutes(6));
            Require(restored.State == EstimateState.Estimated && restored.Segments == 3 && restored.PendingDrop == 1);
            Near(restored.RemainingTokens, 46500);
        }));
        Check("quota startup failures and missing identity retain a visible saved history", () => WithStorage(path =>
        {
            foreach (var health in new[] { SampleHealth.Unavailable, SampleHealth.Loading, SampleHealth.Stale, SampleHealth.Fresh })
            {
                Trained(path);
                var restarted = new QuotaTokenEstimator(path);
                restarted.Observe(Snapshot(4, 94, health, account: null), Usage(4, 0));
                var saved = File.ReadAllText(path);
                var preview = restarted.GetSavedHistory(Window(94).Key, Start.AddMinutes(4));
                Require(preview.Segments == 3 && preview.ObservedDrop == 6 && preview.RemainingTokens is null);
                Require(preview.State == (health == SampleHealth.Fresh ? EstimateState.Incomplete : EstimateState.Stale));
                Require(restarted.Get(Window(94), Start.AddMinutes(4)).Segments == 3 && File.ReadAllText(path) == saved);
            }
        }));
        Check("older quota snapshots leave newer saved history visible but unconfirmed", () => WithStorage(path =>
        {
            Trained(path);
            var restarted = new QuotaTokenEstimator(path);
            restarted.Observe(Snapshot(2, 96), Usage(2, 2000));
            var preview = restarted.Get(Window(96), Start.AddMinutes(4));
            Require(preview.State == EstimateState.Stale && preview.Segments == 3 && preview.ObservedDrop == 6);
            Require(preview.RemainingTokens is null && preview.Through == Start.AddMinutes(3));
        }));
        Check("saved preview does not expose a known different account or an unselected key", () => WithStorage(path =>
        {
            Trained(path);
            var restarted = new QuotaTokenEstimator(path);
            Require(restarted.GetSavedHistory(null, Start.AddMinutes(4)).Segments == 0);
            Require(restarted.GetSavedHistory("codex/secondary/10080", Start.AddMinutes(4)).Segments == 0);
            restarted.Observe(new(Start.AddMinutes(4), [], SampleHealth.Fresh) { AccountKey = "other" }, Usage(4, 0));
            Require(restarted.GetSavedHistory(Window(94).Key, Start.AddMinutes(4)).Segments == 0);
            Require(restarted.Get(Window(94), Start.AddMinutes(4)).Segments == 0);
        }));
        Check("activated history remains visible when its live quota window disappears", () => WithStorage(path =>
        {
            Trained(path);
            var restarted = new QuotaTokenEstimator(path);
            restarted.Observe(Snapshot(4, 94), Usage(4, 0) with { Epoch = "restart" });
            restarted.Observe(Snapshot(5, 94), Usage(5, 0) with { Epoch = "restart" });
            Require(restarted.Get(Window(94), Start.AddMinutes(5)).State == EstimateState.Estimated);
            restarted.Observe(new(Start.AddMinutes(6), [], SampleHealth.Fresh) { AccountKey = "account-A" },
                Usage(6, 0) with { Epoch = "restart" });
            var saved = File.ReadAllText(path);
            var preview = restarted.GetSavedHistory(Window(94).Key, Start.AddMinutes(6));
            Require(preview.Segments == 3 && preview.ObservedDrop == 6 && preview.RemainingTokens is null);
            Require(preview.State == EstimateState.Incomplete && preview.ProgressReason!.Contains("已保留历史"));
            Require(preview.From == Start && preview.Through == Start.AddMinutes(5));
            Require(preview.RecoverySamples == 0 && preview.RecoveryRequired == 2);
            Require(restarted.GetSavedHistory(Window(94).Key, Start.AddHours(25)).Segments == 0);
            Require(restarted.GetSavedHistory(Window(94).Key, Start.AddMinutes(6)).Segments == 3);
            Require(File.ReadAllText(path) == saved);
        }));
        Check("saved preview excludes expired and future segments without changing the retained record", () => WithStorage(path =>
        {
            var original = Trained(path);
            Observe(original, 4, 93, 3500);
            var saved = File.ReadAllText(path);
            var restarted = new QuotaTokenEstimator(path);
            var expired = restarted.GetSavedHistory(Window(94).Key, Start.AddHours(25));
            Require(expired.Segments == 0 && expired.ObservedDrop == 0 && expired.PendingDrop == 0 && expired.RecoveryRequired == 0 && expired.RemainingTokens is null);
            Require(restarted.GetSavedHistory(Window(94).Key, Start.AddHours(-1)).Segments == 0);
            var current = restarted.GetSavedHistory(Window(94).Key, Start.AddMinutes(4));
            Require(current.Segments == 3 && current.PendingDrop == 1);
            Require(File.ReadAllText(path) == saved);
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
        Check("short same-epoch pauses retain progress and recover after two healthy reads", () =>
        {
            var estimator = Trained();
            estimator.Observe(Snapshot(4, 92), Usage(4, 4000) with { Health = SampleHealth.Partial });
            estimator.Observe(Snapshot(3, 94), Usage(3, 3000));
            var replay = estimator.Get(Window(94), Start.AddMinutes(3));
            Require(replay.State == EstimateState.Incomplete && replay.Segments == 3 && replay.RemainingTokens is null);
            Observe(estimator, 5, 90, 5000);
            var first = estimator.Get(Window(90), Start.AddMinutes(5));
            Require(first.RemainingTokens is null && first.RecoverySamples == 1 && first.Segments == 4 && first.ObservedDrop == 10);
            Observe(estimator, 6, 90, 5000);
            var confirmed = estimator.Get(Window(90), Start.AddMinutes(6));
            Require(confirmed.State == EstimateState.Estimated && confirmed.RecoveryRequired == 0);
            Require(confirmed.Segments == 4 && confirmed.ObservedDrop == 10);
            Near(confirmed.RemainingTokens, 45000);
        });
        Check("an older higher balance cannot erase closed history during a pause", () =>
        {
            var estimator = Trained();
            estimator.Invalidate("暂停");
            Observe(estimator, 2, 96, 2000);
            var held = estimator.Get(Window(94), Start.AddMinutes(3));
            Require(held.State == EstimateState.Incomplete && held.Segments == 3 && held.RemainingTokens is null && held.RecoveryRequired == 2 && held.LastResetReason == "暂停");
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
        Check("same-period refill after restart needs a second reading before archiving", () => WithStorage(path =>
        {
            Trained(path);
            var estimator = new QuotaTokenEstimator(path);
            estimator.Observe(Snapshot(4, 99), Usage(4, 0) with { Epoch = "new" });
            var review = estimator.Get(Window(99), Start.AddMinutes(4));
            Require(review.Segments == 3 && review.RemainingTokens is null);
            estimator.Observe(Snapshot(5, 99), Usage(5, 0) with { Epoch = "new" });
            var result = estimator.Get(Window(99), Start.AddMinutes(5));
            Require(result.Segments == 0 && result.RemainingTokens is null);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Require(saved.RootElement.GetProperty("Archives")[0].GetProperty("Segments").GetArrayLength() == 3);
        }));
        Check("quota-only review preserves history and confirms boundaries despite an incomplete ledger", () => WithStorage(path =>
        {
            foreach (string mode in new[] { "deadline", "metadata", "refill" })
            {
                if (File.Exists(path)) File.Delete(path); // Each mode starts an independent timeline.
                var estimator = Trained(path);
                var time = mode == "deadline" ? Start.AddHours(5) : Start.AddMinutes(4);
                var window = Window(mode == "refill" ? 99 : 92) with { ResetsAt = mode == "refill" ? Start.AddHours(5) : Start.AddHours(6) };
                for (int i = 0; i < 2; i++)
                    estimator.Observe(new(time.AddMinutes(i), [window], SampleHealth.Fresh) { AccountKey = "account-A" },
                        Usage(4 + i, 4000) with { ObservedAt = time.AddMinutes(i), Health = SampleHealth.Partial });
                using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var active = saved.RootElement.GetProperty("Windows")[0].GetProperty("Segments").GetArrayLength();
                Require(active == (mode == "metadata" ? 3 : 0));
                if (mode != "metadata") Require(saved.RootElement.GetProperty("Archives").GetArrayLength() > 0);
            }
        }));
        Check("old accounting format cannot seed the corrected collection scope", () => WithStorage(path =>
        {
            Trained(path);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"Version\":3", "\"Version\":1"));
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
        Check("mature history recovers while idle without spending another two points", () => WithStorage(path =>
        {
            Trained(path);
            var estimator = new QuotaTokenEstimator(path);
            estimator.Observe(Snapshot(4, 94), Usage(4, 0) with { Epoch = "idle-restart" });
            var first = estimator.Get(Window(94), Start.AddMinutes(4));
            Require(first.Segments == 3 && first.ObservedDrop == 6 && first.RecoverySamples == 1 && first.RecoveryRequired == 2);
            estimator.Observe(Snapshot(4, 94), Usage(4, 0) with { Epoch = "idle-restart" });
            Require(estimator.Get(Window(94), Start.AddMinutes(4)).RecoverySamples == 1);
            estimator.Observe(Snapshot(5, 94), Usage(5, 0) with { Epoch = "idle-restart" });
            var ready = estimator.Get(Window(94), Start.AddMinutes(5));
            Require(ready.State == EstimateState.Estimated && ready.Segments == 3 && ready.RecoveryRequired == 0);
            Near(ready.RemainingTokens, 47000);
        }));
        Check("unmatched quota interrupts consecutive recovery evidence", () =>
        {
            var estimator = Trained();
            estimator.Invalidate("恢复连续性测试");
            Observe(estimator, 4, 94, 3000);
            Require(estimator.Get(Window(94), Start.AddMinutes(4)).RecoverySamples == 1);
            Observe(estimator, 5, 93, 3000);
            var unmatched = estimator.Get(Window(93), Start.AddMinutes(5));
            Require(unmatched.State == EstimateState.Incomplete && unmatched.RecoverySamples == 0 && unmatched.PendingDrop == 1);
            Observe(estimator, 6, 93, 3500);
            Require(estimator.Get(Window(93), Start.AddMinutes(6)).RecoverySamples == 1);
            Require(estimator.Get(Window(93), Start.AddMinutes(6)).RemainingTokens is null);
            Observe(estimator, 7, 93, 3500);
            Near(estimator.Get(Window(93), Start.AddMinutes(7)).RemainingTokens, 46500);
        });
        Check("a short pause preserves one point and same-epoch continuity closes it", () =>
        {
            var estimator = new QuotaTokenEstimator();
            Observe(estimator, 0, 100, 0); Observe(estimator, 1, 99, 500);
            estimator.Observe(Snapshot(2, 98), Usage(2, 1000) with { Health = SampleHealth.Partial });
            var paused = estimator.Get(Window(98), Start.AddMinutes(2));
            Require(paused.PendingDrop == 1 && paused.Segments == 0 && paused.State == EstimateState.Incomplete);
            Observe(estimator, 3, 98, 1000);
            var closed = estimator.Get(Window(98), Start.AddMinutes(3));
            Require(closed.Segments == 1 && closed.ObservedDrop == 2);
        });
        Check("a changed epoch isolates both prior progress and newly unpaired decline", () => WithStorage(path =>
        {
            var estimator = Trained(path);
            Observe(estimator, 4, 93, 3500);
            estimator.Observe(Snapshot(5, 92), Usage(5, 0) with { Epoch = "rebuilt-ledger" });
            var held = estimator.Get(Window(92), Start.AddMinutes(5));
            Require(held.Segments == 3 && held.ObservedDrop == 6 && held.PendingDrop == 0);
            var diagnostic = held.Diagnostics.Last(e => e.Kind == "fragment-isolated");
            Require(diagnostic.PreviousRemaining == 93 && diagnostic.Remaining == 92);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var isolated = saved.RootElement.GetProperty("Windows")[0].GetProperty("Isolated");
            var last = isolated[isolated.GetArrayLength() - 1];
            Require(last.GetProperty("Drop").GetDouble() == 2);
            Require(last.GetProperty("PreviousPendingDrop").GetDouble() == 1);
            Require(last.GetProperty("AdditionalUnpairedDrop").GetDouble() == 1);
        }));
        Check("even a zero-pending epoch gap records its missing percentage point", () => WithStorage(path =>
        {
            var estimator = Trained(path);
            estimator.Observe(Snapshot(4, 93), Usage(4, 0) with { Epoch = "new-ledger" });
            var diagnostic = estimator.Get(Window(93), Start.AddMinutes(4)).Diagnostics.Last(e => e.Kind == "fragment-isolated");
            Require(diagnostic.PreviousRemaining == 94 && diagnostic.Remaining == 93);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Require(saved.RootElement.GetProperty("Windows")[0].GetProperty("Isolated")[0].GetProperty("Drop").GetDouble() == 1);
        }));
        Check("small reset timestamp drift preserves progress and the original boundary", () => WithStorage(path =>
        {
            var estimator = Trained(path);
            var first = Window(93) with { ResetsAt = Start.AddHours(5).AddSeconds(1) };
            estimator.Observe(new(Start.AddMinutes(4), [first], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(4, 3500));
            Require(estimator.Get(first, Start.AddMinutes(4)).PendingDrop == 1);
            var second = first with { RemainingPercent = 92, ResetsAt = Start.AddHours(5).AddSeconds(2) };
            estimator.Observe(new(Start.AddMinutes(5), [second], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(5, 4000));
            Require(estimator.Get(second, Start.AddMinutes(5)).Segments == 4);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Require(saved.RootElement.GetProperty("Windows")[0].GetProperty("AcceptedResetAt").GetDateTimeOffset() == Start.AddHours(5));
            Require(saved.RootElement.GetProperty("Archives").GetArrayLength() == 0);
        }));
        Check("stable large metadata correction recovers without moving the fixed guard", () => WithStorage(path =>
        {
            var estimator = Trained(path);
            var corrected = Window(94) with { ResetsAt = Start.AddHours(6) };
            for (int minute = 4; minute <= 6; minute++)
                estimator.Observe(new(Start.AddMinutes(minute), [corrected], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(minute, 3000));
            var estimate = estimator.Get(corrected, Start.AddMinutes(6));
            Require(estimate.State == EstimateState.Estimated && estimate.Segments == 3);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var active = saved.RootElement.GetProperty("Windows")[0];
            Require(active.GetProperty("AcceptedResetAt").GetDateTimeOffset() == Start.AddHours(5));
            Require(active.GetProperty("MetadataResetAt").GetDateTimeOffset() == Start.AddHours(6));
            Require(saved.RootElement.GetProperty("Archives").GetArrayLength() == 0);
            // Passing the fixed guard cannot be excused by a previously corrected future date.
            var crossedAt = Start.AddHours(5).AddMinutes(1);
            estimator.Observe(new(crossedAt, [corrected], SampleHealth.Fresh) { AccountKey = "account-A" }, Usage(301, 3000));
            Require(estimator.Get(corrected, crossedAt).RemainingTokens is null);
        }));
        Check("one cached refill response cannot destroy history or bridge its ambiguity", () => WithStorage(path =>
        {
            var estimator = Trained(path);
            Observe(estimator, 4, 99, 3500);
            Require(estimator.Get(Window(99), Start.AddMinutes(4)).Segments == 3);
            Observe(estimator, 5, 94, 4000); Observe(estimator, 6, 94, 4000);
            var result = estimator.Get(Window(94), Start.AddMinutes(6));
            Require(result.State == EstimateState.Estimated && result.Segments == 3 && result.ObservedDrop == 6);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Require(saved.RootElement.GetProperty("Archives").GetArrayLength() == 0);
        }));
        Check("V3 replays a proven pending anchor across restart exactly once", () => WithStorage(path =>
        {
            var estimator = new QuotaTokenEstimator(path);
            estimator.Observe(Snapshot(0, 100), CheckpointUsage(0, 0));
            estimator.Observe(Snapshot(1, 99), CheckpointUsage(1, 500));
            estimator = new QuotaTokenEstimator(path);
            Require(estimator.RestoredLedgerCheckpoint?.Sequence == 2);
            estimator.Observe(Snapshot(2, 98), CheckpointUsage(2, 1000));
            var closed = estimator.Get(Window(98), Start.AddMinutes(2));
            Require(closed.Segments == 1 && closed.ObservedDrop == 2);
            estimator.Observe(Snapshot(2, 98), CheckpointUsage(2, 1000));
            estimator.Observe(Snapshot(3, 98), CheckpointUsage(3, 1000));
            Require(estimator.Get(Window(98), Start.AddMinutes(3)).Segments == 1);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Require(saved.RootElement.GetProperty("Version").GetInt32() == 3);
            var cp = saved.RootElement.GetProperty("LedgerCheckpoint");
            var window = saved.RootElement.GetProperty("Windows")[0];
            Require(cp.GetProperty("Generation").GetString() == window.GetProperty("CheckpointGeneration").GetString());
            Require(cp.GetProperty("Sequence").GetInt64() == window.GetProperty("CheckpointSequence").GetInt64());
        }));
        Check("changed checkpoint generation cannot connect a restored old anchor", () => WithStorage(path =>
        {
            var estimator = new QuotaTokenEstimator(path);
            estimator.Observe(Snapshot(0, 100), CheckpointUsage(0, 0));
            estimator.Observe(Snapshot(1, 99), CheckpointUsage(1, 500));
            estimator = new QuotaTokenEstimator(path);
            estimator.Observe(Snapshot(2, 98), CheckpointUsage(2, 1000, "another-generation"));
            var isolated = estimator.Get(Window(98), Start.AddMinutes(2));
            Require(isolated.Segments == 0 && isolated.PendingDrop == 0);
            estimator.Observe(Snapshot(3, 97), CheckpointUsage(3, 1500, "another-generation"));
            Require(estimator.Get(Window(97), Start.AddMinutes(3)).PendingDrop == 1);
            estimator.Observe(Snapshot(4, 96), CheckpointUsage(4, 2000, "another-generation"));
            var closed = estimator.Get(Window(96), Start.AddMinutes(4));
            Require(closed.Segments == 1 && closed.ObservedDrop == 2 && closed.From == Start.AddMinutes(2));
        }));
        Check("fresh snapshots without checkpoints cannot pair a new anchor with an old generation", () => WithStorage(path =>
        {
            var estimator = new QuotaTokenEstimator(path);
            estimator.Observe(Snapshot(0, 100), CheckpointUsage(0, 0));
            estimator.Observe(Snapshot(1, 99), CheckpointUsage(1, 500));
            estimator.Observe(Snapshot(2, 98), Usage(2, 1000));
            using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
            {
                var window = saved.RootElement.GetProperty("Windows")[0];
                Require(window.GetProperty("Segments").GetArrayLength() == 1);
                Require(window.GetProperty("Anchor").ValueKind == System.Text.Json.JsonValueKind.Null);
                Require(window.GetProperty("CheckpointGeneration").ValueKind == System.Text.Json.JsonValueKind.Null);
            }
            estimator = new QuotaTokenEstimator(path);
            estimator.Observe(Snapshot(3, 96), CheckpointUsage(3, 2000));
            Require(estimator.Get(Window(96), Start.AddMinutes(3)).Segments == 1);
            Require(estimator.Get(Window(96), Start.AddMinutes(3)).PendingDrop == 0);
        }));
        Check("a mismatched checkpoint is rejected without losing the pending point", () =>
        {
            var estimator = new QuotaTokenEstimator();
            estimator.Observe(Snapshot(0, 100), CheckpointUsage(0, 0));
            estimator.Observe(Snapshot(1, 99), CheckpointUsage(1, 500));
            var mismatched = Usage(2, 1000) with { Checkpoint = CheckpointUsage(1, 500).Checkpoint };
            estimator.Observe(Snapshot(2, 98), mismatched);
            var held = estimator.Get(Window(98), Start.AddMinutes(2));
            Require(held.State == EstimateState.Incomplete && held.PendingDrop == 1 && held.Segments == 0);
            estimator.Observe(Snapshot(3, 98), CheckpointUsage(3, 1000));
            Require(estimator.Get(Window(98), Start.AddMinutes(3)).Segments == 1);
        });
        Check("corrupt checkpoint metadata preserves closed history and idle recovery", () => WithStorage(path =>
        {
            var estimator = new QuotaTokenEstimator(path);
            for (int i = 0; i <= 3; i++) estimator.Observe(Snapshot(i, 100 - 2 * i), CheckpointUsage(i, 1000 * i));
            estimator.Observe(Snapshot(4, 93), CheckpointUsage(4, 3500));
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["LedgerCheckpoint"]!["Checksum"] = new string('0', 64);
            File.WriteAllText(path, json.ToJsonString());
            estimator = new QuotaTokenEstimator(path);
            Require(estimator.RestoredLedgerCheckpoint is null);
            estimator.Observe(Snapshot(5, 93), CheckpointUsage(5, 0, "restarted", "fresh-epoch"));
            var held = estimator.Get(Window(93), Start.AddMinutes(5));
            Require(held.Segments == 3 && held.PendingDrop == 0);
            Require(held.Diagnostics.Any(e => e.Kind == "fragment-isolated" && e.Reason.Contains("检查点")));
            estimator.Observe(Snapshot(6, 93), CheckpointUsage(6, 0, "restarted", "fresh-epoch"));
            Near(estimator.Get(Window(93), Start.AddMinutes(6)).RemainingTokens, 46500);
        }));
        Check("V2 closed segments migrate without inventing an old anchor", () => WithStorage(path =>
        {
            Trained(path);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["Version"] = 2;
            json.AsObject().Remove("LedgerCheckpoint");
            var window = json["Windows"]![0]!.AsObject();
            foreach (string property in new[] { "IdentityHash", "AcceptedResetAt", "MetadataResetAt", "Anchor", "LastCounts", "EpochHash",
                "CheckpointGeneration", "CheckpointSequence", "CycleId", "Diagnostics", "Isolated", "Candidate" }) window.Remove(property);
            File.WriteAllText(path, json.ToJsonString());
            var estimator = new QuotaTokenEstimator(path);
            Observe(estimator, 4, 94, 0); Observe(estimator, 5, 94, 0);
            var restored = estimator.Get(Window(94), Start.AddMinutes(5));
            Require(restored.Segments == 3 && restored.RecoveryRequired == 0);
            Require(restored.Diagnostics.Any(e => e.Kind == "history-restored" && e.Reason.Contains("旧版")));
            Near(restored.RemainingTokens, 47000);
        }));
        Check("malformed version types cannot crash loading", () => WithStorage(path =>
        {
            foreach (var version in new[] { "\"3\"", "{}", "[]", "null", "3.5" })
            {
                File.WriteAllText(path, "{\"Version\":" + version + ",\"Windows\":[]}");
                var estimator = new QuotaTokenEstimator(path);
                Observe(estimator, 0, 100, 0);
                Require(estimator.Get(Window(100), Start).Segments == 0);
            }
        }));
        Check("expired restored history returns to initial calibration instead of recovery", () => WithStorage(path =>
        {
            Trained(path);
            var estimator = new QuotaTokenEstimator(path);
            // Get prunes a loaded history after its first valid restore.
            Observe(estimator, 4, 94, 0);
            var old = estimator.Get(Window(94), Start.AddHours(25));
            Require(old.Segments == 0 && old.RecoveryRequired == 0 && old.RecoverySamples == 0);
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
    private static UsageLedgerSnapshot CheckpointUsage(int minute, long total, string generation = "fixture-generation", string epoch = "epoch-A")
    {
        var snapshot = Usage(minute, total) with { Epoch = epoch };
        var checkpoint = new UsageLedgerCheckpoint(epoch, snapshot.Counts, snapshot.ObservedAt, generation, minute + 1)
        { HomeHash = new string('A', 64), SchemaHash = new string('B', 64), Started = Start }.Seal();
        return snapshot with { Checkpoint = checkpoint };
    }
    private static TokenCounts Counts(long total)
    {
        var output = total / 5;
        var input = total - output;
        return new(input, input / 2, 0, output, 0, total);
    }
    private static void Observe(QuotaTokenEstimator estimator, int minute, double remaining, long total) => estimator.Observe(Snapshot(minute, remaining), Usage(minute, total));
    private static void Near(double? value, double expected) => Require(value is not null && Math.Abs(value.Value - expected) <= Math.Max(.000001, Math.Abs(expected) * 1e-10));
    private static void Require(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression("condition")] string? expression = null)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + expression);
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
