using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FhirAugury.Tools.IngestionToggle;

public static class Program
{
    public static int Main(string[] args)
    {
        if (HasHelpVerb(args))
        {
            WriteUsage(Console.Out);
            return 0;
        }

        if (!CliOptions.TryParse(args, out bool enable, out string? error))
        {
            Console.Error.WriteLine(error);
            WriteUsage(Console.Error);
            return 2;
        }

        string repoRoot;
        try
        {
            repoRoot = RepositoryRootLocator.Find();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return IngestionToggleRunner.Run(repoRoot, enable, Console.Out, Console.Error);
    }

    private static bool HasHelpVerb(string[] args) =>
        args.Length > 0 && args[0] is "--help" or "-h" or "help";

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("""
            ingestion-toggle — toggle IngestionPaused in existing source-local appsettings files.

            Usage:
              ingestion-toggle --enable
              ingestion-toggle --disable
              ingestion-toggle --help

            Flags:
              --enable      Set IngestionPaused to false for each existing src/*/appsettings.local.json file.
              --disable     Set IngestionPaused to true for each existing src/*/appsettings.local.json file.
            """);
    }
}

internal static class CliOptions
{
    public static bool TryParse(string[] args, out bool enable, out string? error)
    {
        enable = false;
        bool seen = false;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--enable":
                    if (seen)
                    {
                        enable = false;
                        error = "Specify exactly one of --enable or --disable.";
                        return false;
                    }

                    enable = true;
                    seen = true;
                    break;

                case "--disable":
                    if (seen)
                    {
                        enable = false;
                        error = "Specify exactly one of --enable or --disable.";
                        return false;
                    }

                    enable = false;
                    seen = true;
                    break;

                default:
                    enable = false;
                    error = $"Unknown option: {arg}";
                    return false;
            }
        }

        if (!seen)
        {
            enable = false;
            error = "Missing required flag: --enable or --disable.";
            return false;
        }

        error = null;
        return true;
    }
}

internal enum FileStatus
{
    Changed,
    Skipped,
    Failed,
}

internal sealed record FileResult(string Path, FileStatus Status, string Reason);

internal static class IngestionToggleRunner
{
    public static int Run(string repoRoot, bool enableIngestion, TextWriter output, TextWriter errorOutput)
    {
        string root = string.IsNullOrWhiteSpace(repoRoot) ? RepositoryRootLocator.Find() : repoRoot;
        string srcDirectory = Path.Combine(root, "src");

        if (!Directory.Exists(srcDirectory))
        {
            errorOutput.WriteLine($"Repository source directory not found: {srcDirectory}");
            return 1;
        }

        List<FileResult> results = [];

        foreach (string candidate in Directory.EnumerateDirectories(srcDirectory).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string filePath = Path.Combine(candidate, "appsettings.local.json");
            if (!File.Exists(filePath))
            {
                continue;
            }

            results.Add(ToggleFile(filePath, enableIngestion));
        }

        foreach (FileResult result in results)
        {
            switch (result.Status)
            {
                case FileStatus.Changed:
                    output.WriteLine($"CHANGED  {GetDisplayPath(root, result.Path)} | {result.Reason}");
                    break;
                case FileStatus.Skipped:
                    output.WriteLine($"SKIPPED  {GetDisplayPath(root, result.Path)} | {result.Reason}");
                    break;
                case FileStatus.Failed:
                    errorOutput.WriteLine($"FAILED   {GetDisplayPath(root, result.Path)} | {result.Reason}");
                    break;
            }
        }

        int changedCount = results.Count(r => r.Status == FileStatus.Changed);
        int skippedCount = results.Count(r => r.Status == FileStatus.Skipped);
        int failedCount = results.Count(r => r.Status == FileStatus.Failed);
        output.WriteLine($"Summary: {changedCount} changed, {skippedCount} skipped, {failedCount} failed");
        return failedCount > 0 ? 1 : 0;
    }

    public static FileResult ToggleFile(string filePath, bool enableIngestion)
    {
        if (!File.Exists(filePath))
        {
            return new FileResult(filePath, FileStatus.Skipped, "File does not exist.");
        }

        bool targetPaused = !enableIngestion;

        try
        {
            string json = File.ReadAllText(filePath);
            JsonNode? root = JsonNode.Parse(json);
            if (root is null)
            {
                return new FileResult(filePath, FileStatus.Failed, "JSON file is empty or null.");
            }

            bool found = false;
            bool changed = UpdateIngestionPausedValue(root, targetPaused, ref found);
            if (!found)
            {
                return new FileResult(filePath, FileStatus.Skipped, "No IngestionPaused setting found.");
            }

            if (!changed)
            {
                return new FileResult(filePath, FileStatus.Skipped, $"IngestionPaused already set to {targetPaused.ToString().ToLowerInvariant()}.");
            }

            File.WriteAllText(
                filePath,
                root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }) + Environment.NewLine);
            return new FileResult(filePath, FileStatus.Changed, $"IngestionPaused={targetPaused.ToString().ToLowerInvariant()}");
        }
        catch (JsonException ex)
        {
            return new FileResult(filePath, FileStatus.Failed, $"Malformed JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new FileResult(filePath, FileStatus.Failed, ex.Message);
        }
    }

    private static bool UpdateIngestionPausedValue(JsonNode? node, bool targetPaused, ref bool found)
    {
        if (node is JsonObject rootObject)
        {
            bool changed = false;
            foreach ((string key, JsonNode? value) in rootObject)
            {
                if (key == "IngestionPaused")
                {
                    found = true;
                    if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out bool currentValue))
                    {
                        if (currentValue != targetPaused)
                        {
                            rootObject[key] = targetPaused;
                            changed = true;
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("IngestionPaused exists but is not a boolean value.");
                    }
                }
                else if (value is not null)
                {
                    changed |= UpdateIngestionPausedValue(value, targetPaused, ref found);
                }
            }

            return changed;
        }

        if (node is JsonArray array)
        {
            bool changed = false;
            foreach (JsonNode? item in array)
            {
                changed |= UpdateIngestionPausedValue(item, targetPaused, ref found);
            }

            return changed;
        }

        return false;
    }

    private static string GetDisplayPath(string repoRoot, string fullPath) =>
        Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
}

internal static class RepositoryRootLocator
{
    public static string Find()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "fhir-augury.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository root (fhir-augury.slnx not found).");
    }
}
