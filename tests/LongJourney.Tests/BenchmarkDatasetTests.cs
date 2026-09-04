using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LongJourney.Benchmarks;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class BenchmarkDatasetTests
{
    [Fact]
    public void KeepsLabelsOutsideObservationsAndPreservesBothRoles()
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
        Assert.Contains(result.History.Observations, observation => observation.Raw.Contains("] user\n", StringComparison.Ordinal));
        Assert.Contains(result.History.Observations, observation => observation.Raw.Contains("] assistant\n", StringComparison.Ordinal));
        foreach (var observation in result.History.Observations)
        {
            Assert.DoesNotContain("SENSITIVE", observation.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain("has_answer", observation.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain("single-session-user", observation.Raw, StringComparison.Ordinal);
            Assert.Equal("SENSITIVE_SESSION", observation.SessionId);
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
        Assert.Equal(result.History.Observations.Count, relabeled.History.Observations.Count);
        for (var index = 0; index < result.History.Observations.Count; index++)
        {
            Assert.Equal(result.History.Observations[index].Raw, relabeled.History.Observations[index].Raw);
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
    public void TimelineValidationChecksObservationTimesAndAllowsQuestionTimeEquality()
    {
        var result = Assert.Single(Read(CreateCase()).Cases);
        LongMemEvalDataset.ValidateTimeline(result);
        var equalQuestion = result with { Question = result.Question with { At = result.History.Turns[0].At } };
        LongMemEvalDataset.ValidateTimeline(equalQuestion);
        var futureObservation = result.History.Observations[0] with { At = result.Question.At.AddTicks(1) };
        var future = result with { History = new BenchmarkHistory(result.History.Turns, [futureObservation]) };
        Assert.Throws<InputException>(() => LongMemEvalDataset.ValidateTimeline(future));
    }

    [Fact]
    public void SplitsSentenceAndNewlineUnitsWithoutRepeatingTurnContext()
    {
        const string content = "First sentence. Second question?\r\nThird line!\nFourth line\nFinal";
        var item = WithSingleTurn(content);
        var history = Assert.Single(Read(item).Cases).History;
        Assert.Equal(content, Assert.Single(history.Turns).Content);
        Assert.Equal(5, history.Observations.Count);
        Assert.Equal("First sentence. ", Body(history.Observations[0]));
        Assert.Equal("Second question?\r\n", Body(history.Observations[1]));
        Assert.Equal("Third line!\n", Body(history.Observations[2]));
        Assert.Equal("Fourth line\n", Body(history.Observations[3]));
        Assert.Equal("Final", Body(history.Observations[4]));
        Assert.Equal(content, Reconstruct(history.Observations));
    }

    [Fact]
    public void SplitsUnspacedCjkSentences()
    {
        const string content = "첫 문장。다음 문장！마지막？";
        var history = Assert.Single(Read(WithSingleTurn(content)).Cases).History;
        Assert.Equal(3, history.Observations.Count);
        Assert.Equal(content, Reconstruct(history.Observations));
    }

    [Theory]
    [InlineData(43)]
    [InlineData(80)]
    [InlineData(1000)]
    public void HardSplitsOversizedUnitsWithinTotalRawLimitWithoutBreakingUnicode(int limit)
    {
        var content = new string('x', limit - 42) + "🚀" + new string('한', 2200) + "👨‍👩‍👧";
        var item = WithSingleTurn(content);
        var dataset = Read(item, limit);
        var history = Assert.Single(dataset.Cases).History;
        Assert.Equal(content, Assert.Single(history.Turns).Content);
        Assert.Equal(content, Reconstruct(history.Observations));
        for (var index = 0; index < history.Observations.Count; index++)
        {
            var observation = history.Observations[index];
            Assert.InRange(observation.Raw.Length, 1, limit);
            Assert.Equal(index, observation.PartIndex);
            Assert.Equal(0, observation.TurnIndex);
            Assert.Equal(history.Turns[0].At, observation.At);
            var body = Body(observation);
            Assert.False(char.IsLowSurrogate(body[0]));
            Assert.False(char.IsHighSurrogate(body[^1]));
        }

        var repeated = Assert.Single(Read(item, limit).Cases).History;
        Assert.Equal(history.Observations, repeated.Observations);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(1001)]
    public void RejectsInvalidObservationLimits(int limit)
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
            Assert.Equal(complete.Cases[index + 1].History.Observations, selected.Cases[index].History.Observations);
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
            LongMemEvalDataset.ReadSelectedAsync(file.Path, 1001, [], 1));
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

    private static BenchmarkDataset Read(JsonObject item, int maxRawCharacters = 1000)
    {
        using var file = new DatasetFile(new JsonArray(item.DeepClone()).ToJsonString());
        return LongMemEvalDataset.Read(file.Path, maxRawCharacters);
    }

    private static string Body(BenchmarkObservation observation) =>
        observation.Raw[(observation.Raw.IndexOf('\n') + 1)..];

    private static string Reconstruct(IReadOnlyList<BenchmarkObservation> observations)
    {
        var text = new StringBuilder();
        foreach (var observation in observations)
        {
            text.Append(Body(observation));
        }

        return text.ToString();
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
