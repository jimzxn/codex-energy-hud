if (args.FirstOrDefault() == "app-server") return QuotaTransportTests.RunFixture();
if (args.FirstOrDefault() == "--live-tokens") return await TokenUsageTests.LiveAsync();
if (args.FirstOrDefault() == "--live-ledger") return await UsageLedgerTests.LiveAsync();
if (args.FirstOrDefault() == "--live-desktop") return await DesktopActivityTests.LiveAsync();
var failures = QuotaTests.Run() + ActivityTests.Run() + HardwareTests.Run() + DiskTests.Run() + TokenUsageTests.Run()
    + UsageLedgerTests.Run() + QuotaEstimateTests.Run() + TaskNoticeTests.Run() + DesktopActivityTests.Run();
failures += await QuotaTransportTests.RunAsync();
Console.WriteLine($"TOTAL: {failures} failure(s)");
return failures == 0 ? 0 : 1;
