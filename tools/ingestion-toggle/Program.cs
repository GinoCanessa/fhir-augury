using System.Text.Json;

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
              --enable      Set <Section>.IngestionPaused to false in each existing src/*/appsettings.local.json file.
              --disable     Set <Section>.IngestionPaused to true in each existing src/*/appsettings.local.json file.

            Only existing boolean IngestionPaused values are rewritten; files and properties are never created.
            Exit codes: 0 = no failures, 1 = at least one file failed, 2 = invalid arguments.
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

internal enum ToggleOutcome
{
    Changed,
    AlreadySet,
    NotFound,
    NotBoolean,
    RootNotObject,
}

internal static class IngestionToggleRunner
{
    public const string LocalSettingsFileName = "appsettings.local.json";
    public const string PropertyName = "IngestionPaused";

    private static readonly byte[] _utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] _trueLiteral = "true"u8.ToArray();
    private static readonly byte[] _falseLiteral = "false"u8.ToArray();

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
            string filePath = Path.Combine(candidate, LocalSettingsFileName);
            if (!File.Exists(filePath))
            {
                // Only projects that ship an appsettings.json can have a local override worth reporting.
                if (File.Exists(Path.Combine(candidate, "appsettings.json")))
                {
                    results.Add(new FileResult(filePath, FileStatus.Skipped, "File does not exist."));
                }

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
        string targetText = FormatBool(targetPaused);

        try
        {
            byte[] content = File.ReadAllBytes(filePath);
            ToggleOutcome outcome = Apply(content, targetPaused, out byte[] updated, out IReadOnlyList<string> sections);

            switch (outcome)
            {
                case ToggleOutcome.NotFound:
                    return new FileResult(filePath, FileStatus.Skipped, $"No <Section>.{PropertyName} setting found.");

                case ToggleOutcome.AlreadySet:
                    return new FileResult(filePath, FileStatus.Skipped, $"{FormatSections(sections)} already {targetText}.");

                case ToggleOutcome.NotBoolean:
                    return new FileResult(filePath, FileStatus.Failed, $"{FormatSections(sections)} exists but is not a boolean value.");

                case ToggleOutcome.RootNotObject:
                    return new FileResult(filePath, FileStatus.Failed, "JSON root is not an object.");
            }

            File.WriteAllBytes(filePath, updated);
            return new FileResult(filePath, FileStatus.Changed, $"{FormatSections(sections)}={targetText}");
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

    /// <summary>
    /// Locates every boolean <c>IngestionPaused</c> property that is a direct child of a top-level
    /// section object (e.g. <c>Jira.IngestionPaused</c>) and rewrites only the literal bytes of the
    /// values that differ from <paramref name="targetPaused"/>. All other bytes — formatting,
    /// comments, BOM, line endings, and unrelated settings — are copied through unchanged.
    /// </summary>
    internal static ToggleOutcome Apply(byte[] content, bool targetPaused, out byte[] updated, out IReadOnlyList<string> sections)
    {
        updated = content;

        int offset = content.AsSpan().StartsWith(_utf8Bom) ? _utf8Bom.Length : 0;
        Utf8JsonReader reader = new(
            content.AsSpan(offset),
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

        if (!reader.Read())
        {
            throw new JsonException("The file contains no JSON value.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            sections = [];
            return ToggleOutcome.RootNotObject;
        }

        List<string> found = [];
        List<string> nonBoolean = [];
        List<(int Start, int Length)> edits = [];
        string? currentSection = null;

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.CurrentDepth == 1)
            {
                currentSection = reader.GetString();
                continue;
            }

            if (reader.CurrentDepth != 2 || !reader.ValueTextEquals(PropertyName))
            {
                continue;
            }

            string path = $"{currentSection}.{PropertyName}";
            reader.Read();

            if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
            {
                nonBoolean.Add(path);
                continue;
            }

            found.Add(path);
            bool currentValue = reader.TokenType == JsonTokenType.True;
            if (currentValue != targetPaused)
            {
                edits.Add((offset + (int)reader.TokenStartIndex, currentValue ? _trueLiteral.Length : _falseLiteral.Length));
            }
        }

        if (nonBoolean.Count > 0)
        {
            sections = nonBoolean;
            return ToggleOutcome.NotBoolean;
        }

        sections = found;
        if (found.Count == 0)
        {
            return ToggleOutcome.NotFound;
        }

        if (edits.Count == 0)
        {
            return ToggleOutcome.AlreadySet;
        }

        byte[] replacement = targetPaused ? _trueLiteral : _falseLiteral;
        using MemoryStream buffer = new(content.Length + edits.Count);
        int position = 0;
        foreach ((int start, int length) in edits)
        {
            buffer.Write(content, position, start - position);
            buffer.Write(replacement);
            position = start + length;
        }

        buffer.Write(content, position, content.Length - position);
        updated = buffer.ToArray();
        return ToggleOutcome.Changed;
    }

    private static string FormatBool(bool value) => value ? "true" : "false";

    private static string FormatSections(IReadOnlyList<string> sections) => string.Join(", ", sections);

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
