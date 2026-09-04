using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LongJourney.Benchmarks;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class BenchmarkDatasetTests
{
    [Fact]
    public void KeepsLabelsOutsideSessionInputsAndPreservesBothRoles()
    {
        var item = CreateCase();
        item["question_id"] = "SENSITIVE_ID_abs";
        item["question"] = "SENSITIVE_QUESTION";
        item["answer"] = "SENSITIVE_ANSWER";
        item["haystack_session_ids"] = new JsonArray("SENSITIVE_SESSION");
        item["answer_session_ids"] = new JsonArray("SENSITIVE_SESSION");
        var result = Assert.Single(Read(item).Cases);

        Assert.Equal("SENSITIVE_QUESTION", result.Question.Text);
        Assert.Equal("SENSITIVE_ANSWER", result.Reference.Answer);
        Assert.True(result.Reference.IsAbstention);
        Assert.Equal("SENSITIVE_SESSION", Assert.Single(result.Reference.SessionIds));
        Assert.Equal("user", result.History.Turns[0].Role);
        Assert.Equal("assistant", result.History.Turns[1].Role);
        Assert.Contains(result.History.Sessions, session => session.Raw.Contains("] user\n", StringComparison.Ordinal));
        Assert.Contains(result.History.Sessions, session => session.Raw.Contains("] assistant\n", StringComparison.Ordinal));
        foreach (var session in result.History.Sessions)
        {
            Assert.DoesNotContain("SENSITIVE", session.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain("has_answer", session.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain("single-session-user", session.Raw, StringComparison.Ordinal);
            Assert.Equal("SENSITIVE_SESSION", session.SessionId);
        }

        // Alter every gold field while retaining the same history. Ingestion must be unchanged.
        item["question_id"] = "different";
        item["question_type"] = "multi-session";
        item["question"] = "different question";
        item["answer"] = 123;
        item["haystack_session_ids"] = new JsonArray("different_session");
        item["answer_session_ids"] = new JsonArray();
        item["haystack_sessions"]![0]![0]!["has_answer"] = false;
        var relabeled = Assert.Single(Read(item).Cases);
        Assert.False(relabeled.Reference.IsAbstention);
        Assert.Equal(result.History.Sessions.Count, relabeled.History.Sessions.Count);
        for (var index = 0; index < result.History.Sessions.Count; index++)
        {
            Assert.Equal(result.History.Sessions[index].Raw, relabeled.History.Sessions[index].Raw);
        }
    }

    [Fact]
    public void SortsSessionsChronologicallyAndKeepsStableTiesAndOriginalTurnIndices()
    {
        var item = CreateCase();
        item["haystack_session_ids"] = new JsonArray("late", "early-first", "early-second");
        item["haystack_dates"] = new JsonArray(
            "2023/04/10 (Mon) 10:00", "2023/04/10 (Mon) 08:00", "2023/04/10 (Mon) 08:00");
        item["haystack_sessions"] = JsonNode.Parse("""
            [
              [{"role":"user","content":"Late."}],
              [{"role":"user","content":"Early user."},{"role":"assistant","content":"Early assistant."}],
              [{"role":"user","content":"Equal time."}]
            ]
            """);
        item["answer_session_ids"] = new JsonArray("late");
        var result = Assert.Single(Read(item).Cases);
        var turns = result.History.Turns;
        Assert.Equal(4, turns.Count);
        Assert.Equal("early-first", turns[0].SessionId);
        Assert.Equal("early-first", turns[1].SessionId);
        Assert.Equal("early-second", turns[2].SessionId);
        Assert.Equal("late", turns[3].SessionId);
        Assert.Equal(0, turns[0].TurnIndex);
        Assert.Equal(1, turns[1].TurnIndex);
        Assert.Equal(0, turns[2].TurnIndex);
        Assert.Equal(turns[0].At, turns[1].At);
        Assert.Equal(turns[0].At, turns[2].At);
        Assert.Equal(new DateTimeOffset(2023, 4, 10, 8, 0, 0, TimeSpan.Zero), turns[0].At);
        var sessions = result.History.Sessions;
        Assert.Equal(3, sessions.Count);
        Assert.Equal("early-first", sessions[0].SessionId);
        Assert.Equal("early-second", sessions[1].SessionId);
        Assert.Equal("late", sessions[2].SessionId);
        Assert.Equal(turns[0].At, sessions[0].At);
        Assert.Contains("[turn 1] user\nEarly user.\n\n[turn 2] assistant\nEarly assistant.",
            sessions[0].Raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2023/04/10 (Mon) 10:00")]
    [InlineData("2023-04-10T19:00:00+09:00")]
    [InlineData("2023-04-10T10:00:00Z")]
    [InlineData("2023-04-10T10:00:00.0000000Z")]
    public void ParsesOfficialAndExplicitZoneDatesAsUtc(string date)
    {
        var item = CreateCase();
        item["haystack_dates"] = new JsonArray(date);
        var turn = Assert.Single(Read(item).Cases).History.Turns[0];
        Assert.Equal(new DateTimeOffset(2023, 4, 10, 10, 0, 0, TimeSpan.Zero), turn.At);
        Assert.Equal(TimeSpan.Zero, turn.At.Offset);
    }

    [Theory]
    [InlineData("2023/04/10 (Tue) 10:00")]
    [InlineData("2023/02/29 (Wed) 10:00")]
    [InlineData("2023-04-10T10:00:00")]
    [InlineData("2023/04/10 10:00")]
    [InlineData("2023-04-10T10:00:00+25:00")]
    [InlineData("2023/13/10 (Mon) 10:00")]
    public void RejectsMalformedOrAmbiguousDates(string date)
    {
        var item = CreateCase();
        item["haystack_dates"] = new JsonArray(date);
        Assert.Throws<InvalidDataException>(() => Read(item));
        item = CreateCase();
        item["question_date"] = date;
        Assert.Throws<InvalidDataException>(() => Read(item));
    }

    [Fact]
    public void PreservesFutureHistoryButRejectsTimelineBeforeReplay()
    {
        var item = CreateCase();
        item["haystack_dates"] = new JsonArray("2023/04/11 (Tue) 10:00");
        var result = Assert.Single(Read(item).Cases);
        Assert.Equal(2, result.History.Turns.Count);
        Assert.Equal(new DateTimeOffset(2023, 4, 11, 10, 0, 0, TimeSpan.Zero), result.History.Turns[0].At);
        var exception = Assert.Throws<InputException>(() => LongMemEvalDataset.ValidateTimeline(result));
        Assert.Contains("after question_date", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TimelineValidationChecksSessionTimesAndAllowsQuestionTimeEquality()
    {
        var result = Assert.Single(Read(CreateCase()).Cases);
        LongMemEvalDataset.ValidateTimeline(result);
        var equalQuestion = result with { Question = result.Question with { At = result.History.Turns[0].At } };
        LongMemEvalDataset.ValidateTimeline(equalQuestion);
        var futureSession = result.History.Sessions[0] with { At = result.Question.At.AddTicks(1) };
        var future = result with { History = new BenchmarkHistory(result.History.Turns, [futureSession]) };
        Assert.Throws<InputException>(() => LongMemEvalDataset.ValidateTimeline(future));
    }

    [Fact]
    public void PreservesCompleteSessionContentIncludingListsWhitespaceAndUnicode()
    {
        const string content = "  Here are other alternatives. Which is better?\r\n\r\n" +
                               "1. First option. Its explanation!\r\n" +
                               "2. 다른 표현。설명도 함께！🚀\n\nFinal note.  ";
        var history = Assert.Single(Read(WithSingleTurn(content)).Cases).History;
        Assert.Equal(content, Assert.Single(history.Turns).Content);
        var session = Assert.Single(history.Sessions);
        Assert.Equal("[2023-04-10T10:00:00.0000000Z] conversation\n\n[turn 1] assistant\n" + content, session.Raw);
        Assert.Equal(history.Turns[0].At, session.At);
    }

    [Fact]
    public void CombinesDialogueTurnsInOrderIntoOneContextualInput()
    {
        var item = CreateCase();
        const string first = "I bought a piano. It is blue.\n ";
        const string second = "  Then use the bench I suggested for it.\r\n";
        item["haystack_sessions"]![0]![0]!["content"] = first;
        item["haystack_sessions"]![0]![1]!["content"] = second;
        var history = Assert.Single(Read(item).Cases).History;
        var session = Assert.Single(history.Sessions);
        Assert.Equal(2, history.Turns.Count);
        Assert.Equal("[2023-04-10T10:00:00.0000000Z] conversation\n\n[turn 1] user\n" + first +
                     "\n\n[turn 2] assistant\n" + second, session.Raw);
        Assert.Equal(first, history.Turns[0].Content);
        Assert.Equal(second, history.Turns[1].Content);
    }

    [Fact]
    public void SkipsEmptySessionsWithoutLosingNonemptySessionOrder()
    {
        var item = CreateCase();
        var content = item["haystack_sessions"]![0]!.DeepClone();
        item["haystack_session_ids"] = new JsonArray("empty-first", "session-one", "empty-last");
        item["haystack_dates"] = new JsonArray(
            "2023/04/10 (Mon) 08:00", "2023/04/10 (Mon) 10:00", "2023/04/10 (Mon) 11:00");
        item["haystack_sessions"] = new JsonArray(new JsonArray(), content, new JsonArray());
        var history = Assert.Single(Read(item).Cases).History;
        Assert.Equal(2, history.Turns.Count);
        Assert.Equal("session-one", Assert.Single(history.Sessions).SessionId);

        item = CreateCase();
        item["haystack_sessions"] = new JsonArray(new JsonArray());
        history = Assert.Single(Read(item).Cases).History;
        Assert.Empty(history.Turns);
        Assert.Empty(history.Sessions);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(4000)]
    [InlineData(64000)]
    public void AppliesRawLimitToCompleteSessionHeadersAndContentWithoutSplitting(int limit)
    {
        const string header = "[2023-04-10T10:00:00.0000000Z] conversation\n\n[turn 1] assistant\n";
        var content = new string('한', limit - header.Length - 2) + "🚀";
        var history = Assert.Single(Read(WithSingleTurn(content), limit).Cases).History;
        var session = Assert.Single(history.Sessions);
        Assert.Equal(limit, session.Raw.Length);
        Assert.Equal(header + content, session.Raw);

        var error = Assert.Throws<InputException>(() => Read(WithSingleTurn(content + "x"), limit));
        Assert.Contains("max_raw_characters", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains((limit + 1).ToString(), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(content, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsACompleteSessionWhenItsIndividualTurnsWouldEachFit()
    {
        var item = CreateCase();
        var content = new string('x', 80);
        item["haystack_sessions"]![0]![0]!["content"] = content;
        item["haystack_sessions"]![0]![1]!["content"] = content;
        var expected = "[2023-04-10T10:00:00.0000000Z] conversation\n\n[turn 1] user\n" + content +
                       "\n\n[turn 2] assistant\n" + content;
        var error = Assert.Throws<InputException>(() => Read(item, 200));
        Assert.Contains(expected.Length.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains("case[0].haystack_sessions[0]", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("session-one", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(content, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    public void RejectsInvalidSessionRawLimits(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Read(CreateCase(), limit));
    }

    [Theory]
    [InlineData("question_id")]
    [InlineData("question_type")]
    [InlineData("question")]
    [InlineData("answer")]
    [InlineData("question_date")]
    [InlineData("haystack_session_ids")]
    [InlineData("haystack_dates")]
    [InlineData("haystack_sessions")]
    [InlineData("answer_session_ids")]
    public void RejectsMissingAndNullRequiredFields(string field)
    {
        var item = CreateCase();
        item.Remove(field);
        Assert.Throws<InvalidDataException>(() => Read(item));
        item = CreateCase();
        item[field] = null;
        Assert.Throws<InvalidDataException>(() => Read(item));
    }

    [Theory]
    [InlineData("unsupported-type")]
    [InlineData("aligned-lengths")]
    [InlineData("duplicate-session")]
    [InlineData("duplicate-evidence")]
    [InlineData("absent-evidence")]
    [InlineData("null-session")]
    [InlineData("null-turn")]
    [InlineData("bad-role")]
    [InlineData("null-role")]
    [InlineData("null-content")]
    [InlineData("empty-content")]
    [InlineData("bad-label")]
    [InlineData("bad-answer")]
    public void RejectsInvalidSchema(string defect)
    {
        var item = CreateCase();
        switch (defect)
        {
            case "unsupported-type":
                item["question_type"] = "abstention";
                break;
            case "aligned-lengths":
                item["haystack_dates"] = new JsonArray();
                break;
            case "duplicate-session":
                item["haystack_session_ids"]!.AsArray().Add("session-one");
                item["haystack_dates"]!.AsArray().Add("2023/04/10 (Mon) 11:00");
                item["haystack_sessions"]!.AsArray().Add(new JsonArray());
                break;
            case "duplicate-evidence":
                item["answer_session_ids"]!.AsArray().Add("session-one");
                break;
            case "absent-evidence":
                item["answer_session_ids"] = new JsonArray("absent");
                break;
            case "null-session":
                item["haystack_sessions"]![0] = null;
                break;
            case "null-turn":
                item["haystack_sessions"]![0]![0] = null;
                break;
            case "bad-role":
                item["haystack_sessions"]![0]![0]!["role"] = "system";
                break;
            case "null-role":
                item["haystack_sessions"]![0]![0]!["role"] = null;
                break;
            case "null-content":
                item["haystack_sessions"]![0]![0]!["content"] = null;
                break;
            case "empty-content":
                item["haystack_sessions"]![0]![0]!["content"] = " ";
                break;
            case "bad-label":
                item["haystack_sessions"]![0]![0]!["has_answer"] = "true";
                break;
            case "bad-answer":
                item["answer"] = true;
                break;
        }

        Assert.Throws<InvalidDataException>(() => Read(item));
    }

    [Fact]
    public void RejectsDuplicateQuestionIdsAndDuplicateJsonProperties()
    {
        using var duplicateIds = new DatasetFile(new JsonArray(CreateCase(), CreateCase()).ToJsonString());
        Assert.Throws<InvalidDataException>(() => LongMemEvalDataset.Read(duplicateIds.Path));
        var text = "[{\"question_id\":\"duplicate\"," + CreateCase().ToJsonString()[1..] + "]";
        using var duplicateProperties = new DatasetFile(text);
        Assert.Throws<InvalidDataException>(() => LongMemEvalDataset.Read(duplicateProperties.Path));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[")]
    public void RejectsMalformedRoot(string json)
    {
        using var file = new DatasetFile(json);
        Assert.Throws<InvalidDataException>(() => LongMemEvalDataset.Read(file.Path));
    }

    [Fact]
    public void PreservesNumericAnswersAndHashesExactInputBytes()
    {
        var item = CreateCase();
        item["answer"] = 1234567890123456789L;
        using var file = new DatasetFile(new JsonArray(item).ToJsonString());
        var dataset = LongMemEvalDataset.Read(file.Path);
        Assert.Equal("1234567890123456789", Assert.Single(dataset.Cases).Reference.Answer);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.Path))), dataset.Sha256);
        Assert.Equal(64, dataset.Sha256.Length);
        File.AppendAllText(file.Path, "\n");
        Assert.NotEqual(dataset.Sha256, LongMemEvalDataset.Read(file.Path).Sha256);
    }

    [Fact]
    public async Task StreamingSelectionMatchesFullInputHashAndSelectedHistory()
    {
        using var file = new DatasetFile(new JsonArray(
            NamedCase("z-last"), NamedCase("a-first"), NamedCase("m-middle")).ToJsonString());
        var complete = LongMemEvalDataset.Read(file.Path);
        var selected = await LongMemEvalDataset.ReadSelectedAsync(file.Path, 1000, ["m-middle", "a-first"], 1);
        Assert.Equal(complete.Sha256, selected.Sha256);
        Assert.Equal(2, selected.Cases.Count);
        Assert.Equal("a-first", selected.Cases[0].Id);
        Assert.Equal("m-middle", selected.Cases[1].Id);
        for (var index = 0; index < selected.Cases.Count; index++)
        {
            Assert.Equal(complete.Cases[index + 1].Question, selected.Cases[index].Question);
            Assert.Equal(complete.Cases[index + 1].History.Turns, selected.Cases[index].History.Turns);
            Assert.Equal(complete.Cases[index + 1].History.Sessions, selected.Cases[index].History.Sessions);
            Assert.Equal(complete.Cases[index + 1].Reference.Answer, selected.Cases[index].Reference.Answer);
            Assert.Equal(complete.Cases[index + 1].Reference.SessionIds, selected.Cases[index].Reference.SessionIds);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    public async Task StreamingSelectionUsesOrdinalIdsRatherThanFileOrder(int limit)
    {
        using var file = new DatasetFile(new JsonArray(
            NamedCase("z-last"), NamedCase("a-first"), NamedCase("m-middle")).ToJsonString());
        var selected = await LongMemEvalDataset.ReadSelectedAsync(file.Path, 1000, [], limit);
        string[] orderedIds = ["a-first", "m-middle", "z-last"];
        Assert.Equal(Math.Min(limit, orderedIds.Length), selected.Cases.Count);
        for (var index = 0; index < selected.Cases.Count; index++)
        {
            Assert.Equal(orderedIds[index], selected.Cases[index].Id);
        }
    }

    [Fact]
    public async Task StreamingSelectionExpandsAndValidatesOnlySelectedHistories()
    {
        var unselected = NamedCase("z-unselected");
        unselected["haystack_sessions"] = "invalid history";
        using var file = new DatasetFile(new JsonArray(unselected, NamedCase("a-selected")).ToJsonString());
        var selected = await LongMemEvalDataset.ReadSelectedAsync(file.Path, 1000, [], 1);
        Assert.Equal("a-selected", Assert.Single(selected.Cases).Id);
        Assert.Equal(2, selected.Cases[0].History.Turns.Count);
        Assert.Throws<InvalidDataException>(() => LongMemEvalDataset.Read(file.Path));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LongMemEvalDataset.ReadSelectedAsync(file.Path, 1000, ["z-unselected"], 1));
    }

    [Fact]
    public async Task StreamingSelectionPreservesWholeSessionsAndRejectsOnlySelectedOversizedSessions()
    {
        var oversized = NamedCase("z-oversized");
        oversized["haystack_sessions"]![0]![0]!["content"] = new string('x', 4000);
        var selectedCase = NamedCase("a-selected");
        const string content = "Choose another term.\r\n1. A complete first option.\n2. A complete second option.";
        selectedCase["haystack_sessions"]![0]![0]!["content"] = content;
        using var file = new DatasetFile(new JsonArray(oversized, selectedCase).ToJsonString());

        var selected = await LongMemEvalDataset.ReadSelectedAsync(file.Path, 4000, ["a-selected"], 1);
        var history = Assert.Single(selected.Cases).History;
        var session = Assert.Single(history.Sessions);
        Assert.Equal("[2023-04-10T10:00:00.0000000Z] conversation\n\n[turn 1] user\n" + content +
                     "\n\n[turn 2] assistant\nI suggested a bench.", session.Raw);

        var error = await Assert.ThrowsAsync<InputException>(() =>
            LongMemEvalDataset.ReadSelectedAsync(file.Path, 4000, ["a-selected", "z-oversized"], 1));
        Assert.Contains("case[0].haystack_sessions[0]", error.Message, StringComparison.Ordinal);
        Assert.Contains("max_raw_characters", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("z-oversized", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("missing")]
    public async Task StreamingSelectionValidatesIdentitiesEvenOutsideSelection(string defect)
    {
        var unselected = NamedCase("z-unselected");
        switch (defect)
        {
            case "duplicate":
                unselected["question_id"] = "a-selected";
                break;
            case "null":
                unselected["question_id"] = null;
                break;
            case "empty":
                unselected["question_id"] = " ";
                break;
            case "missing":
                unselected.Remove("question_id");
                break;
        }
        using var file = new DatasetFile(new JsonArray(NamedCase("a-selected"), unselected).ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LongMemEvalDataset.ReadSelectedAsync(file.Path, 1000, ["a-selected"], 1));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("null")]
    public async Task StreamingSelectionRejectsInvalidRequestedIds(string defect)
    {
        using var file = new DatasetFile(new JsonArray(CreateCase()).ToJsonString());
        IReadOnlyList<string> requested = defect switch
        {
            "absent" => ["absent"],
            "duplicate" => ["question-one", "question-one"],
            "empty" => [" "],
            _ => [null!]
        };
        await Assert.ThrowsAsync<InputException>(() =>
            LongMemEvalDataset.ReadSelectedAsync(file.Path, 1000, requested, 1));
    }

    [Fact]
    public async Task StreamingSelectionRejectsAnEmptyDatasetAndInvalidLimits()
    {
        using var file = new DatasetFile("[]");
        await Assert.ThrowsAsync<InputException>(() =>
            LongMemEvalDataset.ReadSelectedAsync(file.Path, 1000, [], 1));
        await Assert.ThrowsAsync<InputException>(() =>
            LongMemEvalDataset.ReadSelectedAsync(file.Path, 1000, [], 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            LongMemEvalDataset.ReadSelectedAsync(file.Path, 42, [], 1));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[")]
    public async Task StreamingSelectionRejectsMalformedJson(string json)
    {
        using var file = new DatasetFile(json);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LongMemEvalDataset.ReadSelectedAsync(file.Path, 1000, [], 1));
    }

    [Fact]
    public async Task StreamingSelectionHonorsCancellationBeforeOpeningTheFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var missingPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LongMemEvalDataset.ReadSelectedAsync(missingPath, 1000, [], 1, cancellation.Token));
    }

    [Fact]
    public async Task StreamingSelectionPreservesInvalidTimelinesForTheExplicitGate()
    {
        var item = CreateCase();
        item["haystack_dates"] = new JsonArray("2023/04/11 (Tue) 10:00");
        using var file = new DatasetFile(new JsonArray(item).ToJsonString());
        var selected = await LongMemEvalDataset.ReadSelectedAsync(file.Path, 1000, [], 1);
        Assert.Throws<InputException>(() => LongMemEvalDataset.ValidateTimeline(Assert.Single(selected.Cases)));
    }

    private static JsonObject NamedCase(string id)
    {
        var item = CreateCase();
        item["question_id"] = id;
        item["question"] = $"Question for {id}";
        item["haystack_sessions"]![0]![0]!["content"] = $"History for {id}.";
        return item;
    }

    private static JsonObject WithSingleTurn(string content)
    {
        var item = CreateCase();
        item["haystack_sessions"] = new JsonArray(new JsonArray(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content
        }));
        return item;
    }

    private static BenchmarkDataset Read(JsonObject item, int maxRawCharacters = 64_000)
    {
        using var file = new DatasetFile(new JsonArray(item.DeepClone()).ToJsonString());
        return LongMemEvalDataset.Read(file.Path, maxRawCharacters);
    }

    private static JsonObject CreateCase() => JsonNode.Parse("""
        {
          "question_id": "question-one",
          "question_type": "single-session-user",
          "question": "Which instrument did I buy?",
          "answer": "Piano",
          "question_date": "2023/04/10 (Mon) 23:07",
          "haystack_session_ids": ["session-one"],
          "haystack_dates": ["2023/04/10 (Mon) 10:00"],
          "haystack_sessions": [[
            {"role":"user","content":"I bought a piano. It is blue.","has_answer":true},
            {"role":"assistant","content":"I suggested a bench.","has_answer":false}
          ]],
          "answer_session_ids": ["session-one"]
        }
        """)!.AsObject();

    private sealed class DatasetFile : IDisposable
    {
        public DatasetFile(string json)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"longjourney-benchmark-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, json);
        }

        public string Path { get; }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }
}
