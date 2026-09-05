using System.Globalization;
using LongJourney.Core;

namespace LongJourney.Server;

/// <summary>Report-only entry point: deliberately does not build AppHost or a mutable store.</summary>
public static class DailyReportCommand
{
    public static int? Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error,
        TimeProvider? timeProvider = null)
    {
        if (!arguments.Any(argument => argument == "--daily-report" ||
            argument.StartsWith("--daily-report=", StringComparison.Ordinal)))
        {
            return null;
        }

        try
        {
            string? period = null;
            var configurationArguments = new List<string>();
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                if (argument == "--reindex")
                {
                    throw new InputException("--daily-report cannot be combined with --reindex.");
                }

                if (argument == "--daily-report" || argument.StartsWith("--daily-report=", StringComparison.Ordinal))
                {
                    if (period is not null)
                    {
                        throw new InputException("Specify --daily-report once.");
                    }

                    if (argument == "--daily-report")
                    {
                        if (++index >= arguments.Count)
                        {
                            throw new InputException("--daily-report requires YYYY-MM-DD or YYYY-MM-DD..YYYY-MM-DD.");
                        }
                        period = arguments[index];
                    }
                    else
                    {
                        period = argument["--daily-report=".Length..];
                    }
                }
                else if (argument != "--no-scheduler")
                {
                    configurationArguments.Add(argument);
                }
            }

            var dates = period!.Split("..", StringSplitOptions.None);
            if (dates.Length is < 1 or > 2 || !TryDate(dates[0], out var first) ||
                !TryDate(dates[^1], out var last) || first > last)
            {
                throw new InputException("Use YYYY-MM-DD or an ascending YYYY-MM-DD..YYYY-MM-DD range.");
            }

            // Reading configuration does not initialize services, perform schema writes or recover Sources.
            var builder = AppHost.CreateHostBuilder(configurationArguments.ToArray());
            var configuration = builder.Configuration;
            using var configurationLifetime = (IDisposable)configuration;
            var dataDirectory = configuration["Engine:DataDirectory"] ?? "data";
            var timeZoneId = configuration["Engine:TimeZoneId"] ?? "Asia/Seoul";
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                throw new InputException("Engine:DataDirectory must not be empty.");
            }

            var reports = new DailyReportService(
                Path.GetFullPath(dataDirectory, builder.Environment.ContentRootPath), timeZoneId, timeProvider);
            for (var date = first; ; date = date.AddDays(1))
            {
                var report = reports.Export(date);
                output.WriteLine(report.MarkdownPath);
                output.WriteLine(report.JsonPath);
                if (date == last)
                {
                    break;
                }
            }
            return 0;
        }
        catch (InputException exception)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            Microsoft.Data.Sqlite.SqliteException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error.WriteLine($"Daily report could not be exported ({exception.GetType().Name}). Check the data directory, database and time zone.");
            return 2;
        }
    }

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
