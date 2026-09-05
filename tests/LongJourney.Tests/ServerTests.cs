using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LongJourney.Core;
using LongJourney.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LongJourney.Tests;

public sealed partial class ServerTests
{
    [Fact]
    public async Task HttpMcpExposesOnlyFourToolsAndPersistsSharedMemoryWithSnakeCaseResults()
    {
        await using var host = await RunningHost.StartAsync();
        var initialized = await host.RpcAsync("initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new
            {
            },
            clientInfo = new
            {
                name = "long-journey-tests",
                version = "1.0"
            }
        });
        Assert.True(initialized.TryGetProperty("result", out _), initialized.ToString());

        var listed = await host.RpcAsync("tools/list", new
        {
        });
        var tools = new List<JsonElement>();
        var toolNames = new List<string?>();
        foreach (var tool in listed.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            tools.Add(tool);
            toolNames.Add(tool.GetProperty("name").GetString());
        }

        toolNames.Sort();
        Assert.Equal(new[] { "recall", "remember", "think", "trace" }, toolNames);
        var rememberTool = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == "remember");
        var rememberProperties = new List<string>();
        foreach (var property in rememberTool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject())
        {
            rememberProperties.Add(property.Name);
        }

        Assert.Equal(new[] { "raw" }, rememberProperties);
        Assert.False(rememberTool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.True(rememberTool.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean());
        var recallTool = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == "recall");
        Assert.Contains("concrete experiences", recallTool.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        var thinkTool = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == "think");
        var thinkSchema = thinkTool.GetProperty("inputSchema");
        Assert.Collection(thinkSchema.GetProperty("properties").EnumerateObject(),
            property => Assert.Equal("topic", property.Name),
            property => Assert.Equal("context", property.Name));
        Assert.Equal("string", thinkSchema.GetProperty("properties").GetProperty("topic").GetProperty("type").GetString());
        Assert.Equal("topic", Assert.Single(thinkSchema.GetProperty("required").EnumerateArray()).GetString());
        Assert.Contains("accumulated philosophy", thinkTool.GetProperty("description").GetString());
        Assert.Equal(recallTool.GetProperty("outputSchema").GetRawText(), thinkTool.GetProperty("outputSchema").GetRawText());
        foreach (var searchTool in new[] { recallTool, thinkTool })
        {
            var annotations = searchTool.GetProperty("annotations");
            Assert.False(annotations.TryGetProperty("readOnlyHint", out var readOnly) && readOnly.GetBoolean());
            Assert.False(annotations.GetProperty("destructiveHint").GetBoolean());
            Assert.True(annotations.GetProperty("openWorldHint").GetBoolean());
        }

        var traceTool = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == "trace");
        Assert.True(traceTool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("memory_id", out _));
        Assert.True(traceTool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.False(traceTool.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean());

        const string raw = "오늘 C#으로 기억 서버를 작성했다.";
        var remembered = Structured(await host.RpcAsync("tools/call", new
        {
            name = "remember",
            arguments = new
            {
                raw
            }
        }));
        var sourceId = remembered.GetProperty("source_id").GetString();
        var memory = remembered.GetProperty("memories")[0];
        var memoryId = memory.GetProperty("id").GetString()!;
        Assert.Equal(0, memory.GetProperty("depth").GetInt32());
        Assert.True(memory.TryGetProperty("created_at", out _));
        Assert.False(remembered.GetProperty("duplicate").GetBoolean());

        // A new HTTP client shares the same corpus; raw equality is checked before cognition.
        using var secondClient = new HttpClient { BaseAddress = host.Client.BaseAddress };
        var duplicated = Structured(await host.RpcAsync("tools/call", new
        {
            name = "remember",
            arguments = new
            {
                raw
            }
        }, secondClient));
        Assert.True(duplicated.GetProperty("duplicate").GetBoolean());
        Assert.Equal(sourceId, duplicated.GetProperty("source_id").GetString());
        Assert.Equal(1, host.Cognition.Extractions);

        var recalled = Structured(await host.RpcAsync("tools/call", new
        {
            name = "recall",
            arguments = new
            {
                query = "기억 서버",
                context = "오늘 작업"
            }
        }));
        Assert.Equal(memoryId, recalled.GetProperty("memories")[0].GetProperty("id").GetString());
        Assert.NotEqual(JsonValueKind.Null, recalled.GetProperty("memories")[0].GetProperty("last_recalled_at").ValueKind);
        var thought = Structured(await host.RpcAsync("tools/call", new
        {
            name = "think",
            arguments = new
            {
                topic = "기억 서버의 설계 원칙"
            }
        }));
        var thoughtMemory = Assert.Single(thought.GetProperty("memories").EnumerateArray());
        Assert.Equal(memoryId, thoughtMemory.GetProperty("id").GetString());
        Assert.Equal(0, thoughtMemory.GetProperty("depth").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, thoughtMemory.GetProperty("last_recalled_at").ValueKind);
        Assert.Equal(("기억 서버의 설계 원칙", (string?)null), host.Cognition.Selections[^1]);

        const string thinkContext = "  새 도구의 설계 방향을 비교 중\n";
        var contextualThought = Structured(await host.RpcAsync("tools/call", new
        {
            name = "think",
            arguments = new
            {
                topic = "기억 서버의 설계 원칙",
                context = thinkContext
            }
        }));
        Assert.Equal(memoryId, Assert.Single(contextualThought.GetProperty("memories").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(("기억 서버의 설계 원칙", thinkContext), host.Cognition.Selections[^1]);

        var traced = Structured(await host.RpcAsync("tools/call", new
        {
            name = "trace",
            arguments = new
            {
                memory_id = memoryId
            }
        }));
        Assert.Equal(raw, traced.GetProperty("sources")[0].GetProperty("raw").GetString());
        Assert.Equal(memoryId, traced.GetProperty("memory_id").GetString());
    }

    [Fact]
    public async Task RejectsCrossOriginRequestsAndInvalidHostAndProtectsCorpusOwnership()
    {
        await using var host = await RunningHost.StartAsync();
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "mcp");
        crossOrigin.Headers.Add("Origin", "https://untrusted.example");
        crossOrigin.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list"
        });
        using var blocked = await host.Client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        using var wrongHost = new HttpRequestMessage(HttpMethod.Post, "mcp");
        wrongHost.Headers.Host = "untrusted.example";
        wrongHost.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list"
        });
        using var rejectedHost = await host.Client.SendAsync(wrongHost);
        Assert.True(rejectedHost.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden);
        Assert.Throws<InputException>(() => AppHost.Build([], builder => RunningHost.Configure(builder, host.DirectoryPath, new CannedCognition())));

        using var sameOrigin = new HttpClient { BaseAddress = host.Client.BaseAddress };
        sameOrigin.DefaultRequestHeaders.Add("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        var allowed = await host.RpcAsync("tools/list", new
        {
        }, sameOrigin);
        Assert.True(allowed.TryGetProperty("result", out _), allowed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public Task RejectsBlankDataDirectoryBeforeCreatingCorpusFiles(string configuredDirectory)
    {
        return AssertConfigurationRejectedBeforeOpeningCorpusAsync(
            "Engine:DataDirectory", configuredDirectory);
    }

    [Theory]
    [InlineData("4294968")]
    [InlineData("2147483647")]
    public Task RejectsUnsupportedPollIntervalBeforeCreatingCorpusFiles(string configuredInterval)
    {
        return AssertConfigurationRejectedBeforeOpeningCorpusAsync(
            "Engine:SchedulerPollSeconds", configuredInterval);
    }

    [Fact]
    public void LargestSupportedPollIntervalPassesConfigurationAndTaskDelayValidation()
    {
        var options = new EngineOptions { SchedulerPollSeconds = 4_294_967 };
        options.Validate();

        // Cancellation avoids waiting while still exercising Task.Delay's real timeout validation.
        var delay = Task.Delay(
            TimeSpan.FromSeconds(options.SchedulerPollSeconds),
            TimeProvider.System,
            new CancellationToken(canceled: true));

        Assert.True(delay.IsCanceled);
    }

    private static async Task AssertConfigurationRejectedBeforeOpeningCorpusAsync(
        string settingName,
        string settingValue)
    {
        var contentRoot = Path.Combine(
            Path.GetTempPath(), "long-journey-invalid-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        WebApplication? app = null;
        try
        {
            var error = Assert.Throws<InputException>(() =>
            {
                app = AppHost.Build(["--contentRoot", contentRoot], builder =>
                {
                    RunningHost.Configure(builder, contentRoot, new CannedCognition());
                    builder.Configuration["Engine:SchedulerEnabled"] = "true";
                    builder.Configuration[settingName] = settingValue;
                });
            });

            Assert.Contains(settingName, error.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(contentRoot));
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }

            // This test owns the absolute, uniquely named directory created above.
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    private static JsonElement Structured(JsonElement response)
    {
        Assert.True(response.TryGetProperty("result", out var result), response.ToString());
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.ToString());
        return result.GetProperty("structuredContent");
    }

    private sealed class RunningHost : IAsyncDisposable
    {
        public required WebApplication App
        {
            get; init;
        }
        public required HttpClient Client
        {
            get; init;
        }
        public required string DirectoryPath
        {
            get; init;
        }
        public required CannedCognition Cognition
        {
            get; init;
        }
        private int sequence;

        public static async Task<RunningHost> StartAsync(Action<WebApplicationBuilder>? configure = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "long-journey-http-" + Guid.NewGuid().ToString("N"));
            var cognition = new CannedCognition();
            var app = AppHost.Build([], builder =>
            {
                Configure(builder, directory, cognition);
                configure?.Invoke(builder);
            });
            try
            {
                await app.StartAsync();
                var server = app.Services.GetRequiredService<IServer>();
                var addresses = server.Features.Get<IServerAddressesFeature>()!.Addresses;
                var address = Assert.Single(addresses);
                return new RunningHost
                {
                    App = app,
                    DirectoryPath = directory,
                    Cognition = cognition,
                    Client = new HttpClient
                    {
                        BaseAddress = new Uri(address + "/"),
                        Timeout = TimeSpan.FromSeconds(20)
                    }
                };
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public static void Configure(WebApplicationBuilder builder, string directory, CannedCognition cognition)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Engine:DataDirectory"] = directory,
                ["Engine:SchedulerEnabled"] = "false",
                ["Server:Port"] = "0"
            });
            builder.Services.AddSingleton<ICognition>(cognition);
        }

        public async Task<JsonElement> RpcAsync(string method, object parameters, HttpClient? client = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "mcp");
            request.Headers.Add("Accept", "application/json, text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
            request.Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref sequence),
                method,
                @params = parameters
            });
            using var response = await (client ?? Client).SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}");
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                var dataLines = new List<string>();
                foreach (var line in text.Split('\n'))
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        dataLines.Add(line[5..].TrimStart());
                    }
                }

                text = string.Join("\n", dataLines);
            }
            using var json = JsonDocument.Parse(text);
            return json.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            // directory is an absolute, unique child created by this test under the temp workspace.
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class CannedCognition : ICognition
    {
        public Task<CognitiveResult<IReadOnlyList<string>>> PrioritizeMeditationAsync(
            IReadOnlyList<MeditationPriorityCandidate> candidates,
            CallContext context, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Scheduler is disabled for HTTP tests.");
        }

        public int Extractions
        {
            get; private set;
        }
        public int Calls { get; private set; }
        public List<(string Query, string? Context)> Selections { get; } = [];
        public string EmbeddingSpace => "test-http:3";
        public Task<CognitiveResult<IReadOnlyList<ObservationProposal>>> ExtractAsync(
            string raw, CallContext context, CancellationToken cancellationToken)
        {
            Calls++;
            Extractions++;
            return Task.FromResult(new CognitiveResult<IReadOnlyList<ObservationProposal>>([new(raw)], "test-http"));
        }
        public Task<EmbeddingVector> EmbedAsync(
            string text, CallContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new EmbeddingVector(EmbeddingSpace, [1f, 1f, 1f]));
        }
        public Task<CognitiveResult<IReadOnlyList<string>>> SelectAsync(
            string query, string? context, IReadOnlyList<MemoryRecord> candidates,
            CallContext call, CancellationToken cancellationToken)
        {
            Calls++;
            Selections.Add((query, context));
            var memoryIds = MemoryTestData.Ids(candidates);
            var result = new CognitiveResult<IReadOnlyList<string>>(memoryIds, "test-http");
            return Task.FromResult(result);
        }
        public Task<CognitiveResult<IReadOnlyList<RelationProposal>>> AssimilateAsync(
            MemoryRecord observation, IReadOnlyList<MemoryRecord> candidates,
            CallContext context, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Scheduler is disabled for HTTP tests.");
        }

        public Task<CognitiveResult<IReadOnlyList<AbstractionProposal>>> AbstractAsync(
            IReadOnlyList<MemoryRecord> neighborhood, IReadOnlyList<SourceArtifact> sources,
            CognitionRole role, CallContext context, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Scheduler is disabled for HTTP tests.");
        }
    }
}
