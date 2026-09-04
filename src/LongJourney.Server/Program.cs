using LongJourney.Core;
using LongJourney.Server;

var reindex = args.Contains("--reindex", StringComparer.Ordinal);
await using var app = AppHost.Build(args.Where(x => x != "--reindex").ToArray());
if (reindex)
{
    var failures = await app.Services.GetRequiredService<MemoryEngine>().ResumePendingAsync();
    if (failures.Count > 0)
        app.Logger.LogWarning("Source recovery left {FailureCount} inputs for retry ({ErrorTypes}); reindexing existing memories.",
            failures.Count, string.Join(", ", failures.Select(x => x.ErrorType).Distinct(StringComparer.Ordinal)));
    await app.Services.GetRequiredService<MemorySearch>().ReindexAsync(new CallContext(), CancellationToken.None);
    app.Logger.LogInformation("Embedding reindex completed for the configured model space.");
}
else await app.RunAsync();
