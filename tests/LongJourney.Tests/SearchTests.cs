using LongJourney.Core;
using Microsoft.Data.Sqlite;

namespace LongJourney.Tests;

public sealed class SearchTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SearchWithNoResultCapacityDoesNotGenerateEmbeddings(int limit)
    {
        using var fixture = new ConsolidationFixture();
        var memory = Assert.Single(fixture.Observations(1, fixture.Clock.Now));
        RemoveEmbedding(fixture.Store, memory.Id);
        var search = new MemorySearch(fixture.Store, fixture.Cognition, fixture.Options);

        var results = await search.SearchAsync(
            "observation", new CallContext(), default, limit: limit);

        Assert.Empty(results);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
        Assert.Null(fixture.Store.GetEmbedding(memory.Id, fixture.Cognition.EmbeddingSpace));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NearestWithNoResultCapacityDoesNotGenerateEmbeddings(int limit)
    {
        using var fixture = new ConsolidationFixture();
        var memories = fixture.Observations(2, fixture.Clock.Now);
        var seed = memories[0];
        RemoveEmbedding(fixture.Store, seed.Id);
        var snapshot = fixture.Store.ReadSnapshot();
        var search = new MemorySearch(fixture.Store, fixture.Cognition, fixture.Options);

        var results = await search.NearestAsync(
            seed, snapshot, new CallContext(), default, limit: limit);

        Assert.Empty(results);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
        Assert.Null(fixture.Store.GetEmbedding(seed.Id, fixture.Cognition.EmbeddingSpace));
    }

    [Fact]
    public async Task SearchRanksNewlyGeneratedAndStoredVectorsTogether()
    {
        using var fixture = new ConsolidationFixture();
        var memories = fixture.Observations(3, fixture.Clock.Now);
        var missingVectorMemory = memories[1];
        RemoveEmbedding(fixture.Store, missingVectorMemory.Id);
        var search = new MemorySearch(fixture.Store, fixture.Cognition, fixture.Options);

        // No lexical match: all three equal vectors must participate in the semantic tie.
        var results = await search.SearchAsync("unmatchedtoken", new CallContext(), default);

        var expectedIds = MemoryTestData.Ids(memories);
        expectedIds.Sort(StringComparer.Ordinal);
        Assert.Equal(expectedIds, MemoryTestData.Ids(results));
        Assert.Equal(2, fixture.Cognition.EmbeddingCalls); // One missing vector and the query.
        Assert.NotNull(fixture.Store.GetEmbedding(missingVectorMemory.Id, fixture.Cognition.EmbeddingSpace));
    }

    [Fact]
    public async Task NearestUsesNewlyGeneratedSeedAndCandidateVectors()
    {
        using var fixture = new ConsolidationFixture();
        var memories = fixture.Observations(3, fixture.Clock.Now);
        var seed = memories[0];
        var missingVectorMemory = memories[1];
        RemoveEmbedding(fixture.Store, seed.Id);
        RemoveEmbedding(fixture.Store, missingVectorMemory.Id);
        var snapshot = fixture.Store.ReadSnapshot();
        var search = new MemorySearch(fixture.Store, fixture.Cognition, fixture.Options);

        var results = await search.NearestAsync(seed, snapshot, new CallContext(), default);

        var expectedIds = new List<string> { memories[1].Id, memories[2].Id };
        expectedIds.Sort(StringComparer.Ordinal);
        Assert.Equal(expectedIds, MemoryTestData.Ids(results));
        Assert.Equal(2, fixture.Cognition.EmbeddingCalls);
        Assert.NotNull(fixture.Store.GetEmbedding(seed.Id, fixture.Cognition.EmbeddingSpace));
        Assert.NotNull(fixture.Store.GetEmbedding(missingVectorMemory.Id, fixture.Cognition.EmbeddingSpace));
    }

    private static void RemoveEmbedding(SqliteMemoryStore store, string memoryId)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM embeddings WHERE memory_id=$memory";
        command.Parameters.AddWithValue("$memory", memoryId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
