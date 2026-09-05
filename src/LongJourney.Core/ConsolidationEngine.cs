using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace LongJourney.Core;

/// <summary>Runs durable consolidation jobs over a graph frozen at the start of each run.</summary>
public sealed class ConsolidationEngine(
    IMemoryStore store,
    ICognition cognition,
    IMemorySearch search,
    EngineOptions options,
    TimeProvider timeProvider)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task<RunSummary> DreamAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(RunKind.Dream, start, end, cancellationToken);
    }

    public Task<RunSummary> MeditateAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(RunKind.Meditation, start, end, cancellationToken);
    }

    private async Task<RunSummary> ExecuteAsync(
        RunKind kind,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        if (start >= end)
        {
            throw new InputException("A consolidation period must have start before end.");
        }

        if (kind == RunKind.Meditation && options.MeditationBudgetUsd is null)
        {
            throw new InputException("Weekly Meditation is deferred until MeditationBudgetUsd is configured.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var budget = kind == RunKind.Meditation ? options.MeditationBudgetUsd : null;
            var run = store.GetOrCreateRun(kind, start, end, timeProvider.GetUtcNow(), budget);
            if (IsTerminal(run.Status))
            {
                return ReadRunSummary(run.Id, run.Status);
            }

            // Frozen evidence may be shared by many carried items, but work status stays live.
            var frozenRuns = new Dictionary<long, FrozenRunEvidence>();
            var frozenEvidence = ReadFrozenRun(run, frozenRuns).Snapshot;
            if (kind == RunKind.Dream)
            {
                EnsureDreamWorkItems(run, frozenEvidence);
            }
            else if (!await EnsureMeditationWorkItemsAsync(run, frozenEvidence, frozenRuns, cancellationToken))
            {
                return ReadRunSummary(run.Id, "budget_exhausted");
            }

            try
            {
                var workItems = new List<RunWorkItem>(store.GetWorkItems(run.Id));
                workItems.Sort(CompareWorkOrder);
                var dreamNeighborhoodKeys = kind == RunKind.Dream
                    ? RestoreDreamNeighborhoodKeys(workItems)
                    : null;
                foreach (var item in workItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.Status == "complete")
                    {
                        continue;
                    }

                    await ProcessScheduledWorkAsync(
                        run, item, frozenRuns, dreamNeighborhoodKeys, cancellationToken);
                }

                store.FinishRun(run.Id, "complete", timeProvider.GetUtcNow());
                return ReadRunSummary(run.Id, "complete");
            }
            catch (BudgetExceededException) when (kind == RunKind.Meditation)
            {
                // Pending original work remains discoverable by a later weekly run.
                store.FinishRun(run.Id, "budget_exhausted", timeProvider.GetUtcNow());
                return ReadRunSummary(run.Id, "budget_exhausted");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ProcessScheduledWorkAsync(
        RunRecord chargedRun,
        RunWorkItem scheduledItem,
        Dictionary<long, FrozenRunEvidence> frozenRuns,
        HashSet<string>? dreamNeighborhoodKeys,
        CancellationToken cancellationToken)
    {
        var chargedCallContext = new CallContext(chargedRun.Id);
        if (!TryGetCarryOrigin(scheduledItem, out var originRunId, out var originKey))
        {
            await ProcessOriginalWorkAsync(
                chargedRun, scheduledItem, frozenRuns[chargedRun.Id].Snapshot,
                chargedCallContext, dreamNeighborhoodKeys, cancellationToken);
            return;
        }

        // Proposals and completion may have changed while earlier items were applied.
        var originalItem = ReadOriginalWorkItem(originRunId, originKey);
        if (originalItem.Status != "complete")
        {
            if (!frozenRuns.TryGetValue(originRunId, out var originalEvidence))
            {
                var originRun = ReadCarryOriginRun(originRunId);
                originalEvidence = ReadFrozenRun(originRun, frozenRuns);
            }

            // Evidence, revision, and work identity belong to the original run.
            // Only API charges belong to the run that is executing the carry item now.
            await ProcessOriginalWorkAsync(
                originalEvidence.Run, originalItem, originalEvidence.Snapshot,
                chargedCallContext, dreamNeighborhoodKeys, cancellationToken);
        }

        store.CompleteWork(chargedRun.Id, scheduledItem.Key);
    }

    private void EnsureDreamWorkItems(RunRecord run, GraphSnapshot snapshot)
    {
        var createdObservations = new List<MemoryRecord>();
        foreach (var memory in snapshot.Memories)
        {
            if (memory.Depth == 0 && InPeriod(memory.CreatedAt, run))
            {
                createdObservations.Add(memory);
            }
        }
        createdObservations.Sort(CompareCreationOrder);

        var recalledIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recall in snapshot.RecallEvents)
        {
            if (InPeriod(recall.RecalledAt, run))
            {
                recalledIds.Add(recall.MemoryId);
            }
        }

        var consolidationSeeds = new List<MemoryRecord>();
        var seedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in createdObservations)
        {
            if (seedIds.Add(observation.Id))
            {
                consolidationSeeds.Add(observation);
            }
        }

        foreach (var memory in snapshot.Memories)
        {
            if (recalledIds.Contains(memory.Id) && seedIds.Add(memory.Id))
            {
                consolidationSeeds.Add(memory);
            }
        }
        consolidationSeeds.Sort(CompareCreationOrder);

        // Complete every new observation's assimilation before processing abstractions.
        var work = new List<WorkSeed>();
        foreach (var observation in createdObservations)
        {
            work.Add(new WorkSeed($"assimilate:{observation.Id}", "assimilation", observation.Id, work.Count));
        }

        foreach (var seed in consolidationSeeds)
        {
            work.Add(new WorkSeed($"abstract:{seed.Id}", "consolidation", seed.Id, work.Count));
        }

        store.EnsureWorkItems(run.Id, work);
    }

    private async Task<bool> EnsureMeditationWorkItemsAsync(
        RunRecord run,
        GraphSnapshot snapshot,
        Dictionary<long, FrozenRunEvidence> frozenRuns,
        CancellationToken cancellationToken)
    {
        if (store.AreWorkItemsInitialized(run.Id))
        {
            return true;
        }

        var candidates = new List<MeditationCandidate>();
        foreach (var memory in snapshot.Memories)
        {
            if (memory.Depth < 1 || !HasChangeInPeriod(memory, run))
            {
                continue;
            }

            var workSeed = new WorkSeed($"abstract:{memory.Id}", "consolidation", memory.Id, 0);
            candidates.Add(CreateMeditationCandidate(workSeed, memory, run, snapshot.ById));
        }

        // Unfinished work keeps its original evidence, even when the same memory changed again this week.
        foreach (var previousRun in store.GetRuns())
        {
            if (previousRun.Kind != RunKind.Meditation ||
                previousRun.Id >= run.Id ||
                previousRun.Status != "budget_exhausted")
            {
                continue;
            }

            IReadOnlyDictionary<string, MemoryRecord>? originalMemories = null;
            foreach (var pendingItem in store.GetWorkItems(previousRun.Id))
            {
                if (pendingItem.Status == "complete" ||
                    pendingItem.Key.StartsWith("carry:", StringComparison.Ordinal))
                {
                    continue;
                }

                originalMemories ??= ReadFrozenRun(previousRun, frozenRuns).Snapshot.ById;
                if (!originalMemories.TryGetValue(pendingItem.MemoryId, out var memory))
                {
                    continue;
                }

                var carryKey = $"carry:{previousRun.Id}:{pendingItem.Key}";
                var workSeed = new WorkSeed(carryKey, "carry", pendingItem.MemoryId, 0);
                candidates.Add(CreateMeditationCandidate(workSeed, memory, previousRun, originalMemories));
            }
        }

        // A stable input order helps reproducibility; only the LLM determines processing order.
        candidates.Sort((left, right) => StringComparer.Ordinal.Compare(left.Seed.Key, right.Seed.Key));
        var priorityCandidates = new List<MeditationPriorityCandidate>();
        var seedsByKey = new Dictionary<string, WorkSeed>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            priorityCandidates.Add(candidate.Evidence);
            seedsByKey.Add(candidate.Seed.Key, candidate.Seed);
        }

        if (candidates.Count == 0)
        {
            store.EnsureWorkItems(run.Id, []);
            return true;
        }

        CognitiveResult<IReadOnlyList<string>> priority;
        try
        {
            priority = await cognition.PrioritizeMeditationAsync(
                priorityCandidates, new CallContext(run.Id), cancellationToken);
        }
        catch (BudgetExceededException)
        {
            var pendingWork = new List<WorkSeed>();
            foreach (var candidate in candidates)
            {
                pendingWork.Add(candidate.Seed with { Ordinal = pendingWork.Count });
            }

            // Closing and preserving the queue must be atomic: a retry may never process this unranked order.
            store.FinishUnprioritizedMeditation(run.Id, pendingWork, timeProvider.GetUtcNow());
            return false;
        }

        if (priority.Value.Count != candidates.Count)
        {
            throw new InvalidDataException("Meditation priority must contain every candidate work key exactly once.");
        }

        var orderedWork = new List<WorkSeed>();
        foreach (var key in priority.Value)
        {
            if (key is null || !seedsByKey.Remove(key, out var seed))
            {
                throw new InvalidDataException("Meditation priority contains a duplicate or unknown work key.");
            }

            orderedWork.Add(seed with { Ordinal = orderedWork.Count });
        }

        // Persist the validated order before honoring cancellation or processing any work.
        store.EnsureWorkItems(run.Id, orderedWork);
        return true;
    }

    private async Task ProcessOriginalWorkAsync(
        RunRecord originRun,
        RunWorkItem originalItem,
        GraphSnapshot frozenEvidence,
        CallContext chargedCallContext,
        HashSet<string>? dreamNeighborhoodKeys,
        CancellationToken cancellationToken)
    {
        using var activity = ActivityScope.Begin(store, originalItem.Phase,
            originRun.Kind.ToString().ToLowerInvariant(), timeProvider.GetUtcNow(),
            new
            {
                seed_id = originalItem.MemoryId,
                period_start = originRun.PeriodStart,
                period_end = originRun.PeriodEnd,
                proposal_reused = originalItem.ProposalJson is not null,
                model_invoked = false,
                settings = options
            },
            runId: originRun.Id, workKey: originalItem.Key, chargedRunId: chargedCallContext.RunId);
        try
        {
            await ProcessOriginalWorkCoreAsync(originRun, originalItem, frozenEvidence,
                chargedCallContext, dreamNeighborhoodKeys, cancellationToken);
            activity.Complete(timeProvider.GetUtcNow());
        }
        catch (Exception error)
        {
            activity.Fail(error, timeProvider.GetUtcNow());
            throw;
        }
    }

    private async Task ProcessOriginalWorkCoreAsync(
        RunRecord originRun,
        RunWorkItem originalItem,
        GraphSnapshot frozenEvidence,
        CallContext chargedCallContext,
        HashSet<string>? dreamNeighborhoodKeys,
        CancellationToken cancellationToken)
    {
        var memoriesById = frozenEvidence.ById;
        if (!memoriesById.TryGetValue(originalItem.MemoryId, out var seed))
        {
            throw new InvariantException(
                $"Work seed {originalItem.MemoryId} is absent from its frozen run snapshot.");
        }

        SavedProposal savedProposal;
        var model = originalItem.Model;
        if (originalItem.ProposalJson is not null)
        {
            savedProposal = JsonSerializer.Deserialize<SavedProposal>(
                originalItem.ProposalJson, JsonDefaults.Options)
                ?? throw new InvariantException("Stored consolidation proposal is invalid.");
        }
        else
        {
            CognitiveResult<SavedProposal> generated;
            if (originalItem.Phase == "assimilation")
            {
                generated = await GenerateAssimilationProposalAsync(
                    seed, frozenEvidence, chargedCallContext, cancellationToken);
            }
            else if (originRun.Kind == RunKind.Dream)
            {
                if (dreamNeighborhoodKeys is null)
                {
                    throw new InvariantException("Dream neighborhood registry is missing.");
                }

                var neighborhood = new List<MemoryRecord>(await RetrieveNeighborhoodAsync(
                    seed, frozenEvidence, RunKind.Dream, chargedCallContext, cancellationToken));
                neighborhood.Sort((left, right) =>
                    StringComparer.Ordinal.Compare(left.Id, right.Id));
                var candidateIds = GetMemoryIds(neighborhood);
                var neighborhoodKey = CanonicalMemoryIdSet(candidateIds);

                if (!dreamNeighborhoodKeys.Add(neighborhoodKey))
                {
                    generated = new CognitiveResult<SavedProposal>(
                        new SavedProposal(candidateIds, [], []),
                        "dream-neighborhood-deduplicated");
                }
                else
                {
                    generated = await GenerateAbstractionProposalAsync(
                        seed, frozenEvidence, RunKind.Dream, memoriesById,
                        chargedCallContext, cancellationToken, neighborhood);
                }
            }
            else
            {
                generated = await GenerateAbstractionProposalAsync(
                    seed, frozenEvidence, originRun.Kind, memoriesById,
                    chargedCallContext, cancellationToken);
            }

            savedProposal = generated.Value;
            model = generated.Model;

            // Persist the exact proposal and candidate IDs before applying its graph changes.
            // A retry can then apply the same proposal without another generation call.
            var proposalJson = JsonSerializer.Serialize(savedProposal, JsonDefaults.Options);
            store.SaveWorkProposal(originRun.Id, originalItem.Key, proposalJson, model);
        }

        ActivityScope.UpdateCurrent(new
        {
            candidate_ids = savedProposal.AllowedCandidateIds,
            relations = savedProposal.Relations,
            abstractions = savedProposal.Abstractions,
            model
        });
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvariantException("Stored proposal model is missing.");
        }

        if (originalItem.Phase == "assimilation")
        {
            ApplySavedRelations(
                originRun, originalItem, seed, memoriesById, savedProposal, cancellationToken);
        }
        else
        {
            await ApplySavedAbstractionsAsync(
                originRun, originalItem, savedProposal, model, chargedCallContext, cancellationToken);
        }

        store.CompleteWork(originRun.Id, originalItem.Key);
    }

    private async Task<CognitiveResult<SavedProposal>> GenerateAssimilationProposalAsync(
        MemoryRecord seed,
        GraphSnapshot frozenEvidence,
        CallContext chargedCallContext,
        CancellationToken cancellationToken)
    {
        var candidates = await RetrieveAssimilationCandidatesAsync(
            seed, frozenEvidence, chargedCallContext, cancellationToken);
        ActivityScope.UpdateCurrent(new { candidate_ids = GetMemoryIds(candidates), model_invoked = true });
        var result = await cognition.AssimilateAsync(
            seed, candidates, chargedCallContext, cancellationToken);

        var candidateIds = GetMemoryIds(candidates);
        var proposal = new SavedProposal(candidateIds, result.Value, []);
        return new CognitiveResult<SavedProposal>(proposal, result.Model);
    }

    private async Task<CognitiveResult<SavedProposal>> GenerateAbstractionProposalAsync(
        MemoryRecord seed,
        GraphSnapshot frozenEvidence,
        RunKind kind,
        IReadOnlyDictionary<string, MemoryRecord> memoriesById,
        CallContext chargedCallContext,
        CancellationToken cancellationToken,
        IReadOnlyList<MemoryRecord>? precomputedNeighborhood = null)
    {
        var neighborhood = precomputedNeighborhood ?? await RetrieveNeighborhoodAsync(
            seed, frozenEvidence, kind, chargedCallContext, cancellationToken);
        var candidateIds = GetMemoryIds(neighborhood);
        ActivityScope.UpdateCurrent(new { candidate_ids = candidateIds });
        if (!CanFormAbstraction(neighborhood, memoriesById, cancellationToken))
        {
            return new CognitiveResult<SavedProposal>(
                new SavedProposal(candidateIds, [], []), "consolidation-ineligible");
        }

        IReadOnlyList<SourceArtifact> sources = [];
        if (kind == RunKind.Meditation)
        {
            sources = ReadSourceEvidence(neighborhood, memoriesById);
        }

        var role = kind == RunKind.Dream ? CognitionRole.Dream : CognitionRole.Meditation;
        ActivityScope.UpdateCurrent(new { model_invoked = true });
        var result = await cognition.AbstractAsync(
            neighborhood, sources, role, chargedCallContext, cancellationToken);

        var proposal = new SavedProposal(candidateIds, [], result.Value);
        return new CognitiveResult<SavedProposal>(proposal, result.Model);
    }

    private bool CanFormAbstraction(
        IReadOnlyList<MemoryRecord> neighborhood,
        IReadOnlyDictionary<string, MemoryRecord> memoriesById,
        CancellationToken cancellationToken)
    {
        var candidateIdsByDepth = new Dictionary<int, HashSet<string>>();
        foreach (var memory in neighborhood)
        {
            if (!candidateIdsByDepth.TryGetValue(memory.Depth, out var candidateIds))
            {
                candidateIds = new HashSet<string>(StringComparer.Ordinal);
                candidateIdsByDepth.Add(memory.Depth, candidateIds);
            }

            candidateIds.Add(memory.Id);
        }

        foreach (var (parentDepth, candidateIds) in candidateIdsByDepth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidateIds.Count < options.RootBase)
            {
                continue;
            }

            // All candidates on one layer may be parents. Their exact ancestry union
            // is the largest root set any proposal from this layer could supply.
            var requiredRoots = BigInteger.Pow(options.RootBase, checked(parentDepth + 1));
            var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            var visitedIds = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(candidateIds);
            while (queue.TryDequeue(out var memoryId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visitedIds.Add(memoryId))
                {
                    continue;
                }

                var memory = memoriesById[memoryId];
                if (memory.SourceRef is not null && sourceIds.Add(memory.SourceRef) &&
                    sourceIds.Count >= requiredRoots)
                {
                    return true;
                }

                foreach (var parentId in memory.DerivedFrom)
                {
                    queue.Enqueue(parentId);
                }
            }
        }

        return false;
    }

    private void ApplySavedRelations(
        RunRecord originRun,
        RunWorkItem originalItem,
        MemoryRecord seed,
        IReadOnlyDictionary<string, MemoryRecord> memoriesById,
        SavedProposal savedProposal,
        CancellationToken cancellationToken)
    {
        var allowedIds = new HashSet<string>(savedProposal.AllowedCandidateIds, StringComparer.Ordinal);
        var seenRelations = new HashSet<RelationProposal>();
        for (var index = 0; index < savedProposal.Relations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proposal = savedProposal.Relations[index];
            try
            {
                var isSuppliedCandidate = allowedIds.Contains(proposal.MemoryId) &&
                    memoriesById.ContainsKey(proposal.MemoryId);
                var pointsToObservation = proposal.RelatedMemoryId == seed.Id;
                var isSelfRelation = proposal.MemoryId == seed.Id;
                if (!isSuppliedCandidate || !pointsToObservation ||
                    isSelfRelation || !Enum.IsDefined(proposal.Kind) || !seenRelations.Add(proposal))
                {
                    throw new InvariantException(
                        "Assimilation must uniquely relate a supplied candidate to the observation, with no self edge.");
                }

                if (store is IActivityRecorder recorder)
                {
                    recorder.ApplyActivityRelation(proposal, originRun, originalItem.Key, index, timeProvider.GetUtcNow());
                }
                else
                {
                    store.AddRelation(proposal, originRun, timeProvider.GetUtcNow());
                }
            }
            catch (InvariantException error)
            {
                if (store is IActivityRecorder recorder)
                {
                    recorder.ApplyActivityRelation(proposal, originRun, originalItem.Key, index, timeProvider.GetUtcNow(), error.Message);
                }
                else
                {
                    store.RejectProposal(originRun.Id, originalItem.Key, index, error.Message);
                }
            }
        }
    }

    private async Task ApplySavedAbstractionsAsync(
        RunRecord originRun,
        RunWorkItem originalItem,
        SavedProposal savedProposal,
        string model,
        CallContext chargedCallContext,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < savedProposal.Abstractions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proposal = savedProposal.Abstractions[index];
            try
            {
                // A prior attempt may have committed this output before interruption.
                // Do not spend the next week's budget embedding it again.
                if (store.GetAppliedAbstraction(originRun.Id, originalItem.Key, index) is not null)
                {
                    continue;
                }

                if (originRun.Kind == RunKind.Dream && index >= 1)
                {
                    throw new InvariantException(
                        "Daily Dream allows at most one abstraction per consolidation neighborhood.");
                }

                // Validate before paying for embedding, then revalidate in the atomic store mutation.
                store.ValidateAbstraction(proposal, originRun, savedProposal.AllowedCandidateIds);
                var embedding = await cognition.EmbedAsync(
                    proposal.Content, chargedCallContext, cancellationToken);
                store.AddAbstraction(
                    proposal, model, originRun, originalItem.Key, index,
                    savedProposal.AllowedCandidateIds, embedding, timeProvider.GetUtcNow());
            }
            catch (InvariantException error)
            {
                store.RejectProposal(originRun.Id, originalItem.Key, index, error.Message);
            }
        }
    }

    private async Task<IReadOnlyList<MemoryRecord>> RetrieveAssimilationCandidatesAsync(
        MemoryRecord seed,
        GraphSnapshot frozenEvidence,
        CallContext chargedCallContext,
        CancellationToken cancellationToken)
    {
        var memoriesById = frozenEvidence.ById;
        var nearest = await search.SearchAsync(
            seed.Content, chargedCallContext, cancellationToken,
            snapshot: frozenEvidence, limit: options.SearchCandidates);

        var candidates = new List<MemoryRecord>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        void AddCandidate(string memoryId)
        {
            if (candidates.Count >= options.SearchCandidates || memoryId == seed.Id)
            {
                return;
            }

            if (memoriesById.TryGetValue(memoryId, out var memory) && selectedIds.Add(memoryId))
            {
                candidates.Add(memory);
            }
        }

        foreach (var relation in seed.Relations)
        {
            AddCandidate(relation.RelatedMemoryId);
        }

        foreach (var memory in nearest)
        {
            AddCandidate(memory.Id);
        }

        return candidates;
    }

    private async Task<IReadOnlyList<MemoryRecord>> RetrieveNeighborhoodAsync(
        MemoryRecord seed,
        GraphSnapshot frozenEvidence,
        RunKind kind,
        CallContext chargedCallContext,
        CancellationToken cancellationToken)
    {
        var memoriesById = frozenEvidence.ById;
        var memoryLimit = kind == RunKind.Dream ? options.NeighborhoodSize : options.MeditationGraphLimit;
        var nearest = await search.NearestAsync(
            seed, frozenEvidence, chargedCallContext, cancellationToken,
            seed.Depth, options.NeighborhoodSize);

        // The list records prompt order; the set only prevents duplicate membership.
        var selectedMemories = new List<MemoryRecord> { seed };
        var selectedIds = new HashSet<string>(StringComparer.Ordinal) { seed.Id };
        var sameDepthCount = 1;

        bool TryAddMemory(string memoryId)
        {
            if (selectedMemories.Count >= memoryLimit ||
                !memoriesById.TryGetValue(memoryId, out var memory))
            {
                return false;
            }

            if (!selectedIds.Add(memoryId))
            {
                return false;
            }

            selectedMemories.Add(memory);
            if (memory.Depth == seed.Depth)
            {
                sameDepthCount++;
            }

            return true;
        }

        // Reserve possible parents on the seed's layer, preferring direct outgoing evidence.
        // A full semantic result page must not displace that evidence.
        foreach (var relation in seed.Relations)
        {
            if (memoriesById.TryGetValue(relation.RelatedMemoryId, out var target) &&
                target.Depth == seed.Depth)
            {
                TryAddMemory(target.Id);
            }
        }

        foreach (var memory in nearest)
        {
            if (memory.Depth != seed.Depth)
            {
                continue;
            }

            if (sameDepthCount >= options.RootBase)
            {
                break;
            }

            TryAddMemory(memory.Id);
        }

        // Fill remaining places with outgoing relations, then semantic neighbors.
        foreach (var relation in seed.Relations)
        {
            TryAddMemory(relation.RelatedMemoryId);
        }

        foreach (var memory in nearest)
        {
            TryAddMemory(memory.Id);
        }

        if (kind == RunKind.Meditation)
        {
            // Broaden the selected region breadth first, visiting parents before outgoing relations.
            // Incoming semantic edges are never traversed.
            var queue = new Queue<MemoryRecord>(selectedMemories);
            var visitedIds = new HashSet<string>(StringComparer.Ordinal);
            while (queue.TryDequeue(out var memory) && selectedMemories.Count < memoryLimit)
            {
                if (!visitedIds.Add(memory.Id))
                {
                    continue;
                }

                foreach (var parentId in memory.DerivedFrom)
                {
                    if (TryAddMemory(parentId))
                    {
                        queue.Enqueue(memoriesById[parentId]);
                    }
                }

                foreach (var relation in memory.Relations)
                {
                    if (TryAddMemory(relation.RelatedMemoryId))
                    {
                        queue.Enqueue(memoriesById[relation.RelatedMemoryId]);
                    }
                }
            }
        }

        return selectedMemories;
    }

    private IReadOnlyList<SourceArtifact> ReadSourceEvidence(
        IReadOnlyList<MemoryRecord> neighborhood,
        IReadOnlyDictionary<string, MemoryRecord> memoriesById)
    {
        var sourceIdsInVisitOrder = new List<string>();
        var seenSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var visitedMemoryIds = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<MemoryRecord>(neighborhood);

        while (queue.TryDequeue(out var memory) &&
            sourceIdsInVisitOrder.Count < options.MeditationSourceLimit)
        {
            if (!visitedMemoryIds.Add(memory.Id))
            {
                continue;
            }

            if (memory.SourceRef is not null && seenSourceIds.Add(memory.SourceRef))
            {
                sourceIdsInVisitOrder.Add(memory.SourceRef);
            }

            foreach (var parentId in memory.DerivedFrom)
            {
                if (memoriesById.TryGetValue(parentId, out var parent))
                {
                    queue.Enqueue(parent);
                }
            }
        }

        var sources = new List<SourceArtifact>();
        foreach (var sourceId in sourceIdsInVisitOrder)
        {
            sources.Add(store.ReadSource(sourceId));
        }

        return sources;
    }

    private RunSummary ReadRunSummary(long runId, string status)
    {
        var completedItems = 0;
        foreach (var item in store.GetWorkItems(runId))
        {
            if (item.Status == "complete")
            {
                completedItems++;
            }
        }

        return new RunSummary(
            runId, status, completedItems,
            store.GetRejectedProposalCount(runId), store.GetRunAccountedUsd(runId));
    }

    private FrozenRunEvidence ReadFrozenRun(
        RunRecord run,
        Dictionary<long, FrozenRunEvidence> frozenRuns)
    {
        if (frozenRuns.TryGetValue(run.Id, out var evidence))
        {
            return evidence;
        }

        evidence = new FrozenRunEvidence(run, store.ReadSnapshot(run));
        frozenRuns.Add(run.Id, evidence);
        return evidence;
    }

    private RunRecord ReadCarryOriginRun(long runId)
    {
        RunRecord? matchingRun = null;
        foreach (var run in store.GetRuns())
        {
            if (run.Id != runId)
            {
                continue;
            }

            if (matchingRun is not null)
            {
                throw new InvariantException("Carry work refers to a duplicate run ID.");
            }

            matchingRun = run;
        }

        return matchingRun ?? throw new InvariantException("Carry work refers to a missing run.");
    }

    private RunWorkItem ReadOriginalWorkItem(long runId, string workKey)
    {
        RunWorkItem? matchingItem = null;
        foreach (var item in store.GetWorkItems(runId))
        {
            if (item.Key != workKey)
            {
                continue;
            }

            if (matchingItem is not null)
            {
                throw new InvariantException("Carry work refers to a duplicate original work key.");
            }

            matchingItem = item;
        }

        return matchingItem ?? throw new InvariantException("Carry work refers to missing original work.");
    }

    private static bool HasChangeInPeriod(MemoryRecord memory, RunRecord run)
    {
        if (InPeriod(memory.CreatedAt, run))
        {
            return true;
        }

        foreach (var relation in memory.Relations)
        {
            if (InPeriod(relation.RelatedAt, run))
            {
                return true;
            }
        }

        return false;
    }

    private static MeditationCandidate CreateMeditationCandidate(
        WorkSeed seed,
        MemoryRecord memory,
        RunRecord period,
        IReadOnlyDictionary<string, MemoryRecord> memoriesById)
    {
        var relatedMemories = new List<MemoryRecord>();
        var relatedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in memory.Relations)
        {
            if (relatedIds.Add(relation.RelatedMemoryId))
            {
                relatedMemories.Add(memoriesById[relation.RelatedMemoryId]);
            }
        }

        var evidence = new MeditationPriorityCandidate(
            seed.Key, memory, period.PeriodStart, period.PeriodEnd, relatedMemories);
        return new MeditationCandidate(seed, evidence);
    }

    private static int CompareWorkOrder(RunWorkItem left, RunWorkItem right)
    {
        var ordinalComparison = left.Ordinal.CompareTo(right.Ordinal);
        if (ordinalComparison != 0)
        {
            return ordinalComparison;
        }

        return StringComparer.Ordinal.Compare(left.Key, right.Key);
    }

    private static int CompareCreationOrder(MemoryRecord left, MemoryRecord right)
    {
        var createdAtComparison = left.CreatedAt.CompareTo(right.CreatedAt);
        if (createdAtComparison != 0)
        {
            return createdAtComparison;
        }

        return StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    private static IReadOnlyList<string> GetMemoryIds(IReadOnlyList<MemoryRecord> memories)
    {
        var memoryIds = new List<string>();
        foreach (var memory in memories)
        {
            memoryIds.Add(memory.Id);
        }

        return memoryIds;
    }

    private static HashSet<string> RestoreDreamNeighborhoodKeys(
        IReadOnlyList<RunWorkItem> workItems)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in workItems)
        {
            if (item.Phase != "consolidation" || item.ProposalJson is null)
            {
                continue;
            }

            var proposal = JsonSerializer.Deserialize<SavedProposal>(
                item.ProposalJson, JsonDefaults.Options)
                ?? throw new InvariantException("Stored consolidation proposal is invalid.");
            keys.Add(CanonicalMemoryIdSet(proposal.AllowedCandidateIds));
        }

        return keys;
    }

    private static string CanonicalMemoryIdSet(IReadOnlyList<string> memoryIds)
    {
        var uniqueIds = new HashSet<string>(memoryIds, StringComparer.Ordinal);
        var orderedIds = new List<string>(uniqueIds);
        orderedIds.Sort(StringComparer.Ordinal);
        return JsonSerializer.Serialize(orderedIds, JsonDefaults.Options);
    }

    private static bool InPeriod(DateTimeOffset timestamp, RunRecord run)
    {
        return timestamp >= run.PeriodStart && timestamp < run.PeriodEnd;
    }

    private static bool IsTerminal(string status)
    {
        return status is "complete" or "budget_exhausted";
    }

    private static bool TryGetCarryOrigin(RunWorkItem item, out long runId, out string workKey)
    {
        runId = 0;
        workKey = "";
        if (!item.Key.StartsWith("carry:", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = item.Key.Split(':', 3);
        if (parts.Length != 3 ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out runId))
        {
            throw new InvariantException("Malformed carry work reference.");
        }

        workKey = parts[2];
        return true;
    }

    private sealed record FrozenRunEvidence(RunRecord Run, GraphSnapshot Snapshot);

    private sealed record MeditationCandidate(
        WorkSeed Seed,
        MeditationPriorityCandidate Evidence);

    private sealed record SavedProposal(
        IReadOnlyList<string> AllowedCandidateIds,
        IReadOnlyList<RelationProposal> Relations,
        IReadOnlyList<AbstractionProposal> Abstractions);
}
