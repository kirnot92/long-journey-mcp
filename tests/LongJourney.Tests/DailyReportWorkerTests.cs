using LongJourney.Core;
using LongJourney.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace LongJourney.Tests;

public sealed class DailyReportWorkerTests
{
    [Fact]
    public void ActivatedButIdleCorpusProducesZeroActivityReport()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Store.ActivateActivityRecording(fixture.Clock.GetUtcNow());
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        var exports = new DailyReportService(fixture.Options.DataDirectory, "UTC", fixture.Clock).ExportClosedDays();
        var exported = Assert.Single(exports);
        using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(exported.JsonPath));
        Assert.Empty(report.RootElement.GetProperty("operations").EnumerateArray());
        Assert.NotEqual("unknown_legacy", report.RootElement.GetProperty("coverage").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReportsRunWithCognitiveSchedulerDisabled()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.SchedulerEnabled = false;
        using (var activity = ActivityScope.Begin(fixture.Store, "remember", "agent",
            fixture.Clock.GetUtcNow(), new { raw_characters = 0, raw_bytes = 0 }))
        {
            activity.Complete(fixture.Clock.GetUtcNow());
        }
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        var reports = new DailyReportService(fixture.Options.DataDirectory, "UTC", fixture.Clock);
        using var worker = new DailyReportWorker(reports, fixture.Options, fixture.Clock,
            NullLogger<DailyReportWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            var path = Path.Combine(fixture.Options.DataDirectory, "reports", "daily", "2026-09-04.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(path))
            {
                await Task.Delay(20, timeout.Token);
            }
            Assert.Empty(fixture.Store.GetRuns());
            Assert.Empty(fixture.Store.ReadSnapshot().Memories);
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    [Fact]
    public async Task DisabledExportsPreserveRecordedActivityWithoutCreatingReports()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.DailyReportsEnabled = false;
        using (var activity = ActivityScope.Begin(fixture.Store, "recall", "agent",
            fixture.Clock.GetUtcNow(), new { query = "empty", candidate_ids = Array.Empty<string>(), returned_ids = Array.Empty<string>() }))
        {
            activity.Complete(fixture.Clock.GetUtcNow());
        }
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        using var worker = new DailyReportWorker(
            new DailyReportService(fixture.Options.DataDirectory, "UTC", fixture.Clock),
            fixture.Options, fixture.Clock, NullLogger<DailyReportWorker>.Instance);
        await worker.StartAsync(default);
        await worker.StopAsync(default);
        Assert.False(Directory.Exists(Path.Combine(fixture.Options.DataDirectory, "reports")));
    }
}
