using System.Text.Json;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed partial class ConsolidationTests
{
    [Fact]
    public async Task IneligibleDreamWorkIsSavedAndDeduplicatedAfterReopening()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(2, start.AddHours(1));
        var searches = 0;
        fixture.Search.Nearest = (_, _) =>
        {
            if (++searches == 2)
            {
                throw new IOException("Simulated interruption after the first skipped work item.");
            }

            return observations;
        };

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Engine.DreamAsync(start, start.AddDays(1)));

        var run = Assert.Single(fixture.Store.GetRuns());
        var skipped = Assert.Single(fixture.Store.GetWorkItems(run.Id), item =>
            item.Model == "consolidation-ineligible");
        Assert.Equal("complete", skipped.Status);
        using var proposal = JsonDocument.Parse(skipped.ProposalJson!);
        Assert.Equal(2, proposal.RootElement.GetProperty("allowed_candidate_ids").GetArrayLength());
        Assert.Equal(0, proposal.RootElement.GetProperty("abstractions").GetArrayLength());
        Assert.Empty(fixture.Cognition.Neighborhoods);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
        Assert.Equal(["assimilation", "assimilation"], fixture.Cognition.Calls);

        var reopened = new SqliteMemoryStore(fixture.Options);
        var resumedCognition = new ConsolidationCognition();
        var engine = new ConsolidationEngine(
            reopened, resumedCognition, new ConsolidationSearch(), fixture.Options, fixture.Clock);
        var result = await engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        Assert.Equal(0, result.RejectedProposals);
        Assert.Empty(resumedCognition.Calls);
        Assert.Equal(0, resumedCognition.EmbeddingCalls);
        Assert.Single(reopened.GetWorkItems(run.Id), item =>
            item.Model == "dream-neighborhood-deduplicated");
        Assert.All(reopened.GetWorkItems(run.Id), item => Assert.Equal("complete", item.Status));
    }

    [Fact]
    public async Task MultipleObservationsFromOneSourceCannotSupplyDistinctRoots()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.MaxObservations = 3;
        var start = fixture.Clock.Now.AddDays(-1);
        var source = fixture.Store.SaveSource("One experience with three observations.", start.AddHours(1));
        Assert.True(fixture.Store.ClaimSource(source.Source.Id));
        fixture.Store.CompleteSource(source.Source.Id,
        [
            new NewObservation("First aspect", "fake", ConsolidationFixture.Vector),
            new NewObservation("Second aspect", "fake", ConsolidationFixture.Vector),
            new NewObservation("Third aspect", "fake", ConsolidationFixture.Vector)
        ], start.AddHours(1));

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        Assert.Equal(0, result.RejectedProposals);
        Assert.Empty(fixture.Cognition.Neighborhoods);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
        Assert.Single(fixture.Store.GetWorkItems(result.RunId), item =>
            item.Model == "consolidation-ineligible");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HigherDepthRequiresExactRootUnionFromAncestorsOutsideNeighborhood(bool disjoint)
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var roots = fixture.Observations(9, start.AddDays(-10));
        var parents = new List<MemoryRecord>();
        for (var index = 0; index < 3; index++)
        {
            var offset = !disjoint && index == 2 ? 5 : index * 3;
            parents.Add(fixture.Abstraction(roots[offset..(offset + 3)], start.AddDays(-5)));
        }
        fixture.Store.RecordRecall(MemoryTestData.Ids(parents), start.AddHours(1));
        fixture.Cognition.Abstract = (neighborhood, _, _) =>
        {
            Assert.All(neighborhood, memory => Assert.Equal(1, memory.Depth));
            return [new AbstractionProposal("A supported higher pattern.", MemoryTestData.Ids(neighborhood))];
        };

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        Assert.Equal(0, result.RejectedProposals);
        Assert.Equal(disjoint ? 1 : 0, fixture.Cognition.Neighborhoods.Count);
        Assert.Equal(disjoint ? 1 : 0, fixture.Cognition.EmbeddingCalls);
        Assert.Equal(disjoint ? 1 : 0,
            MemoryTestData.CountAtDepth(fixture.Store.ReadSnapshot().Memories, 2));
    }

    [Fact]
    public async Task RootsFromDifferentCandidateLayersCannotBeCombined()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var roots = fixture.Observations(10, start.AddDays(-10));
        var parents = new List<MemoryRecord>();
        foreach (var offset in new[] { 0, 3, 5 })
        {
            parents.Add(fixture.Abstraction(roots[offset..(offset + 3)], start.AddDays(-5)));
        }
        fixture.Relate(parents[0], roots[8], RelationKind.Positive, start.AddDays(-2));
        fixture.Relate(parents[0], roots[9], RelationKind.Positive, start.AddDays(-2));
        fixture.Store.RecordRecall([parents[0].Id], start.AddHours(1));

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        Assert.Equal(0, result.RejectedProposals);
        Assert.Empty(fixture.Cognition.Neighborhoods);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
        var skipped = Assert.Single(fixture.Store.GetWorkItems(result.RunId));
        Assert.Equal("consolidation-ineligible", skipped.Model);
        using var proposal = JsonDocument.Parse(skipped.ProposalJson!);
        Assert.Equal(5, proposal.RootElement.GetProperty("allowed_candidate_ids").GetArrayLength());
    }

    [Fact]
    public async Task MoreThanRootBaseParentsCanJointlyReachTheRequiredRootCount()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var roots = fixture.Observations(9, start.AddDays(-10));
        var parents = new List<MemoryRecord>();
        for (var index = 0; index < 4; index++)
        {
            var offset = 1 + index * 2;
            parents.Add(fixture.Abstraction(
                [roots[0], roots[offset], roots[offset + 1]], start.AddDays(-5)));
        }
        fixture.Store.RecordRecall([parents[0].Id], start.AddHours(1));
        fixture.Cognition.Abstract = (neighborhood, _, _) =>
            [new AbstractionProposal("All four parents are needed.", MemoryTestData.Ids(neighborhood))];

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal("complete", result.Status);
        Assert.Equal(4, Assert.Single(fixture.Cognition.Neighborhoods).Count);
        var created = Assert.Single(fixture.Store.ReadSnapshot().Memories, memory => memory.Depth == 2);
        Assert.Equal(9, created.UniqueSourceRootCount);
        Assert.Equal(4, created.DerivedFrom.Count);
        Assert.Equal(1, fixture.Cognition.EmbeddingCalls);
    }

    [Fact]
    public async Task MeditationCanUseAFeasibleLayerOtherThanTheSeedLayer()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-7);
        var roots = fixture.Observations(3, start.AddDays(-10));
        fixture.Abstraction(roots, start.AddDays(1));
        fixture.Cognition.Abstract = (neighborhood, _, _) =>
        {
            Assert.Equal(1, neighborhood[0].Depth);
            return [new AbstractionProposal("Another supported view.", MemoryTestData.Ids(roots))];
        };

        var result = await fixture.Engine.MeditateAsync(start, start.AddDays(7));

        Assert.Equal("complete", result.Status);
        Assert.Equal(0, result.RejectedProposals);
        Assert.Single(fixture.Cognition.Neighborhoods);
        Assert.Equal(3, Assert.Single(fixture.Cognition.SourceBatches).Count);
        Assert.Equal(1, fixture.Cognition.EmbeddingCalls);
        Assert.Equal(2, MemoryTestData.CountAtDepth(fixture.Store.ReadSnapshot().Memories, 1));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task IneligibleMeditationSkipsRawSourceReadsAndAbstractionCalls(int graphLimit)
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.NeighborhoodSize = 3;
        fixture.Options.MeditationGraphLimit = graphLimit;
        var start = fixture.Clock.Now.AddDays(-7);
        var roots = fixture.Observations(3, start.AddDays(-10));
        for (var index = 0; index < 3; index++)
        {
            fixture.Abstraction(roots, start.AddDays(1));
        }
        var source = fixture.Store.ReadSource(roots[0].SourceRef!);
        File.Delete(Path.Combine(fixture.Options.DataDirectory, source.Source.RelativePath));

        var result = await fixture.Engine.MeditateAsync(start, start.AddDays(7));

        Assert.Equal("complete", result.Status);
        Assert.Equal(0, result.RejectedProposals);
        Assert.Equal(["priority"], fixture.Cognition.Calls);
        Assert.Empty(fixture.Cognition.SourceBatches);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
        Assert.All(fixture.Store.GetWorkItems(result.RunId), item =>
        {
            Assert.Equal("complete", item.Status);
            Assert.Equal("consolidation-ineligible", item.Model);
            Assert.NotNull(item.ProposalJson);
        });
    }
}
