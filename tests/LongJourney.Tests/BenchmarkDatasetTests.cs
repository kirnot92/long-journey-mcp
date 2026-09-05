using System.Text.Json;
using LongJourney.Benchmarks;

namespace LongJourney.Tests;

public sealed class BenchmarkDatasetTests
{
    [Fact]
    public void LoaderPreservesEntireLongSessionAndWhitelistsOnlyDialogueAndOpaqueMetadata()
    {
        using var dataset = new DatasetFile();
        var longContent = new string('x', 90_000) + "\nfinal observation 한글";
        var turns = new List<object>();
        for (var index = 0; index < 132; index++)
        {
            turns.Add(new
            {
                role = index % 2 == 0 ? "user" : "assistant",
                content = index == 131 ? longContent : $"turn {index}",
                has_answer = true,
                evidence_label = "hidden annotation"
            });
        }

        dataset.Write(["2023/05/20 (Sat) 02:21"], ["answer_hidden_id"], [turns]);
        var question = Assert.Single(LongMemEvalDataset.Load(dataset.Path));
        var session = Assert.Single(question.Sessions);
        Assert.Equal("answer_hidden_id", session.SessionId);
        Assert.Equal("question evaluation marker", question.Question);
        Assert.Equal("reference evaluation marker", question.Answer);
        Assert.Equal(TimeSpan.Zero, session.Timestamp.Offset);
        Assert.DoesNotContain("answer_hidden_id", session.Raw);
        Assert.DoesNotContain("has_answer", session.Raw);
        Assert.DoesNotContain("evidence_label", session.Raw);
        Assert.DoesNotContain("evaluation marker", session.Raw);
        using var raw = JsonDocument.Parse(session.Raw);
        var preservedTurns = raw.RootElement.GetProperty("turns");
        Assert.Equal(132, preservedTurns.GetArrayLength());
        Assert.Equal(longContent, preservedTurns[131].GetProperty("content").GetString());
        Assert.Equal("assistant", preservedTurns[131].GetProperty("role").GetString());
        Assert.Equal(0, raw.RootElement.GetProperty("session_ordinal").GetInt32());
    }

    [Fact]
    public void LoaderSortsChronologicallyStablyAndKeepsIdenticalSessionsDistinct()
    {
        using var dataset = new DatasetFile();
        object[] turns = [new { role = "user", content = "same full conversation" }];
        dataset.Write(
            ["2023/05/21 (Sun) 02:21", "2023/05/20 (Sat) 02:21", "2023/05/20 (Sat) 02:21"],
            ["last", "tie-first", "tie-second"], [turns, turns, turns]);

        var sessions = Assert.Single(LongMemEvalDataset.Load(dataset.Path)).Sessions;
        Assert.Equal("tie-first", sessions[0].SessionId);
        Assert.Equal("tie-second", sessions[1].SessionId);
        Assert.Equal("last", sessions[2].SessionId);
        Assert.NotEqual(sessions[0].Raw, sessions[1].Raw);
        Assert.Equal(new DateTimeOffset(2023, 5, 20, 2, 21, 0, TimeSpan.Zero), sessions[0].Timestamp);
    }

    [Fact]
    public void LoaderRejectsMisalignedHistory()
    {
        using var dataset = new DatasetFile();
        object[] turns = [new { role = "user", content = "content" }];
        dataset.Write([], ["session"], [turns]);
        Assert.Throws<InvalidDataException>(() => LongMemEvalDataset.Load(dataset.Path));
    }

    [Fact]
    public void LoaderPreservesOfficialHistoryLaterOnQuestionDay()
    {
        using var dataset = new DatasetFile();
        object[] turns = [new { role = "user", content = "late history" }];
        dataset.Write(["2023/05/30 (Tue) 23:59"], ["session"], [turns]);

        var question = Assert.Single(LongMemEvalDataset.Load(dataset.Path));

        Assert.True(Assert.Single(question.Sessions).Timestamp > question.QuestionDate);
        Assert.Equal(23, question.QuestionDate.Hour);
        Assert.Equal(40, question.QuestionDate.Minute);
    }

    [Fact]
    public void RepeatedDatasetIdsRemainDistinctSessionBoundaries()
    {
        using var dataset = new DatasetFile();
        object[] turns = [new { role = "user", content = "identical transcript" }];
        dataset.Write(
            ["2023/05/20 (Sat) 02:21", "2023/05/20 (Sat) 02:21"],
            ["07b7a667_1", "07b7a667_1"], [turns, turns]);

        var sessions = Assert.Single(LongMemEvalDataset.Load(dataset.Path)).Sessions;
        Assert.Equal(2, sessions.Count);
        Assert.Equal(sessions[0].SessionId, sessions[1].SessionId);
        Assert.NotEqual(sessions[0].Raw, sessions[1].Raw);
    }

    private sealed class DatasetFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "longmem-dataset-" + Guid.NewGuid().ToString("N") + ".json");

        public void Write(string[] dates, string[] ids, object[] sessions)
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(new[]
            {
                new
                {
                    question_id = "question-id",
                    question_type = "single-session-user",
                    question = "question evaluation marker",
                    answer = "reference evaluation marker",
                    question_date = "2023/05/30 (Tue) 23:40",
                    answer_session_ids = new[] { "answer_hidden_id" },
                    haystack_dates = dates,
                    haystack_session_ids = ids,
                    haystack_sessions = sessions
                }
            }));
        }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }
}
