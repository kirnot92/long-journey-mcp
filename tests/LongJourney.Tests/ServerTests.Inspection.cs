using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LongJourney.Core;
using LongJourney.Server;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace LongJourney.Tests;

public sealed partial class ServerTests
{
    [Fact]
    public async Task InspectionHttpEncodesTextAndLeavesCorpusAndCognitionUntouched()
    {
        await using var host = await RunningHost.StartAsync();
        var store = (SqliteMemoryStore)host.App.Services.GetRequiredService<IMemoryStore>();
        Assert.Same(store, host.App.Services.GetRequiredService<IInspectionReader>());
        var at = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        const string raw = "\n  <script>alert('source')</script>\r\n\tA & B  ";
        var source = store.SaveSource(raw, at);
        Assert.True(store.ClaimSource(source.Source.Id));
        store.CompleteSource(source.Source.Id,
            [new NewObservation(raw, "<img src=x onerror=alert('model')>", ConsolidationFixture.Vector)], at);
        var memory = Assert.Single(store.GetSourceMemories(source.Source.Id));
        var pending = store.SaveSource("pending source remains pending during inspection", at);
        var run = store.GetOrCreateRun(RunKind.Dream, at, at.AddDays(1), at, null);
        const string key = "work:seed";
        store.EnsureWorkItems(run.Id, [new WorkSeed(key, "assimilation", memory.Id, 0)]);
        const string proposal = "{\"content\":\"<script>proposal</script>\"}";
        store.SaveWorkProposal(run.Id, key, proposal, "stored-model");
        store.RejectProposal(run.Id, key, 0, "<script>rejected</script>");
        store.ReserveUsage(run.Id, "fake", "waiting", .25m, at);
        store.RecordRecall([memory.Id], at.AddMinutes(1));

        var before = ReadInspectionState(store.DatabasePath);
        var calls = host.Cognition.Calls;
        var paths = new[]
        {
            "inspect", "inspect?depth=0&q=source", $"inspect/memory/{memory.Id}",
            $"inspect/trace/{memory.Id}", $"inspect/source/{source.Source.Id}",
            $"inspect/source/{pending.Source.Id}", "inspect/runs",
            $"inspect/runs/{run.Id}", $"inspect/runs/{run.Id}/work?key={Uri.EscapeDataString(key)}",
            "inspection.css"
        };
        foreach (var path in paths)
        {
            using var response = await host.Client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("<script>", html);
            Assert.DoesNotContain("<img src=x", html);
            if (path != "inspection.css")
            {
                Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.CacheControl?.NoStore);
            }
        }

        var memoryHtml = await host.Client.GetStringAsync($"inspect/memory/{memory.Id}");
        Assert.Contains("&lt;script&gt;", memoryHtml);
        Assert.Contains("&lt;img", memoryHtml);
        var sourceHtml = await host.Client.GetStringAsync($"inspect/source/{source.Source.Id}");
        var encodedRaw = Regex.Match(sourceHtml, "<pre[^>]*><code>(.*?)</code></pre>", RegexOptions.Singleline);
        Assert.True(encodedRaw.Success);
        Assert.Equal(raw, WebUtility.HtmlDecode(encodedRaw.Groups[1].Value));
        var workHtml = await host.Client.GetStringAsync($"inspect/runs/{run.Id}/work?key={Uri.EscapeDataString(key)}");
        Assert.Contains("&lt;script&gt;proposal&lt;/script&gt;", workHtml);
        Assert.Contains("&lt;script&gt;rejected&lt;/script&gt;", workHtml);
        Assert.Equal(before, ReadInspectionState(store.DatabasePath));
        Assert.Equal(calls, host.Cognition.Calls);
    }

    [Fact]
    public async Task InspectionHandlesEmptyInvalidMissingAndUnreadableStates()
    {
        await using var host = await RunningHost.StartAsync();
        var empty = WebUtility.HtmlDecode(await host.Client.GetStringAsync("inspect"));
        Assert.Contains("아직 저장된 기억이 없습니다.", empty);
        Assert.Contains("아직 저장된 실행이 없습니다.",
            WebUtility.HtmlDecode(await host.Client.GetStringAsync("inspect/runs")));
        foreach (var query in new[] { "?depth=-1", "?depth=abc", "?p=0", "?p=2147483648", "?snapshot=-1", "?q=" + new string('x', 201) })
        {
            using var response = await host.Client.GetAsync("inspect" + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("필터 값이 올바르지 않습니다.", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        }

        foreach (var path in new[] { "inspect/memory/absent", "inspect/trace/absent", "inspect/source/absent", "inspect/runs/999", "inspect/runs/999/work?key=absent" })
        {
            using var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("찾을 수 없습니다.", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        }

        var store = (SqliteMemoryStore)host.App.Services.GetRequiredService<IMemoryStore>();
        var source = store.SaveSource("unreadable source", DateTimeOffset.UtcNow);
        File.Delete(Path.Combine(host.DirectoryPath, source.Source.RelativePath));
        var before = ReadInspectionState(store.DatabasePath);
        var unavailable = WebUtility.HtmlDecode(await host.Client.GetStringAsync($"inspect/source/{source.Source.Id}"));
        Assert.Contains("저장된 원문을 읽을 수 없습니다.", unavailable);
        Assert.Contains(source.Source.Id, unavailable);
        Assert.DoesNotContain(host.DirectoryPath, unavailable);
        Assert.Equal(before, ReadInspectionState(store.DatabasePath));
        Assert.Equal(0, host.Cognition.Calls);
    }

    [Fact]
    public async Task InspectionPagesAndCssRetainHostAndOriginProtection()
    {
        await using var host = await RunningHost.StartAsync();
        foreach (var path in new[] { "inspect", "inspection.css" })
        {
            using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, path);
            crossOrigin.Headers.Add("Origin", "https://untrusted.example");
            using var blocked = await host.Client.SendAsync(crossOrigin);
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            using var wrongHost = new HttpRequestMessage(HttpMethod.Get, path);
            wrongHost.Headers.Host = "untrusted.example";
            using var rejected = await host.Client.SendAsync(wrongHost);
            Assert.True(rejected.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden);
            using var sameOrigin = new HttpRequestMessage(HttpMethod.Get, path);
            sameOrigin.Headers.Add("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            using var allowed = await host.Client.SendAsync(sameOrigin);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
    }

    [Fact]
    public async Task ColdServerProcessStartsWithoutPriorJsonSerializationOrApiKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "long-journey-cold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var address = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(AppHost).Assembly.Location);
        start.ArgumentList.Add("--no-scheduler");
        start.ArgumentList.Add("--Server:Port=0");
        start.ArgumentList.Add("--Engine:DataDirectory=" + directory);
        start.Environment.Remove("OPENAI_API_KEY");
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        DataReceivedEventHandler readAddress = (_, args) =>
        {
            var line = args.Data;
            if (line is null)
            {
                return;
            }

            var match = Regex.Match(line, @"Now listening on: (http://127\.0\.0\.1:\d+)");
            if (match.Success)
            {
                address.TrySetResult(new Uri(match.Groups[1].Value));
            }
        };
        process.OutputDataReceived += readAddress;
        process.ErrorDataReceived += readAddress;
        process.Exited += (_, _) => address.TrySetException(new InvalidOperationException("Cold server exited before listening."));
        var started = false;
        try
        {
            started = process.Start();
            Assert.True(started);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var endpoint = await address.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var client = new HttpClient { BaseAddress = endpoint };
            using var response = await client.GetAsync("/inspect");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            // This test owns this absolute, unique temporary corpus directory.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string ReadInspectionState(string databasePath)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var table in new[]
        {
            "sources", "memories", "derived_from", "memory_roots", "relations", "recall_events",
            "runs", "run_work", "rejected_proposals", "api_calls", "embeddings", "state"
        })
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new List<string?> { table };
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row.Add(reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture));
                }

                rows.Add(row);
            }
        }

        tx.Commit();
        return JsonSerializer.Serialize(rows);
    }
}
