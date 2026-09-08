using System.Text.Json;
using CodexHud.Core;

public static class QuotaTests
{
    public static int Run()
    {
        var failures = 0;
        Check("multi-pool supersedes legacy and defaults to Codex", () =>
        {
            var sample = Parse("""
                {"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{
                  "model_b":{"limitName":"Other model","primary":{"usedPercent":42,"windowDurationMins":60,"resetsAt":1730950800}},
                  "codex":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1730947200},"secondary":{"usedPercent":15,"windowDurationMins":10080,"resetsAt":1731000000}}
                }}
                """);
            Require(sample.Windows.Count == 3 && sample.Health == SampleHealth.Fresh);
            Require(QuotaSelection.GetDefault(sample.Windows)?.RemainingPercent == 75);
            Require(sample.Windows[0].Key == "codex/primary/300");
            Require(sample.Windows[0].Label == "Codex · 5 小时");
            Require(sample.Windows[1].Label == "Codex · 7 天");
            Require(sample.Windows[2].Label == "Other model · 1 小时");
            Require(sample.Windows[0].ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1730947200));
        });
        Check("zero and hundred remain actual boundaries", () =>
        {
            var sample = Parse("""{"rateLimits":{"primary":{"usedPercent":100,"windowDurationMins":5},"secondary":{"usedPercent":0,"windowDurationMins":10}}} """);
            Require(sample.Windows[0].RemainingPercent == 0 && sample.Windows[1].RemainingPercent == 100);
        });
        Check("out-of-range usage is clamped", () =>
        {
            var sample = Parse("""{"rateLimits":{"primary":{"usedPercent":115},"secondary":{"usedPercent":-12}}} """);
            Require(sample.Windows[0].RemainingPercent == 0 && sample.Windows[1].RemainingPercent == 100);
        });
        Check("unknown percent does not become zero", () =>
        {
            var sample = Parse("""{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":300},"secondary":{"windowDurationMins":10080}}} """);
            Require(sample.Health == SampleHealth.Unavailable);
            Require(sample.Windows.Count == 2 && sample.Windows.All(x => x.RemainingPercent is null));
            Require(QuotaSelection.GetDefault(sample.Windows) is null);
        });
        Check("null pool/slots are absent and invalid fields stay unknown", () =>
        {
            var sample = Parse("""{"rateLimitsByLimitId":{"empty":null,"codex":{"primary":{"usedPercent":"12","windowDurationMins":0,"resetsAt":9223372036854775807},"secondary":null}}} """);
            Require(sample.Windows.Count == 1 && sample.Windows[0].RemainingPercent is null);
            Require(sample.Windows[0].WindowMinutes is null && sample.Windows[0].ResetsAt is null);
            Require(sample.Windows[0].Key == "codex/primary/unknown");
        });
        Check("null multi-view falls back to legacy", () =>
        {
            var sample = Parse("""{"rateLimitsByLimitId":null,"rateLimits":{"limitId":"legacy","primary":{"usedPercent":1,"windowDurationMins":15}}} """);
            Require(sample.Windows.Count == 1 && sample.Windows[0].RemainingPercent == 99);
            Require(sample.Windows[0].LimitId == "legacy");
        });
        Check("empty authoritative multi-view does not resurrect legacy", () =>
        {
            var sample = Parse("""{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":1}}} """);
            Require(sample.Windows.Count == 0 && sample.Health == SampleHealth.Unavailable);
        });
        Check("complete JSON-RPC response and fractional period", () =>
        {
            var sample = Parse("""{"id":3,"result":{"rateLimits":{"primary":{"usedPercent":25.5,"windowDurationMins":90}}}} """);
            Require(sample.Windows[0].RemainingPercent == 74.5 && sample.Windows[0].Label == "Codex · 90 分钟");
        });
        Check("partial fields preserve independently available readings", () =>
        {
            var sample = Parse("""{"rateLimits":{"primary":{"usedPercent":12,"windowDurationMins":300},"secondary":{"usedPercent":null,"windowDurationMins":10080}}} """);
            Require(sample.Health == SampleHealth.Partial && sample.Windows[0].RemainingPercent == 88);
            Require(sample.Windows[1].RemainingPercent is null);
        });
        Check("selection is sticky and missing key requires user choice", () =>
        {
            var sample = Parse("""{"rateLimits":{"primary":{"usedPercent":12,"windowDurationMins":300},"secondary":{"usedPercent":40,"windowDurationMins":10080}}} """);
            Require(QuotaSelection.ResolveSelected(sample.Windows, "codex/secondary/10080")?.RemainingPercent == 60);
            Require(QuotaSelection.ResolveSelected(sample.Windows, "codex/primary/60") is null);
            Require(QuotaSelection.ResolveSelected(sample.Windows, null)?.RemainingPercent == 88);
        });
        Check("default prefers a valid primary Codex window", () =>
        {
            var sample = Parse("""{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":300},"secondary":{"usedPercent":40,"windowDurationMins":10080}}} """);
            Require(QuotaSelection.GetDefault(sample.Windows)?.Slot == "secondary");
        });
        Check("changed period produces a new selection key", () =>
        {
            var oldWindow = Parse("""{"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":300}}} """).Windows[0];
            var newSample = Parse("""{"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":60}}} """);
            Require(QuotaSelection.ResolveSelected(newSample.Windows, oldWindow.Key) is null);
        });
        Check("future fields are ignored and malformed response is rejected", () =>
        {
            var sample = Parse("""{"futureField":{},"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":60,"futureField":{}}}} """);
            Require(sample.Windows.Count == 1);
            try { Parse("{}"); throw new InvalidOperationException("Should reject missing quota fields."); }
            catch (JsonException) { }
        });
        return failures;

        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine($"PASS quota: {name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL quota: {name}: {ex.Message}"); }
        }
    }

    private static QuotaSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return QuotaProvider.Parse(document.RootElement, DateTimeOffset.UnixEpoch);
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }
}
