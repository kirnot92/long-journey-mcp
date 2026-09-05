using System.ComponentModel;
using LongJourney.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LongJourney.Server;

[McpServerToolType]
public sealed class MemoryTools(MemoryEngine engine, ILogger<MemoryTools> logger)
{
    private const string RememberDescription = """
        Remember one coherent experience that may be useful in future sessions.
        Call when an explicit preference or constraint, consequential decision, observed outcome, or correction is clear enough to preserve.
        Record meaningful developments rather than every message or tool result. Before a context reset or session end, check for useful experiences not yet saved.
        Keep raw focused, usually a few sentences or short paragraphs. Include selected source material and enough factual context to understand what happened.
        Preserve important wording, conditions, uncertainty, and outcomes; distinguish plans, decisions, and completed actions. Separate quoted material from added factual context.
        Record separate experiences separately, but do not mechanically split one experience to fit the limit.
        Avoid resubmitting recorded material without new evidence. Exact duplicate raw is deduplicated; reworded duplicates are not.
        Source created time is assigned internally when recorded and does not establish when the event happened; include event timing in raw when relevant. No speaker or project metadata is required.
        """;

    internal static IReadOnlyList<McpServerTool> CreateTools(EngineOptions options)
    {
        var tools = new List<McpServerTool>();
        foreach (var methodName in new[] { nameof(RememberAsync), nameof(RecallAsync), nameof(TraceAsync) })
        {
            var description = methodName == nameof(RememberAsync)
                ? $"{RememberDescription}\nCurrent raw limit: {options.MaxRawCharacters} UTF-16 code units. The server extracts 0 to {options.MaxObservations} observations per Source; this cap is not a target."
                : null;
            // Create the target at invocation time so metadata registration does not open the corpus.
            tools.Add(McpServerTool.Create(typeof(MemoryTools).GetMethod(methodName)!,
                request => ActivatorUtilities.CreateInstance<MemoryTools>(request.Services!),
                new McpServerToolCreateOptions
                {
                    Description = description,
                    SerializerOptions = JsonDefaults.Options
                }));
        }
        return tools;
    }

    [McpServerTool(Name = "remember", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description(RememberDescription)]
    public Task<RememberResult> RememberAsync(
        [Description("Selected source material for one coherent experience, with the factual context needed to understand it. Preserve relevant wording, conditions, uncertainty, and outcomes; distinguish plans from completed actions.")] string raw,
        CancellationToken cancellationToken)
    {
        return InvokeToolAsync(() => engine.RememberAsync(raw, cancellationToken));
    }

    [McpServerTool(Name = "recall", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Retrieve relevant memories from the shared corpus. Records recall time without reinforcing truth, confidence, or ranking. Relations are outgoing only.")]
    public Task<RecallResult> RecallAsync(
        [Description("What to search for in memory.")] string query,
        [Description("Optional context for selecting relevant memories.")] string? context = null,
        CancellationToken cancellationToken = default)
    {
        return InvokeToolAsync(() => engine.RecallAsync(query, context, cancellationToken));
    }

    [McpServerTool(Name = "trace", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Follow a memory's immutable derived_from parents down to original raw Sources. Does not traverse reverse positive or negative relations.")]
    public Task<TraceResult> TraceAsync([Description("The exact memory ID to trace.")] string memory_id)
    {
        return InvokeToolAsync(() => Task.FromResult(engine.Trace(memory_id)));
    }

    private async Task<T> InvokeToolAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InputException exception)
        {
            throw new McpException(exception.Message);
        }
        catch (Exception exception)
        {
            // Do not log exception text or attach it to the protocol error: it may contain provider data.
            logger.LogWarning("Memory operation failed ({ErrorType}); persisted work can be retried.", exception.GetType().Name);
            throw new McpException("Memory operation failed; saved Sources and work state are preserved for retry. Check server logs and OpenAI configuration.");
        }
    }
}
