using System.Text.Json;

namespace LongJourney.Core;

/// <summary>Optional persisted instrumentation, separate from the memory graph contract.</summary>
public interface IActivityRecorder
{
    void ActivateActivityRecording(DateTimeOffset now);
    void BeginActivity(string id, string kind, string origin, string? parentId, string? sourceId,
        long? runId, string? workKey, long? chargedRunId, DateTimeOffset now, string detailsJson);
    void UpdateActivity(string id, string detailsJson);
    void FinishActivity(string id, string status, string? errorType, DateTimeOffset now);
    void ApplyActivityRelation(RelationProposal proposal, RunRecord run, string workKey, int index,
        DateTimeOffset now, string? rejectionReason = null);
}

/// <summary>Async-flow-local correlation. Each nested call restores its caller on disposal.</summary>
public sealed class ActivityScope : IDisposable
{
    private static readonly AsyncLocal<ActivityScope?> Current = new();
    private static readonly AsyncLocal<string?> ApiSettings = new();
    private readonly IActivityRecorder? _recorder;
    private readonly ActivityScope? _previous;
    private readonly string _id;
    public static string? CurrentId => Current.Value?._recorder is null ? null : Current.Value._id;
    public static string? ParentId => Current.Value?._previous?._recorder is null ? null : Current.Value._previous._id;
    public static string? ApiSettingsJson => ApiSettings.Value;

    private ActivityScope(IMemoryStore store, string kind, string origin, DateTimeOffset now,
        object details, string? sourceId, long? runId, string? workKey, long? chargedRunId)
    {
        _recorder = store as IActivityRecorder;
        _previous = Current.Value;
        _id = "activity_" + Guid.NewGuid().ToString("N");
        _recorder?.BeginActivity(_id, kind, origin, CurrentId, sourceId, runId, workKey,
            chargedRunId, now, JsonSerializer.Serialize(details, JsonDefaults.Options));
        Current.Value = this;
    }

    public static ActivityScope Begin(IMemoryStore store, string kind, string origin,
        DateTimeOffset now, object details, string? sourceId = null, long? runId = null,
        string? workKey = null, long? chargedRunId = null) =>
        new(store, kind, origin, now, details, sourceId, runId, workKey, chargedRunId);

    public void Update(object details) =>
        _recorder?.UpdateActivity(_id, JsonSerializer.Serialize(details, JsonDefaults.Options));

    public static void UpdateCurrent(object details) => Current.Value?.Update(details);

    public void Complete(DateTimeOffset now) => _recorder?.FinishActivity(_id, "complete", null, now);
    public void Fail(Exception error, DateTimeOffset now) => _recorder?.FinishActivity(_id,
        error is OperationCanceledException ? "cancelled" : "failed", error.GetType().Name, now);
    public void Dispose() => Current.Value = _previous;

    public static IDisposable BeginApiSettings(object settings)
    {
        var previous = ApiSettings.Value;
        ApiSettings.Value = JsonSerializer.Serialize(settings, JsonDefaults.Options);
        return new ApiSettingsScope(previous);
    }

    private sealed class ApiSettingsScope(string? previous) : IDisposable
    {
        public void Dispose() => ApiSettings.Value = previous;
    }
}
