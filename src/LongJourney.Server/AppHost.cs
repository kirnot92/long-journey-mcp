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
    public static WebApplication Build(IReadOnlyList<string> args, Action<WebApplicationBuilder>? configure = null)
    {
        var disableScheduler = false;
        var hostArguments = new List<string>();
        foreach (var argument in args)
        {
            if (argument == "--no-scheduler")
            {
                disableScheduler = true;
            }
            else
            {
                hostArguments.Add(argument);
            }
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = hostArguments.ToArray(),
            ApplicationName = typeof(AppHost).Assembly.GetName().Name,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        configure?.Invoke(builder);

        var engineOptions = ReadEngineOptions(builder, disableScheduler);
        var openAiOptions = builder.Configuration.GetSection("OpenAI").Get<OpenAiOptions>() ?? new();
        ConfigureLocalListener(builder);
        ConfigureOperationalLogging(builder);
        builder.Services.AddSingleton(new OpenAiApiKeySource(
            builder.Environment.ContentRootPath, builder.Configuration["OpenAI:ApiKeyFile"]));
        RegisterMemoryServices(builder.Services, engineOptions, openAiOptions);

        builder.Services.AddRazorPages();

        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(MemoryTools.CreateTools(engineOptions));

        var app = builder.Build();
        try
        {
            // Resolve now so corpus ownership and source archive recovery complete before serving requests.
            _ = app.Services.GetRequiredService<MemoryEngine>();
            ConfigureRequestPipeline(app);
            return app;
        }
        catch
        {
            // Failed initialization must release any corpus lease acquired through dependency injection.
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static EngineOptions ReadEngineOptions(
        WebApplicationBuilder builder,
        bool disableScheduler)
    {
        var options = builder.Configuration.GetSection("Engine").Get<EngineOptions>() ?? new();
        if (disableScheduler)
        {
            options.SchedulerEnabled = false;
        }

        // Resolving an empty path would turn it into the content root and hide the invalid setting.
        options.Validate();
        options.DataDirectory = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
        return options;
    }

    private static void ConfigureLocalListener(WebApplicationBuilder builder)
    {
        var port = builder.Configuration.GetValue("Server:Port", 5088);
        if (port is < 0 or > 65535)
        {
            throw new InputException("Server:Port must be between 0 and 65535.");
        }

        using var configuredEndpoints = builder.Configuration
            .GetSection("Kestrel:Endpoints").GetChildren().GetEnumerator();
        if (configuredEndpoints.MoveNext())
        {
            throw new InputException("Use Server:Port for the local listener; custom Kestrel endpoints are not supported.");
        }

        builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
    }

    private static void ConfigureOperationalLogging(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Services.Configure<ConsoleLoggerOptions>(
            options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // SDK trace logs can contain protocol data. Keep operational logs free of memory payloads.
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    }

    private static void RegisterMemoryServices(
        IServiceCollection services,
        EngineOptions engineOptions,
        OpenAiOptions openAiOptions)
    {
        services.AddSingleton(engineOptions);
        services.AddSingleton(openAiOptions);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CorpusLease>();
        services.AddSingleton<IMemoryStore>(provider =>
        {
            // The lock must be held before the store recovers interrupted extraction.
            _ = provider.GetRequiredService<CorpusLease>();
            return new SqliteMemoryStore(engineOptions);
        });
        services.AddSingleton<IInspectionReader>(provider => (IInspectionReader)provider.GetRequiredService<IMemoryStore>());
        services.AddSingleton<IUsageLedger>(provider => provider.GetRequiredService<IMemoryStore>());
        services.AddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.TryAddSingleton<ICognition>(provider => new OpenAiCognition(
            provider.GetRequiredService<HttpClient>(),
            openAiOptions,
            engineOptions,
            provider.GetRequiredService<IUsageLedger>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<OpenAiApiKeySource>().Read));
        services.AddSingleton<MemorySearch>();
        services.AddSingleton<IMemorySearch>(provider => provider.GetRequiredService<MemorySearch>());
        services.AddSingleton<MemoryEngine>();
        services.AddSingleton<ConsolidationEngine>();
        services.AddSingleton<MemoryScheduler>();
        services.AddHostedService<SchedulerWorker>();
    }

    private static void ConfigureRequestPipeline(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!IsLocalRequest(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/inspect"))
            {
                await next(context);
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            try
            {
                await next(context);
            }
            catch (Exception exception) when (!context.Response.HasStarted && exception is not OperationCanceledException)
            {
                app.Logger.LogWarning("Inspection read failed ({ErrorType}).", exception.GetType().Name);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("저장된 정보를 읽을 수 없습니다. 잠시 후 다시 확인해 주세요.");
            }
        });
        app.UseStaticFiles();
        app.MapRazorPages();
        app.MapMcp("/mcp");
    }

    private static bool IsLocalRequest(HttpRequest request)
    {
        var host = request.Host.Host;
        var isLocalHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                          host is "127.0.0.1" or "[::1]" or "::1";
        if (!isLocalHost)
        {
            return false;
        }

        if (!request.Headers.TryGetValue("Origin", out var origins))
        {
            return true;
        }

        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin))
        {
            return false;
        }

        // Browser requests must have exactly this server's origin, including host and port.
        return origin.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase) &&
               origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
               origin.AbsolutePath == "/" &&
               origin.Query.Length == 0 &&
               origin.Fragment.Length == 0 &&
               origin.UserInfo.Length == 0;
    }
}
