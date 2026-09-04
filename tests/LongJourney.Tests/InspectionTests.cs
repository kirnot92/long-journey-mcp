using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class InspectionTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MemoryPagesKeepTheirSequenceBoundaryAndUseLiteralCaseSensitiveSearch()
    {
        using var fixture = new ConsolidationFixture();
        var memories = fixture.Observations(30, At);
        var first = fixture.Store.BrowseMemories(new InspectionMemoryQuery());
        Assert.Equal(30, first.Memories.Total);
        Assert.Equal(25, first.Memories.Items.Count);
        Assert.Equal(memories[29].Id, first.Memories.Items[0].Id);
        Assert.Equal(memories[5].Id, first.Memories.Items[24].Id);
        Assert.Equal(30, first.Statistics.Memories);
        Assert.Equal(30, first.Statistics.Sources);
        Assert.Equal(30, Assert.Single(first.Statistics.Depths).Count);

        fixture.Observations(1, At.AddDays(1));
        var second = fixture.Store.BrowseMemories(new InspectionMemoryQuery(Page: 2, Snapshot: first.Memories.Snapshot));
        Assert.Equal(30, second.Memories.Total);
        Assert.Equal(5, second.Memories.Items.Count);
        Assert.Equal(memories[4].Id, second.Memories.Items[0].Id);
        Assert.Equal(memories[0].Id, second.Memories.Items[4].Id);

        var special = AddObservation(fixture.Store, "Literal %_ ' MiXeD", At);
        Assert.Single(fixture.Store.BrowseMemories(new InspectionMemoryQuery(Search: "%_")).Memories.Items);
        Assert.Single(fixture.Store.BrowseMemories(new InspectionMemoryQuery(Search: special.Id)).Memories.Items);
        Assert.Single(fixture.Store.BrowseMemories(new InspectionMemoryQuery(Search: "MiXeD")).Memories.Items);
        Assert.Empty(fixture.Store.BrowseMemories(new InspectionMemoryQuery(Search: "mixed")).Memories.Items);
        Assert.Empty(fixture.Store.BrowseMemories(new InspectionMemoryQuery(Depth: 1)).Memories.Items);
        Assert.Throws<InputException>(() => fixture.Store.BrowseMemories(new InspectionMemoryQuery(Page: 0)));
        Assert.Throws<InputException>(() => fixture.Store.BrowseMemories(new InspectionMemoryQuery(Depth: -1)));
    }

    [Fact]
    public void TracePreservesSharedParentsAndRecentRelationsKeepTheirStoredDirection()
    {
        using var fixture = new ConsolidationFixture();
        var roots = fixture.Observations(9, At);
        var first = fixture.Abstraction([roots[0], roots[1], roots[2]], At);
        var second = fixture.Abstraction([roots[0], roots[3], roots[4]], At);
        var third = fixture.Abstraction([roots[5], roots[6], roots[7], roots[8]], At);
        var top = fixture.Abstraction([first, second, third], At);
        var unrelated = fixture.Observations(2, At);
        fixture.Relate(top, unrelated[0], RelationKind.Positive, At.AddMinutes(1));
        fixture.Relate(unrelated[1], top, RelationKind.Negative, At.AddMinutes(2));
        fixture.Store.RecordRecall([top.Id], At.AddMinutes(3));

        var trace = Assert.IsType<InspectionTrace>(fixture.Store.ReadTrace(top.Id));
        Assert.False(trace.Truncated);
        Assert.Equal(13, trace.Memories.Count);
        Assert.Single(trace.Memories, memory => memory.Id == roots[0].Id);
        Assert.Contains(roots[0].Id, Assert.Single(trace.Memories, memory => memory.Id == first.Id).DerivedFrom);
        Assert.Contains(roots[0].Id, Assert.Single(trace.Memories, memory => memory.Id == second.Id).DerivedFrom);
        Assert.DoesNotContain(trace.Memories, memory => memory.Id == unrelated[0].Id || memory.Id == unrelated[1].Id);
        Assert.Null(fixture.Store.ReadTrace("absent"));
        var detail = Assert.IsType<MemoryRecord>(fixture.Store.GetMemory(top.Id));
        Assert.Equal(unrelated[0].Id, Assert.Single(detail.Relations).RelatedMemoryId);
        Assert.Empty(fixture.Store.GetMemory(unrelated[0].Id)!.Relations);
        Assert.Equal(At.AddMinutes(3), detail.LastRecalledAt);

        var overview = fixture.Store.BrowseMemories(new InspectionMemoryQuery(Depth: 2));
        Assert.Single(overview.Memories.Items);
        Assert.Equal(2, overview.Statistics.Relations);
        Assert.Equal(unrelated[1].Id, overview.RecentRelations[0].MemoryId);
        Assert.Equal(top.Id, overview.RecentRelations[0].RelatedMemoryId);
        Assert.Equal(At.AddMinutes(2), overview.RecentRelations[0].RelatedAt);
    }

    [Fact]
    public void LargeTraceHasAnExplicitBoundAndKeepsLinksToUnloadedParents()
    {
        using var fixture = new ConsolidationFixture();
        var roots = fixture.Observations(205, At);
        var top = fixture.Abstraction(roots, At);
        var trace = Assert.IsType<InspectionTrace>(fixture.Store.ReadTrace(top.Id));
        Assert.True(trace.Truncated);
        Assert.Equal(InspectionTrace.NodeLimit, trace.Memories.Count);
        var topNode = Assert.Single(trace.Memories, memory => memory.Id == top.Id);
        Assert.Equal(205, topNode.DerivedFrom.Count);
        var visible = new HashSet<string>(MemoryTestData.Ids(trace.Memories));
        Assert.Contains(topNode.DerivedFrom, parent => !visible.Contains(parent));
        var continued = fixture.Store.ReadTrace(roots[0].Id)!;
        Assert.False(continued.Truncated);
        Assert.Equal(roots[0].SourceRef, Assert.Single(continued.Memories).SourceRef);
    }

    [Fact]
    public void SourceReadPreservesRawAndRetainsMetadataAndObservationsWhenFileIsMissing()
    {
        using var fixture = new ConsolidationFixture();
        const string raw = "\n  <script>raw & text</script>\r\n\tlast line  ";
        var memory = AddObservation(fixture.Store, raw, At);
        var source = Assert.IsType<InspectionSource>(fixture.Store.InspectSource(memory.SourceRef!));
        Assert.Equal(raw, source.Raw);
        Assert.Null(source.ReadError);
        Assert.Equal(memory.Id, Assert.Single(source.Observations.Items).Id);

        File.Delete(Path.Combine(fixture.Options.DataDirectory, source.Source.RelativePath));
        var unavailable = fixture.Store.InspectSource(memory.SourceRef!)!;
        Assert.Null(unavailable.Raw);
        Assert.NotNull(unavailable.ReadError);
        Assert.Equal(source.Source, unavailable.Source);
        Assert.Equal(memory.Id, Assert.Single(unavailable.Observations.Items).Id);
        Assert.Null(fixture.Store.InspectSource("../../missing"));
    }

    [Fact]
    public void RunInspectionSeparatesOriginOutputsAndChargedDecimalCostsWithoutInferringCompletion()
    {
        using var fixture = new ConsolidationFixture();
        var roots = fixture.Observations(3, At);
        var origin = fixture.Store.GetOrCreateRun(RunKind.Meditation, At, At.AddDays(7), At, 2m);
        fixture.Store.EnsureWorkItems(origin.Id, [new WorkSeed("abstract:seed", "consolidation", roots[0].Id, 0)]);
        const string proposal = "{\"content\":\"<script>proposal</script>\"}";
        fixture.Store.SaveWorkProposal(origin.Id, "abstract:seed", proposal, "stored-model");
        fixture.Store.RejectProposal(origin.Id, "abstract:seed", 1, "stored rejection");
        var parentIds = MemoryTestData.Ids(roots);
        var output = fixture.Store.AddAbstraction(new AbstractionProposal("output", parentIds),
            "stored-model", origin, "abstract:seed", 0, parentIds, ConsolidationFixture.Vector, At);
        var originCall = fixture.Store.ReserveUsage(origin.Id, "fake", "origin", .2m, At);
        fixture.Store.CompleteUsage(originCall.Id, new ApiUsage(1, 0, 1, .1234567890123456789m), At);
        fixture.Store.FinishRun(origin.Id, "budget_exhausted", At.AddMinutes(1));

        var charged = fixture.Store.GetOrCreateRun(RunKind.Meditation, At.AddDays(7), At.AddDays(14), At.AddDays(7), 2m);
        var carryKey = $"carry:{origin.Id}:abstract:seed";
        fixture.Store.EnsureWorkItems(charged.Id, [new WorkSeed(carryKey, "carry", roots[0].Id, 0)]);
        var settled = fixture.Store.ReserveUsage(charged.Id, "fake", "settled", .9m, At);
        fixture.Store.CompleteUsage(settled.Id, new ApiUsage(1, 0, 1, .4m), At);
        fixture.Store.ReserveUsage(charged.Id, "fake", "unknown", .6m, At);
        var zero = fixture.Store.ReserveUsage(charged.Id, "fake", "zero", .3m, At);
        fixture.Store.CompleteUsage(zero.Id, new ApiUsage(0, 0, 0, 0m), At);

        var current = fixture.Store.InspectRun(charged.Id)!;
        Assert.Equal(.4m, current.Cost.ActualUsd);
        Assert.Equal(.6m, current.Cost.UnsettledReservedUsd);
        Assert.Equal(1m, current.Cost.AccountedUsd);
        Assert.Equal(1, current.Cost.UnsettledCalls);
        Assert.Equal(0, current.CompletedWork);
        Assert.Equal(0, current.OutputMemories);
        Assert.True(current.Run.WorkInitialized);
        Assert.Null(current.Run.FinishedAt);
        Assert.Equal("running", current.Run.Run.Status);
        var older = fixture.Store.InspectRun(origin.Id)!;
        Assert.Equal(.1234567890123456789m, older.Cost.ActualUsd);
        Assert.Equal(0m, older.Cost.UnsettledReservedUsd);
        Assert.Equal(At.AddMinutes(1), older.Run.FinishedAt);
        Assert.Equal("budget_exhausted", older.Run.Run.Status);
        Assert.Equal(1, older.OutputMemories);
        Assert.Equal(output.Id, Assert.Single(fixture.Store.BrowseMemories(new InspectionMemoryQuery(Revision: origin.Id)).Memories.Items).Id);
        var carry = fixture.Store.InspectWork(charged.Id, carryKey)!;
        Assert.Equal(new InspectionWorkOrigin(origin.Id, "abstract:seed"), carry.Origin);
        Assert.Null(carry.Work.ProposalJson);
        var saved = fixture.Store.InspectWork(origin.Id, "abstract:seed")!;
        Assert.Equal(proposal, saved.Work.ProposalJson);
        Assert.Equal("stored rejection", Assert.Single(saved.Rejections).Reason);
        Assert.Null(fixture.Store.InspectRun(long.MaxValue));
        Assert.Null(fixture.Store.InspectWork(charged.Id, "missing"));
    }

    [Fact]
    public void RunAndWorkListsArePagedAndEmptyStateIsAccurate()
    {
        using var fixture = new ConsolidationFixture();
        Assert.Empty(fixture.Store.BrowseRuns().Items);
        Assert.Empty(fixture.Store.BrowseMemories(new InspectionMemoryQuery()).Memories.Items);
        var seed = fixture.Observations(1, At)[0];
        for (var index = 0; index < 27; index++)
        {
            var run = fixture.Store.GetOrCreateRun(RunKind.Dream, At.AddDays(index), At.AddDays(index + 1), At, null);
            if (index == 26)
            {
                var work = new List<WorkSeed>();
                for (var ordinal = 0; ordinal < 27; ordinal++)
                {
                    work.Add(new WorkSeed("item:" + ordinal, "assimilation", seed.Id, ordinal));
                }

                fixture.Store.EnsureWorkItems(run.Id, work);
                var detail = fixture.Store.InspectRun(run.Id, 2)!;
                Assert.Equal(27, detail.Work.Total);
                Assert.Equal(2, detail.Work.Items.Count);
                Assert.Equal(25, detail.Work.Items[0].Ordinal);
            }
        }

        var first = fixture.Store.BrowseRuns();
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(27, first.Items[0].Run.Id);
        var second = fixture.Store.BrowseRuns(2, first.Snapshot);
        Assert.Equal(2, second.Items.Count);
        Assert.Equal(2, second.Items[0].Run.Id);
    }

    private static MemoryRecord AddObservation(SqliteMemoryStore store, string raw, DateTimeOffset at)
    {
        var artifact = store.SaveSource(raw, at);
        Assert.True(store.ClaimSource(artifact.Source.Id));
        store.CompleteSource(artifact.Source.Id, [new NewObservation(raw, "test", ConsolidationFixture.Vector)], at);
        return Assert.Single(store.GetSourceMemories(artifact.Source.Id));
    }
}
