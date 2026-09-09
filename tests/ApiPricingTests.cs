using CodexHud.Core;

public static class ApiPricingTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool condition, string scenario)
        {
            if (condition) return;
            failures++;
            Console.Error.WriteLine("FAIL API pricing: " + scenario);
        }
        void Exact(TokenCostResult actual, decimal expected, string scenario)
        {
            Check(actual.IsPriced && actual.MinimumUsd == expected && actual.MaximumUsd == expected,
                scenario + $" (expected {expected}; got {actual.MinimumUsd}–{actual.MaximumUsd})");
        }

        // Independent published examples: each row states the public USD-per-million prices,
        // while the samples contain 100K tokens to remain below long-context thresholds.
        var prices = new (string Model, decimal Input, decimal Cached, decimal Write, decimal Output, decimal Fast)[]
        {
            ("gpt-6-astra", 10m, 1m, 12.5m, 50m, 2m),
            ("gpt-5.6-sol", 4m, .4m, 5m, 20m, 2m),
            ("gpt-5.6-terra", 2m, .2m, 2.5m, 12m, 2m),
            ("gpt-5.6-luna", .2m, .02m, .25m, 1.2m, 2m),
            ("gpt-5.5", 5m, .5m, 5m, 30m, 2.5m),
            ("gpt-5.4-mini", .75m, .075m, .75m, 4.5m, 2m),
            ("gpt-5.4", 2.5m, .25m, 2.5m, 15m, 2m),
            ("gpt-5.3-codex", 1.75m, .175m, 1.75m, 14m, 2m)
        };
        foreach (var price in prices)
        {
            Exact(ApiPricingCatalog.Price(Counts(100_000), price.Model, "default"), price.Input / 10m,
                price.Model + " Standard ordinary input");
            Exact(ApiPricingCatalog.Price(Counts(100_000, cache: 100_000), price.Model, "standard"), price.Cached / 10m,
                price.Model + " cached input replaces ordinary input");
            Exact(ApiPricingCatalog.Price(Counts(100_000, write: 100_000), price.Model, "default"), price.Write / 10m,
                price.Model + " cache write replaces ordinary input without double-counting");
            Exact(ApiPricingCatalog.Price(Counts(0, 100_000, reasoning: 90_000), price.Model, "default"), price.Output / 10m,
                price.Model + " reasoning is already included in output");
            var mixed = Counts(100_000, 10_000, 50_000, 25_000, 9_000);
            decimal standard = (.025m * price.Input + .05m * price.Cached
                + .025m * price.Write + .01m * price.Output);
            Exact(ApiPricingCatalog.Price(mixed, price.Model, "fast"), standard * price.Fast,
                price.Model + " Fast multiplier includes input cache and output");
            Exact(ApiPricingCatalog.Price(mixed, price.Model, "priority"), standard * price.Fast,
                price.Model + " Priority and Fast identify the same API price");
        }

        foreach (var model in new[] { "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.4" })
        {
            var price = prices.Single(item => item.Model == model);
            Exact(ApiPricingCatalog.Price(Counts(272_000, 1_000, 100_000, 10_000), model, "default"),
                .162m * price.Input + .1m * price.Cached + .01m * price.Write + .001m * price.Output,
                model + " exactly 272K remains short context");
            decimal longPrice = (.162001m * price.Input + .1m * price.Cached + .01m * price.Write) * 2m
                + .001m * price.Output * 1.5m;
            Exact(ApiPricingCatalog.Price(Counts(272_001, 1_000, 100_000, 10_000), model, "default"), longPrice,
                model + " 272001 uses long-context rates for the whole request");
            if (model != "gpt-5.4")
                Exact(ApiPricingCatalog.Price(Counts(272_001, 1_000, 100_000, 10_000), model, "fast"),
                    longPrice * 2m, model + " long-context and Fast multipliers stack");
        }

        var uncertainWrite = ApiPricingCatalog.Price(Counts(100_000, 1_000, 60_000, null), "gpt-6-astra", "default");
        Check(uncertainWrite.MinimumUsd == .51m && uncertainWrite.MaximumUsd == .61m
            && uncertainWrite.IsPartial && uncertainWrite.Notes.Count > 0,
            "missing new-model cache writes produce a bounded token-only estimate");
        var knownWrite = ApiPricingCatalog.Price(Counts(100_000, 1_000, 60_000, 10_000), "gpt-6-astra", "default");
        Exact(knownWrite, .535m, "known cache writes use only the 25 percent premium over ordinary input");
        Check(!knownWrite.IsPartial, "known cache write count is not partial");
        var allCached = ApiPricingCatalog.Price(Counts(100_000, cache: 100_000, write: null), "gpt-6-astra", "default");
        Exact(allCached, .1m, "fully cached input leaves no unknown possible write charge");
        Check(!allCached.IsPartial, "irrelevant absent write field does not make fully cached input partial");
        Exact(ApiPricingCatalog.Price(Counts(100_000, 1_000, 60_000, null), "gpt-5.5", "default"), .26m,
            "legacy missing cache writes incur no additional fee");

        var assumedTier = ApiPricingCatalog.Price(Counts(100_000), "gpt-6-astra", null);
        Exact(assumedTier, 1m, "absent service tier uses the stated Standard assumption");
        Check(!assumedTier.IsPartial && assumedTier.Notes.Any(note => note.Contains("Standard")),
            "absent tier is annotated without being made partial solely by the assumption");
        var long55 = ApiPricingCatalog.Price(Counts(273_000, 1_000), "gpt-5.5", "default");
        Exact(long55, 2.775m, "GPT-5.5 long context produces a per-request estimate");
        Check(long55.IsPartial && long55.Notes.Any(note => note.Contains("歧义")),
            "GPT-5.5 long-context billing scope assumption remains explicit");
        var fallback54 = ApiPricingCatalog.Price(Counts(273_000, 1_000), "gpt-5.4", "fast");
        Exact(fallback54, 1.3875m, "GPT-5.4 long Fast request automatically falls back to Standard long rates");
        Check(fallback54.Notes.Any(note => note.Contains("回退")), "GPT-5.4 automatic fallback is explained");
        foreach (var model in new[] { "gpt-5.5", "gpt-5.4-mini" })
            Check(!ApiPricingCatalog.Price(Counts(272_001), model, "fast").IsPriced,
                model + " unsupported Fast long-context combination remains unpriced");
        Exact(ApiPricingCatalog.Price(Counts(300_000, 1_000), "gpt-5.4-mini", "default"), .2295m,
            "GPT-5.4-mini does not inherit flagship Standard long-context surcharge");
        Exact(ApiPricingCatalog.Price(Counts(300_000, 1_000), "gpt-5.3-codex", "priority"), 1.078m,
            "GPT-5.3-codex preserves published 3.50/0.35/28 Priority rates");

        foreach (var alias in new[] { "gpt-5.6", "gpt-5.5-2026-04-23", "gpt-5.4-2026-03-05" })
            Check(ApiPricingCatalog.Price(Counts(100), alias, "default").IsPriced, "documented alias is priced: " + alias);
        foreach (var unknown in new[] { null, "", "gpt-5.3-codex-spark", "gpt-5.6-sol-unknown", "gpt-6", "other/gpt-6-astra" })
        {
            var result = ApiPricingCatalog.Price(Counts(0), unknown, "default");
            Check(!result.IsPriced && result.IsPartial && result.MaximumUsd is null && result.UnpricedReason is not null,
                "unknown model is not treated as free or approximately matched: " + unknown);
        }
        foreach (var tier in new[] { "", "auto", "flex", "batch", "scale", "unknown" })
            Check(!ApiPricingCatalog.Price(Counts(100), "gpt-6-astra", tier).IsPriced,
                "unknown or out-of-scope explicit tier is not silently Standard: " + tier);
        Check(!ApiPricingCatalog.Price(Counts(100), "gpt-6-astra", "default", "azure").IsPriced,
            "third-party provider cannot borrow direct OpenAI API prices");
        Check(!ApiPricingCatalog.Price(Counts(100), "gpt-6-astra", "default", "").IsPriced,
            "empty explicit provider is unpriced");
        Exact(ApiPricingCatalog.Price(Counts(100), "gpt-6-astra", "default", "OpenAI"), .001m,
            "explicit OpenAI provider is accepted");

        TokenCounts[] invalid =
        [
            new(null, 0, 0, 1, 0, null), new(1, null, 0, 1, 0, 2), new(1, 0, 0, null, 0, null),
            new(-1, 0, 0, 1, 0, 0), new(1, -1, 0, 1, 0, 2), new(1, 0, -1, 1, 0, 2),
            new(1, 0, 0, -1, 0, 0), new(1, 0, 0, 1, -1, 2), new(1, 0, 0, 1, 0, -1),
            new(10, 11, 0, 1, 0, 11), new(10, 9, 2, 1, 0, 11), new(1, 0, 0, 1, 2, 2),
            new(1, 0, 0, 1, 0, 3), new(long.MaxValue, 0, 0, 1, 0, null)
        ];
        foreach (var counts in invalid)
            Check(!ApiPricingCatalog.Price(counts, "gpt-6-astra", "default").IsPriced,
                "invalid or required missing token counts remain unpriced: " + counts);
        Exact(ApiPricingCatalog.Price(new(100_000, 0, 0, 10_000, null, null), "gpt-6-astra", "default"), 1.5m,
            "optional reasoning and total counts are not needed for pricing");
        var zero = ApiPricingCatalog.Price(Counts(0, write: null), "gpt-6-astra", "default");
        Exact(zero, 0m, "recorded true zero is distinct from missing usage");
        Check(!zero.IsPartial, "true zero is complete when no write charge is possible");
        var huge = new TokenCounts(long.MaxValue - 1, 0, 0, 1, 1, long.MaxValue);
        Check(ApiPricingCatalog.Price(huge, "gpt-6-astra", "fast").IsPriced,
            "large valid counts are priced using decimal without long multiplication overflow");

        var estimate = new SessionCostEstimate("root")
        {
            SelfUsd = 1.2m, SelfUpperUsd = 1.5m, DescendantsUsd = 2.3m, DescendantsUpperUsd = 2.7m,
            PricedResponses = 3, UnpricedResponses = 1, UnpricedTokens = 100,
            DescendantCount = 2, Health = SampleHealth.Partial
        };
        Check(estimate.TotalUsd == 3.5m && estimate.TotalUpperUsd == 4.2m && estimate.Notes.Count == 0,
            "session DTO retains own and descendant amounts and computes aggregate bounds");
        Check(ApiPricingCatalog.Version.Contains("2026-09-08") && ApiPricingCatalog.VerifiedOn == new DateOnly(2026, 9, 8)
            && ApiPricingCatalog.Sources.Contains("https://developers.openai.com/api/docs/pricing"),
            "price catalog retains its verification date and primary pricing URL");
        return failures;
    }

    private static TokenCounts Counts(long input, long output = 0, long cache = 0, long? write = 0, long reasoning = 0)
        => new(input, cache, write, output, reasoning, checked(input + output));
}
