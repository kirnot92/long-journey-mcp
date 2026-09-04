using LongJourney.Core;
using LongJourney.Server;

var reindex = false;
var hostArguments = new List<string>();
foreach (var argument in args)
{
    if (argument == "--reindex")
    {
        reindex = true;
    }
    else
    {
        hostArguments.Add(argument);
    }
}

await using var app = AppHost.Build(hostArguments.ToArray());
if (reindex)
{
    await RecoverSourcesAndReindexAsync(app);
}
else
{
    await app.RunAsync();
}

static async Task RecoverSourcesAndReindexAsync(WebApplication app)
{
    var engine = app.Services.GetRequiredService<MemoryEngine>();
    var failures = await engine.ResumePendingAsync();
    SourceRecoveryLog.WriteIfIncomplete(app.Logger, failures);

    var search = app.Services.GetRequiredService<MemorySearch>();
    await search.ReindexAsync(new CallContext(), CancellationToken.None);
    app.Logger.LogInformation("Embedding reindex completed for the configured model space.");
}
