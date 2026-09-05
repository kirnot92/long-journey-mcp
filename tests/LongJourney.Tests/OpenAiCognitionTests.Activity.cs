using System.Text.Json;
using LongJourney.Core;
using LongJourney.OpenAI;
using Microsoft.Data.Sqlite;

namespace LongJourney.Tests;

public sealed partial class OpenAiCognitionTests
{
    [Fact]
    public async Task ConcurrentApiCallsRetainTheirActivityAndReasoningSettings()
    {
        using var fixture = new ConsolidationFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        using var handler = new Handler(async _ =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                release.SetResult();
            }
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Response("""{"observations":[]}""");
        });
        using var http = new HttpClient(handler);

        async Task<string> ExtractAsync(string reasoning)
        {
            using var activity = ActivityScope.Begin(fixture.Store, "extraction", "agent",
                fixture.Clock.GetUtcNow(), new { settings = fixture.Options });
            var id = ActivityScope.CurrentId!;
            var configuration = new OpenAiOptions { Remember = new ModelOptions { ReasoningEffort = reasoning } };
            var cognition = new OpenAiCognition(http, configuration, fixture.Options, fixture.Store,
                fixture.Clock, () => "test-key");
            await cognition.ExtractAsync("A remembered experience.", new CallContext(), default);
            activity.Complete(fixture.Clock.GetUtcNow());
            return id;
        }

        var low = ExtractAsync("low");
        var high = ExtractAsync("high");
        await Task.WhenAll(low, high);
        Assert.Null(ActivityScope.CurrentId);
        Assert.Null(ActivityScope.ApiSettingsJson);

        using var db = new SqliteConnection($"Data Source={fixture.Store.DatabasePath};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT a.activity_id,a.settings_json,c.actual_usd FROM activity_api_calls a
            JOIN api_calls c ON c.id=a.api_call_id ORDER BY a.activity_id
            """;
        using var reader = command.ExecuteReader();
        var settings = new Dictionary<string, string>();
        while (reader.Read())
        {
            using var json = JsonDocument.Parse(reader.GetString(1));
            settings.Add(reader.GetString(0), json.RootElement.GetProperty("reasoning_effort").GetString()!);
            Assert.False(reader.IsDBNull(2));
        }
        Assert.Equal(2, settings.Count);
        Assert.Equal("low", settings[await low]);
        Assert.Equal("high", settings[await high]);
    }
}
