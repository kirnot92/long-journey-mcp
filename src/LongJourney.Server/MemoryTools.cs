using System.ComponentModel;
using LongJourney.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LongJourney.Server;

[McpServerToolType]
public sealed class MemoryTools(MemoryEngine engine, ILogger<MemoryTools> logger)
{
    [McpServerTool(Name = "remember", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Preserve one observation-sized raw input in the shared memory corpus. Exact duplicate raw input is not stored again. Created time is assigned internally; no speaker or project metadata is required.")]
    public Task<RememberResult> RememberAsync(
        [Description("The unmodified text of one observation to remember.")] string raw,
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
