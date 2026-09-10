if (args.FirstOrDefault() == "--live-billing") return await LiveBillingCheck.RunAsync(args);
if (args.FirstOrDefault() == "app-server") return QuotaTransportTests.RunFixture();
if (args.FirstOrDefault() == "--live-tokens") return await TokenUsageTests.LiveAsync();
if (args.FirstOrDefault() == "--live-ledger") return await UsageLedgerTests.LiveAsync();
if (args.FirstOrDefault() == "--live-workload") return await WorkloadUsageTests.LiveAsync(args);
if (args.FirstOrDefault() == "--live-desktop") return await DesktopActivityTests.LiveAsync();
var failures = BillingCycleTests.Run() + PeriodUsageTests.Run() + ApiPricingTests.Run() + SessionCostTests.Run() + QuotaTests.Run() + ActivityTests.Run() + HardwareTests.Run() + DiskTests.Run() + TokenUsageTests.Run()
    + UsageLedgerTests.Run() + WorkloadUsageTests.Run() + UsageSmoothingTests.Run() + QuotaEstimateTests.Run() + TaskNoticeTests.Run() + DesktopActivityTests.Run();
failures += await QuotaTransportTests.RunAsync();
Console.WriteLine($"TOTAL: {failures} failure(s)");
return failures == 0 ? 0 : 1;
