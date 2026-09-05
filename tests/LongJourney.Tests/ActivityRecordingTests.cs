using System.Text;
using System.Text.Json;
using LongJourney.Core;
using Microsoft.Data.Sqlite;

namespace LongJourney.Tests;

public sealed class ActivityRecordingTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RememberSeparatesSubmissionSourceAndExtractionWithExactSizes()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Store.GetState("activity.started_at"));
        const string raw = "한글 😀";
        var first = await fixture.Engine.RememberAsync(raw);
        await fixture.Engine.RememberAsync(raw);
        var calls = fixture.Read("remember");
        Assert.Equal(2, calls.Count);
        Assert.Equal(raw.Length, calls[0].Details.GetProperty("raw_characters").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(raw), calls[0].Details.GetProperty("raw_bytes").GetInt32());
        Assert.True(calls[0].Details.GetProperty("new_source").GetBoolean());
        Assert.False(calls[1].Details.GetProperty("new_source").GetBoolean());
        Assert.Single(calls[0].Details.GetProperty("created_ids").EnumerateArray());
        Assert.Empty(calls[1].Details.GetProperty("created_ids").EnumerateArray());
        Assert.Equal(first.Memories[0].Id, calls[1].Details.GetProperty("returned_ids")[0].GetString());
        var extraction = Assert.Single(fixture.Read("extraction"));
        Assert.Equal(calls[0].Id, extraction.ParentId);
        Assert.Equal(first.SourceId, extraction.SourceId);
        Assert.Equal("complete", extraction.Status);
        Assert.Equal(Day, DateTimeOffset.Parse(fixture.Store.GetState("activity.started_at")!));
        Assert.Null(ActivityScope.CurrentId);
    }

    [Fact]
    public async Task InvalidInputEmptyExtractionAndRecoveryFailureRemainDistinct()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Engine.RememberAsync(null!));
        await Assert.ThrowsAsync<InputException>(() => fixture.Engine.RememberAsync(new string('x', 4001)));
        var empty = await fixture.Engine.RememberAsync(" ");
        Assert.Empty(empty.Memories);
        var missing = fixture.Store.SaveSource("missing", Day);
        File.Delete(Path.Combine(fixture.Options.DataDirectory, missing.Source.RelativePath));
        Assert.Single(await fixture.Engine.ResumePendingAsync());
        var calls = fixture.Read("remember");
        Assert.Equal(3, calls.Count);
        Assert.Equal("failed", calls[0].Status);
        Assert.Equal(JsonValueKind.Null, calls[0].Details.GetProperty("raw_bytes").ValueKind);
        Assert.Null(calls[1].SourceId);
        Assert.Equal("complete", calls[2].Status);
        var attempts = fixture.Read("extraction");
        Assert.Equal(2, attempts.Count);
        Assert.False(attempts[0].Details.GetProperty("model_invoked").GetBoolean());
        Assert.Equal("recovery", attempts[1].Origin);
        Assert.Equal("failed", attempts[1].Status);
        Assert.Equal("FileNotFoundException", attempts[1].ErrorType);
    }

    [Fact]
    public async Task ConcurrentDuplicateAndCancelledExtractionAreRecordedWithoutDoubleCreation()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Cognition.BeforeExtract = async () => { entered.SetResult(); await release.Task; };
        var first = fixture.Engine.RememberAsync("same");
        await entered.Task;
        await fixture.Engine.RememberAsync("same");
        release.SetResult();
        await first;
        var calls = fixture.Read("remember");
        Assert.Equal("processing", calls[1].Details.GetProperty("source_status").GetString());
        Assert.False(calls[1].Details.GetProperty("extraction_performed").GetBoolean());
        Assert.Single(fixture.Read("extraction"));
        fixture.Cognition.BeforeExtract = () => throw new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Engine.RememberAsync("cancel"));
        Assert.Equal("cancelled", fixture.Read("remember")[2].Status);
        Assert.Equal("cancelled", fixture.Read("extraction")[1].Status);
        Assert.Null(ActivityScope.CurrentId);
    }

    [Fact]
    public async Task RecallPreservesCandidateAndReturnedOrderIncludingRepeatedAndEmptyCalls()
    {
        using var fixture = new Fixture();
        await fixture.Engine.RecallAsync("empty corpus");
        await fixture.Engine.RememberAsync("first");
        await fixture.Engine.RememberAsync("second");
        var selected = await fixture.Engine.RecallAsync("query", "context");
        await fixture.Engine.RecallAsync("query", "context");
        fixture.Cognition.EmptySelection = true;
        await fixture.Engine.RecallAsync("no selection");
        var calls = fixture.Read("recall");
        Assert.Equal(4, calls.Count);
        Assert.All(calls, call => Assert.Equal("recall", call.Details.GetProperty("tool").GetString()));
        Assert.Equal("query", calls[1].Details.GetProperty("query").GetString());
        Assert.Equal("context", calls[1].Details.GetProperty("context").GetString());
        Assert.Empty(calls[0].Details.GetProperty("candidate_ids").EnumerateArray());
        var candidates = calls[1].Details.GetProperty("candidate_ids").EnumerateArray().Select(value => value.GetString()).ToArray();
        var returned = calls[1].Details.GetProperty("returned_ids").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(candidates.Reverse(), returned);
        Assert.Equal(selected.Memories.Select(memory => memory.Id), returned);
        Assert.Equal(returned, calls[2].Details.GetProperty("returned_ids").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(2, calls[3].Details.GetProperty("candidate_ids").GetArrayLength());
        Assert.Empty(calls[3].Details.GetProperty("returned_ids").EnumerateArray());
        Assert.Equal(4L, fixture.Scalar("SELECT COUNT(*) FROM recall_events"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("  comparing maintenance approaches\n")]
    public async Task ThinkAndRecallReturnTheSameOrderedMixedDepthMemoriesAndOnlyRecordRecalls(string? context)
    {
        using var fixture = new Fixture();
        var parents = new List<MemoryRecord>();
        foreach (var raw in new[] { "first automation experience", "second automation experience", "third automation experience" })
        {
            parents.AddRange((await fixture.Engine.RememberAsync(raw)).Memories);
        }
        var parentIds = MemoryTestData.Ids(parents);
        var run = fixture.Store.GetOrCreateRun(RunKind.Dream, Day.Date, Day.Date.AddDays(1), Day.AddDays(1), null);
        var abstraction = fixture.Store.AddAbstraction(new AbstractionProposal("automation and user control", parentIds),
            "fake", run, "pattern", 0, parentIds, ConsolidationFixture.Vector, Day.AddDays(1));
        fixture.Store.AddRelation(new RelationProposal(parents[0].Id, parents[1].Id, RelationKind.Negative), run, Day.AddDays(1));
        fixture.Store.FinishRun(run.Id, "complete", Day.AddDays(1));
        var before = fixture.Store.ReadSnapshot();
        fixture.Cognition.EmbeddedTexts.Clear();
        const string topic = "automation and user control";

        fixture.Clock.Now = Day.AddDays(2);
        var recalled = await fixture.Engine.RecallAsync(topic, context);
        Assert.All(recalled.Memories, memory => Assert.Equal(fixture.Clock.Now, memory.LastRecalledAt));
        fixture.Clock.Now = Day.AddDays(3);
        var thought = await fixture.Engine.ThinkAsync(topic, context);

        Assert.Equal(4, thought.Memories.Count);
        Assert.Equal(MemoryTestData.Ids(recalled.Memories), MemoryTestData.Ids(thought.Memories));
        Assert.Contains(thought.Memories, memory => memory.Depth == 0);
        Assert.Contains(thought.Memories, memory => memory.Id == abstraction.Id && memory.Depth == 1);
        Assert.All(thought.Memories, memory => Assert.Equal(fixture.Clock.Now, memory.LastRecalledAt));
        Assert.Equal(new[] { (topic, context), (topic, context) }, fixture.Cognition.Selections);
        Assert.Equal(new[] { topic, topic }, fixture.Cognition.EmbeddedTexts);
        var after = fixture.Store.ReadSnapshot();
        Assert.Equal(MemoryTestData.Ids(before.Memories), MemoryTestData.Ids(after.Memories));
        Assert.Equal(8, after.RecallEvents.Count);
        foreach (var memory in after.Memories)
        {
            var original = before.ById[memory.Id];
            Assert.Equal(original.Content, memory.Content);
            Assert.Equal(original.DerivedFrom, memory.DerivedFrom);
            Assert.Equal(original.Relations, memory.Relations);
            Assert.Equal(original.UniqueSourceRootCount, memory.UniqueSourceRootCount);
            Assert.Equal(fixture.Clock.Now, memory.LastRecalledAt);
        }

        var calls = fixture.Read("recall");
        Assert.Equal(2, calls.Count);
        Assert.Equal("recall", calls[0].Details.GetProperty("tool").GetString());
        Assert.Equal("think", calls[1].Details.GetProperty("tool").GetString());
        Assert.All(calls, call =>
        {
            Assert.Null(call.ParentId);
            Assert.Equal("agent", call.Origin);
            Assert.Equal("complete", call.Status);
            Assert.Equal(topic, call.Details.GetProperty("query").GetString());
            Assert.Equal(context, call.Details.GetProperty("context").GetString());
        });
        Assert.Equal(calls[0].Details.GetProperty("candidate_ids").GetRawText(), calls[1].Details.GetProperty("candidate_ids").GetRawText());
        Assert.Equal(calls[0].Details.GetProperty("returned_ids").GetRawText(), calls[1].Details.GetProperty("returned_ids").GetRawText());
        Assert.Null(ActivityScope.CurrentId);
    }

    [Fact]
    public async Task ThinkValidatesTopicAndContextBoundsBeforeCognitionAndRecordsEachFailureOnce()
    {
        using var fixture = new Fixture();
        await fixture.Engine.RememberAsync("existing experience");
        var callsBefore = fixture.Cognition.Calls;
        foreach (var topic in new[] { null, "", " \t" })
        {
            var error = await Assert.ThrowsAsync<InputException>(() => fixture.Engine.ThinkAsync(topic!));
            Assert.Equal("topic must not be empty.", error.Message);
            Assert.Null(ActivityScope.CurrentId);
        }
        var oversized = await Assert.ThrowsAsync<InputException>(
            () => fixture.Engine.ThinkAsync(new string('x', fixture.Options.MaxRawCharacters + 1)));
        Assert.Equal("Think topic or context exceeds the configured input bound.", oversized.Message);
        var oversizedContext = await Assert.ThrowsAsync<InputException>(
            () => fixture.Engine.ThinkAsync("principles", new string('x', fixture.Options.MaxRawCharacters + 1)));
        Assert.Equal(oversized.Message, oversizedContext.Message);
        Assert.Equal(callsBefore, fixture.Cognition.Calls);
        Assert.Empty(fixture.Store.ReadSnapshot().RecallEvents);
        var calls = fixture.Read("recall");
        Assert.Equal(5, calls.Count);
        Assert.All(calls, call =>
        {
            Assert.Null(call.ParentId);
            Assert.Equal("think", call.Details.GetProperty("tool").GetString());
            Assert.Equal("failed", call.Status);
            Assert.Equal(nameof(InputException), call.ErrorType);
            Assert.Empty(call.Details.GetProperty("candidate_ids").EnumerateArray());
        });
        Assert.Null(ActivityScope.CurrentId);

        var atLimit = new string('x', fixture.Options.MaxRawCharacters);
        Assert.Single((await fixture.Engine.ThinkAsync(atLimit, atLimit)).Memories);
        Assert.Equal((atLimit, atLimit), Assert.Single(fixture.Cognition.Selections));
    }

    [Fact]
    public async Task ThinkFailureAndCancellationDisposeTheirSingleActivityBeforeTheNextInvocation()
    {
        using var fixture = new Fixture();
        await fixture.Engine.RememberAsync("existing experience");
        fixture.Cognition.SelectionFailure = new InvalidOperationException("injected selection failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.ThinkAsync("principles"));
        Assert.Null(ActivityScope.CurrentId);
        fixture.Cognition.SelectionFailure = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Engine.ThinkAsync("principles", cancellationToken: new CancellationToken(canceled: true)));
        Assert.Null(ActivityScope.CurrentId);
        Assert.Empty(fixture.Store.ReadSnapshot().RecallEvents);
        Assert.Single((await fixture.Engine.ThinkAsync("principles")).Memories);
        var calls = fixture.Read("recall");
        Assert.Equal(3, calls.Count);
        Assert.Equal("failed", calls[0].Status);
        Assert.Equal("cancelled", calls[1].Status);
        Assert.Equal("complete", calls[2].Status);
        Assert.All(calls, call =>
        {
            Assert.Null(call.ParentId);
            Assert.Equal("think", call.Details.GetProperty("tool").GetString());
            Assert.Equal(JsonValueKind.Null, call.Details.GetProperty("context").ValueKind);
        });
        Assert.Null(ActivityScope.CurrentId);
    }

    [Fact]
    public async Task NestedApiCorrelationRestoresParentAndIsolatesConcurrentFlows()
    {
        using var fixture = new Fixture();
        async Task<string> Record(string name)
        {
            using var scope = ActivityScope.Begin(fixture.Store, "remember", "agent", Day, new { name });
            var parent = ActivityScope.CurrentId!;
            await Task.Yield();
            using (ActivityScope.Begin(fixture.Store, "extraction", "agent", Day, new { }))
            using (ActivityScope.BeginApiSettings(new { reasoning_effort = name }))
            {
                fixture.Store.ReserveUsage(null, "model", "remember", .1m, Day);
                await Task.Yield();
            }
            Assert.Equal(parent, ActivityScope.CurrentId);
            Assert.Null(ActivityScope.ApiSettingsJson);
            return parent;
        }

        var parents = await Task.WhenAll(Record("low"), Record("high"));
        Assert.NotEqual(parents[0], parents[1]);
        Assert.Null(ActivityScope.CurrentId);
        Assert.Equal(2L, fixture.Scalar("SELECT COUNT(*) FROM activity_api_calls a JOIN activity_operations o ON a.activity_id=o.id WHERE o.kind='extraction' AND a.settings_json IS NOT NULL"));
        Assert.Equal(2L, fixture.Scalar("SELECT COUNT(DISTINCT settings_json) FROM activity_api_calls"));
    }

    [Fact]
    public void RelationResultsPreserveFirstAppendAcrossInterruptedReplayAndRejections()
    {
        using var fixture = new ConsolidationFixture();
        var memories = fixture.Observations(2, Day);
        var run = fixture.Store.GetOrCreateRun(RunKind.Dream, Day.Date, Day.Date.AddDays(1), Day.AddDays(1), null);
        var proposal = new RelationProposal(memories[0].Id, memories[1].Id, RelationKind.Positive);
        using (ActivityScope.Begin(fixture.Store, "assimilation", "dream", Day, new { }, runId: run.Id, workKey: "work"))
        {
            fixture.Store.ApplyActivityRelation(proposal, run, "work", 0, Day);
        }

        var restarted = new SqliteMemoryStore(fixture.Options);
        using (ActivityScope.Begin(restarted, "assimilation", "dream", Day.AddDays(1), new { proposal_reused = true }, runId: run.Id, workKey: "work"))
        {
            restarted.ApplyActivityRelation(proposal, run, "work", 0, Day.AddDays(1));
            restarted.ApplyActivityRelation(proposal, run, "work", 1, Day.AddDays(1));
            restarted.ApplyActivityRelation(proposal with { RelatedMemoryId = "unknown" }, run, "work", 2, Day.AddDays(1));
            restarted.ApplyActivityRelation(proposal, run, "work", 3, Day.AddDays(1), "invalid direction");
        }

        using var db = new SqliteConnection($"Data Source={restarted.DatabasePath};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT outcome FROM activity_relation_results ORDER BY proposal_index";
        using var reader = command.ExecuteReader();
        var outcomes = new List<string>();
        while (reader.Read())
        {
            outcomes.Add(reader.GetString(0));
        }
        Assert.Equal(["appended", "already_exists", "rejected", "rejected"], outcomes);
        Assert.Single(restarted.GetMemory(memories[0].Id)!.Relations);
        Assert.Equal(2, restarted.GetRejectedProposalCount(run.Id));
    }

    [Fact]
    public void FailedRelationAuditWriteRollsBackGraphAppend()
    {
        using var fixture = new ConsolidationFixture();
        var memories = fixture.Observations(2, Day);
        var run = fixture.Store.GetOrCreateRun(RunKind.Dream, Day.Date, Day.Date.AddDays(1), Day.AddDays(1), null);
        var proposal = new RelationProposal(memories[0].Id, memories[1].Id, RelationKind.Positive);
        using var db = new SqliteConnection($"Data Source={fixture.Store.DatabasePath};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_activity_write BEFORE INSERT ON activity_relation_results
            BEGIN SELECT RAISE(ABORT, 'injected audit failure'); END;
            """;
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => fixture.Store.ApplyActivityRelation(proposal, run, "work", 0, Day));
        Assert.Empty(fixture.Store.GetMemory(memories[0].Id)!.Relations);
        command.CommandText = "DROP TRIGGER fail_activity_write";
        command.ExecuteNonQuery();
        fixture.Store.ApplyActivityRelation(proposal, run, "work", 0, Day);
        command.CommandText = "SELECT outcome FROM activity_relation_results";
        Assert.Equal("appended", command.ExecuteScalar());
        Assert.Single(fixture.Store.GetMemory(memories[0].Id)!.Relations);
    }

    [Fact]
    public async Task DreamAttemptRecordsSavedProposalReuseAndNoModelInvocation()
    {
        using var fixture = new ConsolidationFixture();
        var memories = fixture.Observations(2, Day);
        var start = new DateTimeOffset(Day.Date, TimeSpan.Zero);
        var run = fixture.Store.GetOrCreateRun(RunKind.Dream, start, start.AddDays(1), Day.AddDays(1), null);
        fixture.Store.EnsureWorkItems(run.Id, [new WorkSeed("saved", "assimilation", memories[1].Id, 0)]);
        fixture.Store.SaveWorkProposal(run.Id, "saved", JsonSerializer.Serialize(new
        {
            allowed_candidate_ids = new[] { memories[0].Id },
            relations = new[] { new RelationProposal(memories[0].Id, memories[1].Id, RelationKind.Positive) },
            abstractions = Array.Empty<AbstractionProposal>()
        }, JsonDefaults.Options), "saved-model");
        await fixture.Engine.DreamAsync(start, start.AddDays(1));
        Assert.Empty(fixture.Cognition.Calls);
        using var db = new SqliteConnection($"Data Source={fixture.Store.DatabasePath};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT details_json FROM activity_operations WHERE kind='assimilation'";
        var details = JsonDocument.Parse((string)command.ExecuteScalar()!).RootElement;
        Assert.True(details.GetProperty("proposal_reused").GetBoolean());
        Assert.False(details.GetProperty("model_invoked").GetBoolean());
        Assert.Equal("saved-model", details.GetProperty("model").GetString());
        Assert.Single(fixture.Store.GetMemory(memories[0].Id)!.Relations);
    }

    private sealed record ActivityRow(string Id, string? ParentId, string? SourceId, string Origin,
        string Status, string? ErrorType, JsonElement Details);

    private sealed class Fixture : IDisposable
    {
        public EngineOptions Options { get; } = new()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "long-journey-activity-tests", Guid.NewGuid().ToString("N")),
            TimeZoneId = "UTC"
        };
        public SqliteMemoryStore Store { get; }
        public ActivityCognition Cognition { get; } = new();
        public ConsolidationClock Clock { get; } = new() { Now = Day };
        public MemoryEngine Engine { get; }
        public Fixture()
        {
            Store = new SqliteMemoryStore(Options);
            Engine = new MemoryEngine(Store, Cognition, new MemorySearch(Store, Cognition, Options), Options, Clock);
        }

        public List<ActivityRow> Read(string kind)
        {
            using var db = new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT id,parent_id,source_id,origin,status,error_type,details_json FROM activity_operations WHERE kind=$kind ORDER BY rowid";
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            var result = new List<ActivityRow>();
            while (reader.Read())
            {
                using var document = JsonDocument.Parse(reader.GetString(6));
                result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), document.RootElement.Clone()));
            }
            return result;
        }

        public object? Scalar(string sql)
        {
            using var db = new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        public void Dispose() => Directory.Delete(Options.DataDirectory, true);
    }

    private sealed class ActivityCognition : ICognition
    {
        public string EmbeddingSpace => "test:3";
        public Func<Task>? BeforeExtract { get; set; }
        public bool EmptySelection { get; set; }
        public int Calls { get; private set; }
        public Exception? SelectionFailure { get; set; }
        public List<(string Query, string? Context)> Selections { get; } = [];
        public List<string> EmbeddedTexts { get; } = [];
        public async Task<CognitiveResult<IReadOnlyList<ObservationProposal>>> ExtractAsync(string raw, CallContext context, CancellationToken cancellationToken)
        {
            Calls++;
            if (BeforeExtract is not null)
            {
                await BeforeExtract();
            }
            return new([new ObservationProposal(raw)], "fake");
        }
        public Task<EmbeddingVector> EmbedAsync(string text, CallContext context, CancellationToken cancellationToken)
        {
            Calls++;
            EmbeddedTexts.Add(text);
            return Task.FromResult(ConsolidationFixture.Vector);
        }
        public Task<CognitiveResult<IReadOnlyList<string>>> SelectAsync(string query, string? context,
            IReadOnlyList<MemoryRecord> candidates, CallContext call, CancellationToken cancellationToken)
        {
            Calls++;
            Selections.Add((query, context));
            if (SelectionFailure is not null)
            {
                throw SelectionFailure;
            }
            return Task.FromResult(new CognitiveResult<IReadOnlyList<string>>(EmptySelection ? [] : candidates.Reverse().Select(memory => memory.Id).ToArray(), "fake"));
        }
        public Task<CognitiveResult<IReadOnlyList<RelationProposal>>> AssimilateAsync(MemoryRecord observation,
            IReadOnlyList<MemoryRecord> candidates, CallContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CognitiveResult<IReadOnlyList<AbstractionProposal>>> AbstractAsync(IReadOnlyList<MemoryRecord> neighborhood,
            IReadOnlyList<SourceArtifact> sources, CognitionRole role, CallContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CognitiveResult<IReadOnlyList<string>>> PrioritizeMeditationAsync(IReadOnlyList<MeditationPriorityCandidate> candidates,
            CallContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
