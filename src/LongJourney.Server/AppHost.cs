using System.Net;
using LongJourney.Core;
using LongJourney.OpenAI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol.AspNetCore;

namespace LongJourney.Server;

public static class AppHost
{
    // The callback is an in-process test seam; production always uses OpenAiCognition.
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var noScheduler = args.Contains("--no-scheduler", StringComparer.Ordinal);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args.Where(x => x != "--no-scheduler").ToArray(),
            ApplicationName = typeof(AppHost).Assembly.GetName().Name
        });
        configure?.Invoke(builder);
        var engine = builder.Configuration.GetSection("Engine").Get<EngineOptions>() ?? new();
        if (noScheduler) engine.SchedulerEnabled = false;
        engine.DataDirectory = Path.GetFullPath(engine.DataDirectory, builder.Environment.ContentRootPath);
        engine.Validate();
        var openAi = builder.Configuration.GetSection("OpenAI").Get<OpenAiOptions>() ?? new();
        var port = builder.Configuration.GetValue("Server:Port", 5088);
        if (port is < 0 or > 65535) throw new InputException("Server:Port must be between 0 and 65535.");
        if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InputException("Use Server:Port for the local listener; custom Kestrel endpoints are not supported.");
        builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        // SDK trace logs can contain protocol data. Keep operational logs free of memory payloads.
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddSingleton(engine);
        builder.Services.AddSingleton(openAi);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<CorpusLease>();
        builder.Services.AddSingleton<IMemoryStore>(services =>
        {
            // Source recovery must never race a second process using the same corpus.
            _ = services.GetRequiredService<CorpusLease>();
            return new SqliteMemoryStore(engine);
        });
        builder.Services.AddSingleton<IUsageLedger>(services => services.GetRequiredService<IMemoryStore>());
        builder.Services.AddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        builder.Services.TryAddSingleton<ICognition, OpenAiCognition>();
        builder.Services.AddSingleton<MemorySearch>();
        builder.Services.AddSingleton<IMemorySearch>(services => services.GetRequiredService<MemorySearch>());
        builder.Services.AddSingleton<MemoryEngine>();
        builder.Services.AddSingleton<ConsolidationEngine>();
        builder.Services.AddSingleton<MemoryScheduler>();
        builder.Services.AddHostedService<SchedulerWorker>();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<MemoryTools>(JsonDefaults.Options);

        var app = builder.Build();
        try
        {
            _ = app.Services.GetRequiredService<MemoryEngine>();
            app.Use(async (context, next) =>
            {
                if (!IsLocalRequest(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(context);
            });
            app.MapMcp("/mcp");
            return app;
        }
        catch
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static bool IsLocalRequest(HttpRequest request)
    {
        var host = request.Host.Host;
        if (!(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              host is "127.0.0.1" or "[::1]" or "::1")) return false;
        if (!request.Headers.TryGetValue("Origin", out var origins)) return true;
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)) return false;
        // Browser requests must have exactly this server's origin, including host and port.
        return origin.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase) &&
               origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
               origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0 &&
               origin.UserInfo.Length == 0;
    }
}
