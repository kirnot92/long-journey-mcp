using LongJourney.Core;
using LongJourney.Server;
using Microsoft.Extensions.DependencyInjection;

namespace LongJourney.Tests;

public sealed partial class ServerTests
{
    [Fact]
    public async Task ServerStartsActivityCoverageBeforeFirstUserCall()
    {
        using var fixture = new ConsolidationFixture();
        Assert.Null(fixture.Store.GetState("activity.started_at"));
        await using var app = AppHost.Build(["--no-scheduler"], builder =>
        {
            RunningHost.Configure(builder, fixture.Options.DataDirectory, new CannedCognition());
            builder.Services.AddSingleton<TimeProvider>(fixture.Clock);
        });
        var store = app.Services.GetRequiredService<IMemoryStore>();
        Assert.Equal(fixture.Clock.GetUtcNow(), DateTimeOffset.Parse(store.GetState("activity.started_at")!));
        Assert.Empty(store.ReadSnapshot().Memories);
        Assert.Empty(store.GetRuns());
    }
}
