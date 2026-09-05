using LongJourney.Core;

namespace LongJourney.Tests;

public sealed partial class ConsolidationTests
{
    [Fact]
    public async Task DreamSeedsCreatedObservationsAndAllDepthRecallsAndFreezesRelationsAndGeneration()
    {
        using var fixture = new ConsolidationFixture();
        var today = fixture.Clock.Now.Date;
        var start = new DateTimeOffset(today, TimeSpan.Zero);
        var old = fixture.Observations(3, start.AddDays(-10));
        var recalled = fixture.Abstraction(old, start.AddDays(-8));
        var fresh = fixture.Observations(3, start.AddHours(1));
        var createdAbstraction = fixture.Abstraction(fresh, start.AddHours(2));
        fixture.Store.RecordRecall([recalled.Id], start.AddHours(3));
        fixture.Cognition.Assimilate = (observation, _) => [new RelationProposal(recalled.Id, observation.Id, RelationKind.Negative)];
        fixture.Cognition.Abstract = (neighborhood, _, _) =>
        {
            var parentIds = new List<string>();
            foreach (var memory in neighborhood)
            {
                if (memory.Depth == 0)
                {
                    parentIds.Add(memory.Id);
                }

                if (parentIds.Count == 3)
                {
                    return [new AbstractionProposal("A provisional shared pattern.", parentIds)];
                }
            }

            return [];
        };

        var summary = await fixture.Engine.DreamAsync(start, start.AddDays(1));
        var run = Assert.Single(fixture.Store.GetRuns(), candidate => candidate.Id == summary.RunId);
        var work = fixture.Store.GetWorkItems(run.Id);
        Assert.Equal("complete", summary.Status);
        var expectedAssimilationIds = MemoryTestData.Ids(fresh);
        expectedAssimilationIds.Sort();
        Assert.Equal(expectedAssimilationIds, SortedWorkMemoryIds(work, "assimilation"));

        var expectedConsolidationIds = new List<string>(expectedAssimilationIds) { recalled.Id };
        expectedConsolidationIds.Sort();
        Assert.Equal(expectedConsolidationIds, SortedWorkMemoryIds(work, "consolidation"));
        Assert.DoesNotContain(work, x => x.MemoryId == createdAbstraction.Id);
        Assert.Equal(["assimilation", "assimilation", "assimilation"], fixture.Cognition.Calls.GetRange(0, 3));
        foreach (var neighborhood in fixture.Cognition.Neighborhoods)
        {
            foreach (var memory in neighborhood)
            {
                Assert.True(memory.Sequence <= run.MemoryHighWater);
                if (memory.Id == recalled.Id)
                {
                    Assert.Empty(memory.Relations);
                }
            }
        }
        Assert.Equal(3, fixture.Store.GetMemory(recalled.Id)!.Relations.Count);
        Assert.All(fresh, x => Assert.Empty(fixture.Store.GetMemory(x.Id)!.Relations));
        Assert.All(fixture.Cognition.Contexts, x => Assert.Equal(run.Id, x.RunId));
    }

    [Fact]
    public async Task InvalidParentsAndReversedAssimilationAreRejectedBeforeEmbedding()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(3, start.AddHours(1));
        fixture.Cognition.Assimilate = (observation, candidates) =>
            [new RelationProposal(observation.Id, candidates[0].Id, RelationKind.Positive)];
        fixture.Cognition.Abstract = (_, _, _) => [new AbstractionProposal("Invented evidence", [observations[0].Id, observations[1].Id, "unknown"])];

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal(4, result.RejectedProposals);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
        Assert.All(fixture.Store.ReadSnapshot().Memories, x => Assert.Empty(x.Relations));
        Assert.Equal(3, fixture.Store.ReadSnapshot().Memories.Count);
    }

    [Fact]
    public async Task DuplicateAssimilationRelationsAreRejectedIndividually()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        fixture.Observations(2, start.AddDays(-2));
        var fresh = fixture.Observations(1, start.AddHours(1))[0];
        string? ownerId = null;
        fixture.Cognition.Assimilate = (_, candidates) =>
        {
            ownerId = candidates[0].Id;
            return
            [
                new RelationProposal(ownerId, fresh.Id, RelationKind.Positive),
                new RelationProposal(ownerId, fresh.Id, RelationKind.Positive)
            ];
        };

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        Assert.Equal(1, result.RejectedProposals);
        Assert.NotNull(ownerId);
        Assert.Single(fixture.Store.GetMemory(ownerId)!.Relations);
    }

    [Fact]
    public async Task DirectEvidenceSurvivesFullSemanticPageAndMixedDepthProposalIsRejected()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var old = fixture.Observations(25, start.AddDays(-10));
        var higher = fixture.Abstraction(old[..3], start.AddDays(-8));
        var seed = fixture.Observations(1, start.AddHours(1))[0];
        fixture.Relate(seed, higher, RelationKind.Negative, start.AddHours(2));
        fixture.Cognition.Abstract = (_, _, _) => [new AbstractionProposal("Mixed depth is invalid", [seed.Id, higher.Id, old[0].Id])];

        var summary = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        var neighborhood = Assert.Single(fixture.Cognition.Neighborhoods);
        Assert.Contains(neighborhood, x => x.Id == higher.Id);
        Assert.True(MemoryTestData.CountAtDepth(neighborhood, 0) >= fixture.Options.RootBase);
        Assert.Equal(fixture.Options.NeighborhoodSize, neighborhood.Count);
        Assert.Equal(1, summary.RejectedProposals);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
    }

    [Fact]
    public async Task ExactDreamNeighborhoodIsProcessedOnceInOrdinalOrder()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(3, start.AddHours(1));
        fixture.Cognition.Abstract = (neighborhood, _, role) =>
        {
            Assert.Equal(CognitionRole.Dream, role);
            return [new AbstractionProposal(
                "A bounded pattern shared by these experiences.",
                MemoryTestData.Ids(neighborhood))];
        };

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        var neighborhood = Assert.Single(fixture.Cognition.Neighborhoods);
        var actualIds = MemoryTestData.Ids(neighborhood);
        var expectedIds = MemoryTestData.Ids(observations);
        expectedIds.Sort(StringComparer.Ordinal);
        Assert.Equal(expectedIds, actualIds);
        Assert.Equal(1, fixture.Cognition.EmbeddingCalls);
        Assert.Single(fixture.Store.ReadSnapshot().Memories, memory => memory.Depth == 1);
        var consolidationWork = fixture.Store.GetWorkItems(result.RunId)
            .Where(item => item.Phase == "consolidation").ToList();
        Assert.Equal(3, consolidationWork.Count);
        Assert.All(consolidationWork, item => Assert.Equal("complete", item.Status));
        Assert.Equal(2, consolidationWork.Count(item =>
            item.Model == "dream-neighborhood-deduplicated"));
    }

    [Fact]
    public async Task AlmostEqualDreamNeighborhoodsAreNotDeduplicated()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(4, start.AddHours(1));
        fixture.Search.Nearest = (seed, _) => seed.Id switch
        {
            var id when id == observations[0].Id => [observations[1], observations[2]],
            var id when id == observations[1].Id => [observations[0], observations[3]],
            var id when id == observations[2].Id => [observations[0], observations[1]],
            _ => [observations[0], observations[1]]
        };

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        Assert.Equal(2, fixture.Cognition.Neighborhoods.Count);
        var sets = fixture.Cognition.Neighborhoods
            .Select(neighborhood => MemoryTestData.Ids(neighborhood).Order(StringComparer.Ordinal).ToArray())
            .ToList();
        Assert.Contains(sets, ids => ids.SequenceEqual(
            new[] { observations[0].Id, observations[1].Id, observations[2].Id }
                .Order(StringComparer.Ordinal)));
        Assert.Contains(sets, ids => ids.SequenceEqual(
            new[] { observations[0].Id, observations[1].Id, observations[3].Id }
                .Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task SameDreamNeighborhoodCanBeProcessedAgainInNextRun()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(3, start.AddHours(1));

        var first = await fixture.Engine.DreamAsync(start, start.AddDays(1));
        fixture.Store.RecordRecall(MemoryTestData.Ids(observations), start.AddDays(1).AddHours(1));
        var second = await fixture.Engine.DreamAsync(start.AddDays(1), start.AddDays(2));

        Assert.Equal("complete", first.Status);
        Assert.Equal("complete", second.Status);
        Assert.Equal(2, fixture.Cognition.Neighborhoods.Count);
        Assert.Equal(
            MemoryTestData.Ids(fixture.Cognition.Neighborhoods[0]),
            MemoryTestData.Ids(fixture.Cognition.Neighborhoods[1]));
    }

    [Fact]
    public async Task SavedDreamProposalResumesThenSuppressesDuplicateWorkWithoutRepeatingReasoning()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(3, start.AddHours(1));
        var parents = MemoryTestData.Ids(observations);
        var abstractCalls = 0;
        fixture.Cognition.Abstract = (_, _, _) =>
        {
            abstractCalls++;
            return [new AbstractionProposal("First possible pattern", parents)];
        };
        fixture.Cognition.Embedding = (_, _) => throw new IOException("simulated crash");

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Engine.DreamAsync(start, start.AddDays(1)));
        var interrupted = Assert.Single(fixture.Store.GetRuns());
        Assert.Equal("running", interrupted.Status);
        Assert.Contains(fixture.Store.GetWorkItems(interrupted.Id), item =>
            item.Status == "pending" && item.ProposalJson is not null);

        fixture.Cognition.Embedding = (_, _) => ConsolidationFixture.Vector;
        var resumed = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", resumed.Status);
        Assert.Equal(1, abstractCalls);
        Assert.Single(fixture.Store.ReadSnapshot().Memories, memory => memory.Depth == 1);
        var work = fixture.Store.GetWorkItems(resumed.RunId);
        Assert.All(work, item => Assert.Equal("complete", item.Status));
        Assert.Equal(2, work.Count(item =>
            item.Phase == "consolidation" && item.Model == "dream-neighborhood-deduplicated"));
        var callsBeforeTerminalRetry = fixture.Cognition.Calls.Count;
        await fixture.Engine.DreamAsync(start, start.AddDays(1));
        Assert.Equal(callsBeforeTerminalRetry, fixture.Cognition.Calls.Count);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task EmptyOrInvalidFirstDreamProposalStillSuppressesDuplicateWork(
        bool invalid, int expectedRejections)
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(3, start.AddHours(1));
        var calls = 0;
        fixture.Cognition.Abstract = (_, _, _) =>
        {
            calls++;
            return invalid
                ? [new AbstractionProposal(
                    "Invalid duplicate evidence.",
                    [observations[0].Id, observations[0].Id, observations[1].Id])]
                : [];
        };

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        Assert.Equal(1, calls);
        Assert.Equal(expectedRejections, result.RejectedProposals);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
        Assert.DoesNotContain(fixture.Store.ReadSnapshot().Memories, memory => memory.Depth > 0);
    }

    [Fact]
    public async Task DreamAppliesOnlyFirstReturnedAbstractionAndRejectsExtraIndividually()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(3, start.AddHours(1));
        var parents = MemoryTestData.Ids(observations);
        fixture.Cognition.Abstract = (_, _, _) =>
        [
            new AbstractionProposal("First bounded pattern.", parents),
            new AbstractionProposal("Second bounded pattern.", parents)
        ];

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        Assert.Equal(1, result.RejectedProposals);
        Assert.Equal(1, fixture.Cognition.EmbeddingCalls);
        Assert.Single(fixture.Store.ReadSnapshot().Memories, memory => memory.Depth == 1);
    }

    [Fact]
    public async Task DreamLeavesAlreadyAppliedLegacyExtraOutputUntouched()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(3, start.AddHours(1));
        var parentIds = MemoryTestData.Ids(observations);
        var run = fixture.Store.GetOrCreateRun(
            RunKind.Dream, start, start.AddDays(1), fixture.Clock.Now, null);
        var workKey = $"abstract:{observations[0].Id}";
        fixture.Store.EnsureWorkItems(run.Id,
            [new WorkSeed(workKey, "consolidation", observations[0].Id, 0)]);
        var first = new AbstractionProposal("First legacy pattern.", parentIds);
        var second = new AbstractionProposal("Second legacy pattern.", parentIds);
        var proposalJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            allowed_candidate_ids = parentIds,
            relations = Array.Empty<RelationProposal>(),
            abstractions = new[] { first, second }
        }, JsonDefaults.Options);
        fixture.Store.SaveWorkProposal(run.Id, workKey, proposalJson, "legacy-model");
        fixture.Store.AddAbstraction(
            second, "legacy-model", run, workKey, 1, parentIds,
            ConsolidationFixture.Vector, fixture.Clock.Now);

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        Assert.Equal(0, result.RejectedProposals);
        Assert.Equal(1, fixture.Cognition.EmbeddingCalls);
        Assert.Equal(2, MemoryTestData.CountAtDepth(fixture.Store.ReadSnapshot().Memories, 1));
    }

    [Fact]
    public async Task WeeklyChangesIncludeOldRelationOwnersAndFollowLlmOrderInsteadOfNegativeCounts()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-7);
        var roots = fixture.Observations(9, start.AddDays(-10));
        var first = fixture.Abstraction(roots[..3], start.AddDays(-8));
        var second = fixture.Abstraction(roots[3..6], start.AddDays(-8));
        var recent = fixture.Abstraction(roots[6..], start.AddDays(1));
        var evidence = fixture.Observations(1, start.AddDays(2))[0];
        fixture.Relate(first, evidence, RelationKind.Negative, start.AddDays(3));
        fixture.Relate(second, evidence, RelationKind.Positive, start.AddDays(4));
        fixture.Cognition.Prioritize = (_, _) =>
            [$"abstract:{second.Id}", $"abstract:{recent.Id}", $"abstract:{first.Id}"];

        var summary = await fixture.Engine.MeditateAsync(start, start.AddDays(7));

        var work = fixture.Store.GetWorkItems(summary.RunId);
        var expectedIds = new[] { first.Id, second.Id, recent.Id };
        Array.Sort(expectedIds);
        Assert.Equal(expectedIds, SortedWorkMemoryIds(work));
        Assert.Equal(second.Id, work[0].MemoryId);
        Assert.Equal(recent.Id, work[1].MemoryId);
        Assert.Equal(first.Id, work[2].MemoryId);
        Assert.Equal(second.Id, fixture.Cognition.Neighborhoods[0][0].Id);
        Assert.Equal(recent.Id, fixture.Cognition.Neighborhoods[1][0].Id);
        Assert.Equal(first.Id, fixture.Cognition.Neighborhoods[2][0].Id);
        var priorityCandidates = Assert.Single(fixture.Cognition.PriorityBatches);
        Assert.Equal(3, priorityCandidates.Count);
        var changedOwner = Assert.Single(priorityCandidates, candidate => candidate.Memory.Id == first.Id);
        Assert.Equal(evidence.Id, Assert.Single(changedOwner.RelatedMemories).Id);
        Assert.Equal(start.AddDays(3), Assert.Single(changedOwner.Memory.Relations).RelatedAt);
        Assert.DoesNotContain(work, x => x.MemoryId == evidence.Id);
        Assert.All(fixture.Cognition.Roles, x => Assert.Equal(CognitionRole.Meditation, x));
        Assert.All(fixture.Cognition.SourceBatches, x => Assert.NotEmpty(x));
        Assert.All(fixture.Cognition.Contexts, x => Assert.Equal(summary.RunId, x.RunId));
    }

    [Fact]
    public async Task WeeklyBudgetCarriesPartialProposalWithoutPayingForAlreadyAppliedOutputAgain()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.MeditationBudgetUsd = 0.3m;
        var start = fixture.Clock.Now.AddDays(-7);
        var roots = fixture.Observations(9, start.AddDays(-10));
        var parents = new MemoryRecord[3];
        for (var index = 0; index < parents.Length; index++)
        {
            var firstRoot = index * 3;
            var rootGroup = roots[firstRoot..(firstRoot + 3)];
            parents[index] = fixture.Abstraction(rootGroup, start.AddDays(1));
        }

        var parentIds = MemoryTestData.Ids(parents);
        var abstractCalls = 0;
        fixture.Cognition.Abstract = (_, _, _) =>
        {
            abstractCalls++;
            if (abstractCalls == 1)
            {
                return
                [
                    new AbstractionProposal("Possible higher pattern one", parentIds),
                    new AbstractionProposal("Possible higher pattern two", parentIds)
                ];
            }

            return [];
        };
        fixture.Cognition.Embedding = (_, context) =>
        {
            var reservation = fixture.Store.ReserveUsage(context.RunId, "fake", "embedding", 0.2m, fixture.Clock.Now);
            fixture.Store.CompleteUsage(reservation.Id, new ApiUsage(1, 0, 0, 0.2m), fixture.Clock.Now);
            return ConsolidationFixture.Vector;
        };

        var exhausted = await fixture.Engine.MeditateAsync(start, start.AddDays(7));
        Assert.Equal("budget_exhausted", exhausted.Status);
        Assert.Equal(0.2m, exhausted.AccountedUsd);
        Assert.Single(fixture.Store.ReadSnapshot().Memories, x => x.Depth == 2);
        RunWorkItem? originWork = null;
        foreach (var item in fixture.Store.GetWorkItems(exhausted.RunId))
        {
            if (item.ProposalJson is not null)
            {
                originWork = item;
                break;
            }
        }

        Assert.NotNull(originWork);
        Assert.Equal("pending", originWork.Status);

        fixture.Clock.Now = fixture.Clock.Now.AddDays(7);
        var next = await fixture.Engine.MeditateAsync(start.AddDays(7), start.AddDays(14));
        Assert.Equal("complete", next.Status);
        Assert.Equal(0.2m, next.AccountedUsd);
        Assert.Equal(2, MemoryTestData.CountAtDepth(fixture.Store.ReadSnapshot().Memories, 2));
        var completedOrigin = Assert.Single(
            fixture.Store.GetWorkItems(exhausted.RunId), item => item.Key == originWork.Key);
        Assert.Equal("complete", completedOrigin.Status);
        Assert.Contains(fixture.Store.GetWorkItems(next.RunId), x => x.Key.StartsWith("carry:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnconfiguredWeeklyBudgetDoesNotCreateRun()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.MeditationBudgetUsd = null;
        await Assert.ThrowsAsync<InputException>(() => fixture.Engine.MeditateAsync(fixture.Clock.Now.AddDays(-7), fixture.Clock.Now));
        Assert.Empty(fixture.Store.GetRuns());
    }

    private static IReadOnlyList<string> SortedWorkMemoryIds(IReadOnlyList<RunWorkItem> work, string? phase = null)
    {
        var memoryIds = new List<string>();
        foreach (var item in work)
        {
            if (phase is null || item.Phase == phase)
            {
                memoryIds.Add(item.MemoryId);
            }
        }

        memoryIds.Sort();
        return memoryIds;
    }
}
