using LongJourney.Core;
using LongJourney.Server;
using Microsoft.Data.Sqlite;

namespace LongJourney.Tests;

public sealed class DailyReportCommandTests
{
    [Fact]
    public void EnvironmentSpecificConfigurationAndContentRootMatchTheServer()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Clock.Now = new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.Zero);
        using (var activity = ActivityScope.Begin(fixture.Store, "remember", "agent", fixture.Clock.GetUtcNow(),
            new { raw_characters = 0, raw_bytes = 0 }))
        {
            activity.Complete(fixture.Clock.GetUtcNow());
        }
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "appsettings.ReportTest.json"),
            """{"Engine":{"DataDirectory":".","TimeZoneId":"UTC"}}""");
        fixture.Clock.Now = fixture.Clock.Now.AddDays(2);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = DailyReportCommand.Run([
            "--daily-report=2026-09-04", "--contentRoot", fixture.Options.DataDirectory,
            "--environment", "ReportTest"], output, error, fixture.Clock);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        var path = Path.Combine(fixture.Options.DataDirectory, "reports", "daily", "2026-09-04.json");
        using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("UTC", report.RootElement.GetProperty("time_zone_id").GetString());
        Assert.Single(report.RootElement.GetProperty("operations").EnumerateArray());
    }

    [Fact]
    public void ReportOnlyCommandDoesNotRecoverPendingSourcesOrRequireApiAccess()
    {
        using var fixture = new ConsolidationFixture();
        var source = fixture.Store.SaveSource("Keep this pending experience.", fixture.Clock.GetUtcNow());
        Assert.True(fixture.Store.ClaimSource(source.Source.Id));
        using (var activity = ActivityScope.Begin(fixture.Store, "remember", "agent",
            fixture.Clock.GetUtcNow(), new { raw_characters = 29, raw_bytes = 29 }, source.Source.Id))
        {
            activity.Complete(fixture.Clock.GetUtcNow());
        }
        var before = DatabaseCounts(fixture.Store.DatabasePath);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(2);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = DailyReportCommand.Run([
            "--daily-report", "2026-09-04..2026-09-05",
            "--Engine:DataDirectory=" + fixture.Options.DataDirectory,
            "--Engine:TimeZoneId=UTC"], output, error, fixture.Clock);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        Assert.Equal(before, DatabaseCounts(fixture.Store.DatabasePath));
        Assert.Equal("processing", fixture.Store.ReadSource(source.Source.Id).Source.Status);
        Assert.True(File.Exists(Path.Combine(fixture.Options.DataDirectory, "reports", "daily", "2026-09-04.json")));
        Assert.Contains("2026-09-05.md", output.ToString());
    }

    [Fact]
    public void MissingCorpusAndInvalidArgumentsDoNotInitializeDataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "long-journey-report-absent-" + Guid.NewGuid().ToString("N"));
        foreach (var arguments in new[]
        {
            new[] { "--daily-report", "2026-09-04", "--Engine:DataDirectory=" + directory },
            new[] { "--daily-report", "2026-09-05..2026-09-04" },
            new[] { "--daily-report", "2026-09-04", "--reindex" },
            new[] { "--daily-report" }
        })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(2, DailyReportCommand.Run(arguments, output, error));
            Assert.NotEmpty(error.ToString());
            Assert.False(Directory.Exists(directory));
        }
    }

    private static string DatabaseCounts(string path)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM sources) || ':' || (SELECT COUNT(*) FROM memories) || ':' ||
                (SELECT COUNT(*) FROM api_calls) || ':' || (SELECT COUNT(*) FROM runs) || ':' ||
                (SELECT COUNT(*) FROM activity_operations)
            """;
        return (string)command.ExecuteScalar()!;
    }
}
