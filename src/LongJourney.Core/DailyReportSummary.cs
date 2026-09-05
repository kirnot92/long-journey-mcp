using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace LongJourney.Core;

public sealed partial class DailyReportService
{
    private static JsonObject Summarize(List<JsonObject> operations, List<JsonObject> calls,
        List<JsonObject> newSources, List<JsonObject> created, List<JsonObject> outcomes,
        Dictionary<string, JsonObject> memories, List<JsonObject> workItems, List<JsonObject> allOperations, string coverage)
    {
        var remember = operations.Where(row => Text(row, "kind") == "remember" && Text(row, "origin") == "agent").ToList();
        var extraction = operations.Where(row => Text(row, "kind") == "extraction").ToList();
        var recall = operations.Where(row => Text(row, "kind") == "recall").ToList();
        var assimilation = operations.Where(row => Text(row, "kind") == "assimilation").ToList();
        var returned = recall.SelectMany(row => Ids(Details(row), "returned_ids")).ToList();
        var returnedD0 = returned.Where(id => memories.TryGetValue(id, out var memory) && Number(memory, "depth") == 0).ToList();
        var d0 = created.Where(row => Number(row, "depth") == 0).ToList();
        var sourceCounts = newSources.Select(source => (long)memories.Values.Count(memory => Number(memory, "depth") == 0 && Text(memory, "source_ref") == Text(source, "id"))).ToList();
        var actual = calls.Sum(row => Decimal(row, "actual_usd"));
        var unsettled = calls.Where(row => row["actual_usd"] is null).Sum(row => Decimal(row, "reserved_usd"));
        var workCount = assimilation.Select(WorkKey).Distinct().Count();
        var assimilationCalls = calls.Where(row => Text(row, "parent_activity_kind") == "assimilation").ToList();
        var assimilationActual = assimilationCalls.Sum(row => Decimal(row, "actual_usd"));
        var everAttempted = allOperations.Where(row => Text(row, "kind") == "assimilation").Select(WorkKey).ToHashSet();
        var assimilationWork = workItems.Where(row => Text(row, "phase") == "assimilation").ToList();
        JsonObject summary = new()
        {
            ["remember"] = new JsonObject
            {
                ["calls"] = coverage == "unknown_legacy" ? null : remember.Count,
                ["observed_calls"] = remember.Count,
                ["states"] = Counts(remember.Select(row => Text(row, "status") ?? "unknown")),
                ["submitted_raw_characters"] = remember.Sum(row => Number(Details(row), "raw_characters") ?? 0),
                ["submitted_raw_bytes"] = remember.Sum(row => Number(Details(row), "raw_bytes") ?? 0),
                ["unknown_raw_size_calls"] = remember.Count(row => Number(Details(row), "raw_bytes") is null),
                ["raw_characters"] = Distribution(remember.Select(row => Number(Details(row), "raw_characters")).OfType<long>()),
                ["raw_bytes"] = Distribution(remember.Select(row => Number(Details(row), "raw_bytes")).OfType<long>()),
                ["new_source_calls"] = remember.Count(row => Boolean(Details(row), "new_source") == true),
                ["reused_source_calls"] = remember.Count(row => Boolean(Details(row), "new_source") == false && Text(row, "source_id") is not null),
                ["new_unique_sources"] = newSources.Count,
                ["new_stored_raw_bytes_known"] = newSources.Sum(row => Number(row, "raw_bytes") ?? 0),
                ["new_stored_raw_characters_known"] = newSources.Sum(row => Number(row, "raw_characters") ?? 0),
                ["new_source_size_unknown"] = newSources.Count(row => Number(row, "raw_bytes") is null),
                ["created_d0"] = d0.Count,
                ["created_d0_per_new_source"] = Distribution(sourceCounts),
                ["source_cohort_note"] = "Sources created on report day, all their D0 visible as of snapshot (including later extraction). Pending/failed Source counts are separate; zero D0 does not mean completed empty extraction.",
                ["new_source_current_states"] = Counts(newSources.Select(row => Text(row, "status") ?? "unknown")),
                ["extraction_attempts"] = extraction.Count,
                ["zero_observation_extractions"] = extraction.Count(row => Text(row, "status") == "complete" && !Ids(Details(row), "created_ids").Any()),
                ["extraction_states"] = Counts(extraction.Select(row => Text(row, "status") ?? "unknown")),
                ["recovery_extraction_attempts"] = extraction.Count(row => Text(row, "origin") == "recovery")
            },
            ["recall"] = new JsonObject
            {
                ["calls"] = coverage == "unknown_legacy" ? null : recall.Count,
                ["observed_calls"] = recall.Count,
                ["states"] = Counts(recall.Select(row => Text(row, "status") ?? "unknown")),
                ["empty_completed_results"] = recall.Count(row => Text(row, "status") == "complete" && !Ids(Details(row), "returned_ids").Any()),
                ["zero_candidate_completed_calls"] = recall.Count(row => Text(row, "status") == "complete" && !Ids(Details(row), "candidate_ids").Any()),
                ["candidates_but_zero_returned"] = recall.Count(row => Text(row, "status") == "complete" && Ids(Details(row), "candidate_ids").Any() && !Ids(Details(row), "returned_ids").Any()),
                ["returned_d0_total"] = returnedD0.Count,
                ["returned_d0_distinct"] = returnedD0.Distinct().Count(),
                ["returned_by_depth"] = Counts(returned.Select(id => memories.TryGetValue(id, out var memory) ? Text(memory, "depth")! : "unknown")),
                ["returned_d0_frequency"] = Counts(returnedD0)
            },
            ["assimilation"] = new JsonObject
            {
                ["attempts"] = coverage == "unknown_legacy" ? null : assimilation.Count,
                ["logical_work_count"] = workCount,
                ["model_invoked_attempts"] = assimilation.Count(row => Boolean(Details(row), "model_invoked") == true),
                ["model_invoked_definition"] = "Cognition invocation attempted; auth, cancellation or budget checks can prevent an API reservation. API reservations do not guarantee provider execution.",
                ["linked_api_reservations"] = assimilationCalls.Count,
                ["referenced_run_work_states_as_of"] = Counts(assimilationWork.Select(row => Text(row, "status") ?? "unknown")),
                ["referenced_run_work_without_recorded_attempt_as_of"] = assimilationWork.Count(row => !everAttempted.Contains(WorkKey(row))),
                ["recorded_attempt_note"] = "No activity record can mean queued work or execution before instrumentation; it does not prove the work was never attempted.",
                ["proposal_reused_attempts"] = assimilation.Count(row => Boolean(Details(row), "proposal_reused") == true),
                ["states"] = Counts(assimilation.Select(row => Text(row, "status") ?? "unknown")),
                ["completed_zero_proposal_attempts"] = assimilation.Count(row => Text(row, "status") == "complete" && Details(row)?["relations"] is JsonArray array && array.Count == 0),
                ["relation_results_at_execution_date"] = outcomes.Count,
                ["outcomes"] = Counts(outcomes.Select(row => Text(row, "outcome") ?? "unknown")),
                ["appended_categories"] = Counts(outcomes.Where(row => Text(row, "outcome") == "appended").Select(row => Text(row, "category")!))
            },
            ["costs"] = new JsonObject
            {
                ["api_calls"] = calls.Count,
                ["actual_usd"] = actual,
                ["unsettled_reserved_usd"] = unsettled,
                ["unsettled_calls"] = calls.Count(row => row["actual_usd"] is null),
                ["unattributed_calls"] = calls.Count(row => Text(row, "activity_id") is null),
                ["unattributed_actual_usd"] = calls.Where(row => Text(row, "activity_id") is null).Sum(row => Decimal(row, "actual_usd")),
                ["new_source_denominator"] = newSources.Count,
                ["actual_usd_per_new_source"] = newSources.Count == 0 ? null : actual / newSources.Count,
                ["assimilation_work_denominator"] = workCount,
                ["assimilation_linked_actual_usd"] = assimilationActual,
                ["actual_usd_per_assimilation_work"] = workCount == 0 ? null : assimilationActual / workCount,
                ["ratio_note"] = "Source ratio uses all daily costs. Assimilation ratio uses only API costs linked to assimilation (including embedding), divided by works attempted that day. API and attempt dates can differ; these are daily workload ratios, not usefulness.",
                ["stages"] = Rows(calls.GroupBy(row => (Operation: Text(row, "operation") ?? "unknown", Parent: Text(row, "parent_activity_kind") ?? "unattributed")).OrderBy(group => group.Key.Parent).ThenBy(group => group.Key.Operation).Select(group => new JsonObject
                {
                    ["operation"] = group.Key.Operation,
                    ["parent_activity_kind"] = group.Key.Parent,
                    ["api_calls"] = group.Count(),
                    ["actual_usd"] = group.Sum(row => Decimal(row, "actual_usd")),
                    ["unsettled_reserved_usd"] = group.Where(row => row["actual_usd"] is null).Sum(row => Decimal(row, "reserved_usd")),
                    ["input_tokens"] = group.Sum(row => Number(row["usage"] as JsonObject, "input_tokens") ?? 0),
                    ["cached_input_tokens"] = group.Sum(row => Number(row["usage"] as JsonObject, "cached_input_tokens") ?? 0),
                    ["cache_write_tokens"] = group.Sum(row => Number(row["usage"] as JsonObject, "cache_write_tokens") ?? 0),
                    ["output_tokens"] = group.Sum(row => Number(row["usage"] as JsonObject, "output_tokens") ?? 0),
                    ["usage_unknown_calls"] = group.Count(row => row["usage"] is null)
                }))
            }
        };
        if (coverage == "unknown_legacy")
        {
            foreach (var key in new[] { "submitted_raw_characters", "submitted_raw_bytes", "unknown_raw_size_calls", "raw_characters", "raw_bytes", "new_source_calls", "reused_source_calls", "extraction_attempts", "zero_observation_extractions", "extraction_states", "recovery_extraction_attempts", "states" })
            {
                summary["remember"]![key] = null;
            }

            foreach (var key in summary["recall"]!.AsObject().Select(pair => pair.Key).Where(key => key != "observed_calls").ToArray())
            {
                summary["recall"]![key] = null;
            }

            foreach (var key in summary["assimilation"]!.AsObject().Select(pair => pair.Key).ToArray())
            {
                summary["assimilation"]![key] = null;
            }
        }

        return summary;
    }

    private static string RelationCategory(JsonObject row, Dictionary<string, JsonObject> memories)
    {
        if (!memories.TryGetValue(Text(row, "memory_id") ?? "", out var owner) ||
            !memories.TryGetValue(Text(row, "related_memory_id") ?? "", out var target))
        {
            return "unknown";
        }

        if (Number(owner, "depth") > 0 && Number(target, "depth") == 0)
        {
            return "abstraction_to_d0";
        }

        if (Number(owner, "depth") == 0 && Number(target, "depth") == 0)
        {
            return Text(owner, "source_ref") == Text(target, "source_ref") ? "same_source_d0" : "cross_source_d0";
        }

        return "other_depths";
    }

    private static JsonObject? Details(JsonObject row) => row["details"] as JsonObject;
    private static long? Number(JsonObject? row, string key) => long.TryParse(Text(row, key), CultureInfo.InvariantCulture, out var value) ? value : null;
    private static decimal Decimal(JsonObject? row, string key) => decimal.TryParse(Text(row, key), CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static bool? Boolean(JsonObject? row, string key) => bool.TryParse(Text(row, key), out var value) ? value : null;
    private static JsonObject Counts(IEnumerable<string> values)
    {
        JsonObject counts = [];
        foreach (var group in values.GroupBy(value => value).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            counts[group.Key] = group.Count();
        }

        return counts;
    }

    private static JsonObject Distribution(IEnumerable<long> values)
    {
        var ordered = values.Order().ToArray();
        return new JsonObject
        {
            ["n"] = ordered.Length,
            ["method"] = "nearest_rank",
            ["p50"] = ordered.Length == 0 ? null : ordered[(int)Math.Ceiling(ordered.Length * .5) - 1],
            ["p90"] = ordered.Length == 0 ? null : ordered[(int)Math.Ceiling(ordered.Length * .9) - 1],
            ["max"] = ordered.Length == 0 ? null : ordered[^1]
        };
    }

    private static string RenderMarkdown(JsonObject report)
    {
        var date = Text(report, "date")!;
        var summary = report["summary"]!.AsObject();
        var remember = summary["remember"]!.AsObject();
        var recall = summary["recall"]!.AsObject();
        var assimilation = summary["assimilation"]!.AsObject();
        var costs = summary["costs"]!.AsObject();
        StringBuilder text = new();
        text.AppendLine($"# Daily report · {date}");
        text.AppendLine();
        text.AppendLine($"Snapshot: `{Text(report, "snapshot_id")}` · schema 1 · {Escape(Text(report, "as_of"))}");
        text.AppendLine();
        text.AppendLine($"Time zone: {Escape(Text(report, "time_zone_id"))}. Coverage: **{Escape(Text(report["coverage"]!.AsObject(), "status"))}**. {(Boolean(report, "provisional") == true ? "**Provisional day.**" : "Closed day; late completions and settlements may update this report.")}");
        text.AppendLine();
        text.AppendLine($"[Complete ordered activity, memory, provenance, source and cost details]({date}.json). Original raw remains in the Source archive. Remember/Recall/Think counts use invocation start date; costs use API start date; relation outcomes use application date. Dream target periods are listed separately in JSON.");
        text.AppendLine("Recall / Think totals share the recall activity kind. JSON operations retain details.tool, query/context and ordered candidate/returned IDs for comparing the two tools; a missing tool field means the distinction was not recorded.");
        text.AppendLine();
        text.AppendLine("| Metric | Value |");
        text.AppendLine("| --- | ---: |");
        void Metric(string label, JsonObject data, string key) => text.AppendLine($"| {label} | {Escape(Text(data, key) ?? "unknown / legacy")} |");
        Metric("Remember agent calls", remember, "calls");
        Metric("Submitted raw UTF-16 characters", remember, "submitted_raw_characters");
        Metric("Submitted raw UTF-8 bytes (including duplicates)", remember, "submitted_raw_bytes");
        Metric("Unknown input sizes", remember, "unknown_raw_size_calls");
        Metric("New unique Sources", remember, "new_unique_sources");
        Metric("New stored raw bytes (known)", remember, "new_stored_raw_bytes_known");
        Metric("New Sources with unknown raw size", remember, "new_source_size_unknown");
        Metric("Created D0", remember, "created_d0");
        Metric("Extraction attempts", remember, "extraction_attempts");
        Metric("Zero-observation completed extractions", remember, "zero_observation_extractions");
        Metric("Recovery extraction attempts", remember, "recovery_extraction_attempts");
        Metric("Recall / Think calls", recall, "calls");
        Metric("Completed empty Recall / Think results", recall, "empty_completed_results");
        Metric("Returned D0 total", recall, "returned_d0_total");
        Metric("Returned D0 distinct", recall, "returned_d0_distinct");
        Metric("Assimilation execution attempts", assimilation, "attempts");
        Metric("Assimilation logical works", assimilation, "logical_work_count");
        Metric("Cognition invocation attempts", assimilation, "model_invoked_attempts");
        Metric("Linked API reservations (not guaranteed provider execution)", assimilation, "linked_api_reservations");
        Metric("Referenced run work without a recorded attempt (includes legacy gaps)", assimilation, "referenced_run_work_without_recorded_attempt_as_of");
        Metric("Saved proposal reuse attempts", assimilation, "proposal_reused_attempts");
        text.AppendLine();
        text.AppendLine($"Source cohort: Sources created this day, with all their D0 available as of this snapshot, including later extraction. Current Source states: {Compact(remember["new_source_current_states"])}. Zero D0 in a pending Source is not a completed empty extraction.");
        text.AppendLine();
        text.AppendLine("| Distribution | n | p50 | p90 | max |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var key in new[] { "raw_characters", "raw_bytes", "created_d0_per_new_source" })
        {
            if (remember[key] is not JsonObject distribution)
            {
                text.AppendLine($"| {key} | unknown | unknown | unknown | unknown |");
                continue;
            }
            text.AppendLine($"| {key} | {Text(distribution, "n")} | {Text(distribution, "p50") ?? "—"} | {Text(distribution, "p90") ?? "—"} | {Text(distribution, "max") ?? "—"} |");
        }

        text.AppendLine();
        text.AppendLine($"States — Remember: {Compact(remember["states"])}; extraction: {Compact(remember["extraction_states"])}; Recall / Think: {Compact(recall["states"])}; assimilation: {Compact(assimilation["states"])}.");
        text.AppendLine();
        text.AppendLine($"Relation outcomes: {Compact(assimilation["outcomes"])}. Appended categories: {Compact(assimilation["appended_categories"])}. A saved proposal is counted once per run/work/index; repeated attempts do not change its first application outcome.");
        text.AppendLine();
        text.AppendLine($"API calls: {Text(costs, "api_calls")}; settled actual: **${Text(costs, "actual_usd")}**; unsettled reservations: **${Text(costs, "unsettled_reserved_usd")}** ({Text(costs, "unsettled_calls")} calls). Unattributed calls: {Text(costs, "unattributed_calls")}.");
        text.AppendLine();
        text.AppendLine("| Parent activity / API stage | Calls | Input tokens | Output tokens | Actual USD | Unsettled USD |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var node in costs["stages"]!.AsArray())
        {
            var stage = node!.AsObject();
            text.AppendLine($"| {Escape(Text(stage, "parent_activity_kind"))} / {Escape(Text(stage, "operation"))} | {Text(stage, "api_calls")} | {Text(stage, "input_tokens")} | {Text(stage, "output_tokens")} | {Text(stage, "actual_usd")} | {Text(stage, "unsettled_reserved_usd")} |");
        }

        text.AppendLine();
        text.AppendLine($"Daily total USD / new Source: {Text(costs, "actual_usd_per_new_source") ?? "n/a"} (n={Text(costs, "new_source_denominator")}); assimilation-linked USD ({Text(costs, "assimilation_linked_actual_usd")}) / assimilation work: {Text(costs, "actual_usd_per_assimilation_work") ?? "n/a"} (n={Text(costs, "assimilation_work_denominator")}). Costs and attempts use their own start dates, so these are workload ratios, not usefulness measurements.");
        text.AppendLine();
        text.AppendLine("Source references (all referenced Sources, including earlier experiences):");
        text.AppendLine();
        foreach (var node in report["sources"]!.AsArray())
        {
            var source = node!.AsObject();
            var path = Text(source, "absolute_path");
            var link = path is null ? Escape(Text(source, "id")) : $"[{Escape(Text(source, "id"))}](<{path.Replace('\\', '/').Replace(">", "%3E", StringComparison.Ordinal)}>)";
            text.AppendLine($"- {link} — {Escape(Text(source, "artifact_status"))}; created {Escape(Text(source, "created_at"))}; raw bytes {Text(source, "raw_bytes") ?? "unknown"}.");
        }

        text.AppendLine();
        text.AppendLine("Counts describe activity, not usefulness. A returned memory may not be used in an answer. Experiences an agent never submitted are unobserved. Prior-to-instrumentation call counts and application outcomes are unknown, even when legacy Sources, memories, and API costs are available. JSON preserves complete query/context and immutable memory content; mutable current relations are not presented as historical Recall context.");
        return text.ToString();
    }

    private static string Compact(JsonNode? value) => Escape(value?.ToJsonString() ?? "unknown");
    private static string Escape(string? value) => (value ?? "").Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("|", "&#124;", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal).Replace("[", "&#91;", StringComparison.Ordinal)
        .Replace("]", "&#93;", StringComparison.Ordinal).Replace("`", "&#96;", StringComparison.Ordinal);
}
