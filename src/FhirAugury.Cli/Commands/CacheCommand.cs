using System.CommandLine;
using System.Text.Json;
using FhirAugury.Models.Caching;

namespace FhirAugury.Cli.Commands;

public static class CacheCommand
{
    private static readonly string[] Sources = ["jira", "zulip", "confluence"];

    public static Command Create(Option<bool> jsonOption)
    {
        var cacheCommand = new Command("cache", "Manage the response cache");

        cacheCommand.Add(CreateStatsCommand(jsonOption));
        cacheCommand.Add(CreateClearCommand());

        return cacheCommand;
    }

    private static Command CreateStatsCommand(Option<bool> jsonOption)
    {
        var statsCommand = new Command("stats", "Show cache size per source");

        var cachePathOption = new Option<string>("--cache-path")
        {
            Description = "Cache root directory",
            DefaultValueFactory = _ => "./cache",
        };

        statsCommand.Add(cachePathOption);

        statsCommand.SetAction((parseResult, _) =>
        {
            var cachePath = parseResult.GetValue(cachePathOption)!;
            var json = parseResult.GetValue(jsonOption);
            var fullPath = Path.GetFullPath(cachePath);

            if (!Directory.Exists(fullPath))
            {
                Console.WriteLine($"Cache directory does not exist: {fullPath}");
                return Task.CompletedTask;
            }

            var cache = new FileSystemResponseCache(fullPath);

            var allStats = Sources.Select(s => cache.GetStats(s)).ToList();
            var totalFiles = allStats.Sum(s => s.FileCount);
            var totalBytes = allStats.Sum(s => s.TotalBytes);

            if (json)
            {
                var output = new
                {
                    sources = allStats.Select(s => new
                    {
                        name = s.Source,
                        fileCount = s.FileCount,
                        totalBytes = s.TotalBytes,
                        subPaths = s.SubPaths,
                    }),
                    totalFiles,
                    totalBytes,
                };
                Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
                return Task.CompletedTask;
            }

            Console.WriteLine($"{"Source",-14} {"Files",8}  {"Size",12}  Sub-paths");
            Console.WriteLine(new string('─', 60));

            foreach (var stats in allStats)
            {
                var sizeStr = FormatBytes(stats.TotalBytes);
                var subPathStr = stats.SubPaths.Count > 0 ? string.Join(", ", stats.SubPaths.Take(5)) : "(root)";
                if (stats.SubPaths.Count > 5) subPathStr += ", ...";
                Console.WriteLine($"{stats.Source,-14} {stats.FileCount,8}  {sizeStr,12}  {subPathStr}");
            }

            Console.WriteLine(new string('─', 60));
            Console.WriteLine($"{"Total",-14} {totalFiles,8}  {FormatBytes(totalBytes),12}");

            return Task.CompletedTask;
        });

        return statsCommand;
    }

    private static Command CreateClearCommand()
    {
        var clearCommand = new Command("clear", "Clear cached responses");

        var sourceOption = new Option<string?>("--source")
        {
            Description = "Clear only this source's cache (jira, zulip, confluence)",
        };

        var cachePathOption = new Option<string>("--cache-path")
        {
            Description = "Cache root directory",
            DefaultValueFactory = _ => "./cache",
        };

        clearCommand.Add(sourceOption);
        clearCommand.Add(cachePathOption);

        clearCommand.SetAction((parseResult, _) =>
        {
            var source = parseResult.GetValue(sourceOption);
            var cachePath = parseResult.GetValue(cachePathOption)!;
            var fullPath = Path.GetFullPath(cachePath);

            if (!Directory.Exists(fullPath))
            {
                Console.WriteLine($"Cache directory does not exist: {fullPath}");
                return Task.CompletedTask;
            }

            var cache = new FileSystemResponseCache(fullPath);

            if (source is not null)
            {
                cache.Clear(source);
                Console.WriteLine($"Cleared cache for {source}.");
            }
            else
            {
                cache.ClearAll();
                Console.WriteLine("Cleared all caches.");
            }

            return Task.CompletedTask;
        });

        return clearCommand;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
    };
}
