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

        if (!CliOptions.TryParse(args, out IReadOnlyDictionary<string, bool> settings, out string? error))
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
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return IngestionToggleRunner.Run(repoRoot, settings, Console.Out, Console.Error);
    }

    private static bool HasHelpVerb(string[] args) =>
        args.Length == 1 && args[0] is "--help" or "-h" or "help";

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("""
            ingestion-toggle - update existing boolean source-local settings.

            Usage:
              ingestion-toggle --ingestion-paused true --ingest-on-startup-only false
              ingestion-toggle --reload-from-cache-on-startup false
              ingestion-toggle --<setting-name> <true|false> [--<setting-name> <true|false> ...]
              ingestion-toggle --enable
              ingestion-toggle --disable
              ingestion-toggle --help

            Setting flags use kebab-case names: IngestionPaused becomes --ingestion-paused.
            Both --setting true and --setting=true are accepted.

            Aliases:
              --ingest-on-startup-only <true|false>  Set RunIngestionOnStartupOnly.
              --enable                             Set IngestionPaused to false (resume ingestion).
              --disable                            Set IngestionPaused to true (pause ingestion).

            Only existing boolean properties directly inside the matching source section are rewritten.
            Only src/FhirAugury.Source.*/appsettings.local.json files are eligible.
            Missing files and properties are skipped, never created. Duplicate settings are rejected.
            Exit codes: 0 = no failures, 1 = at least one file failed, 2 = invalid arguments.
            """);
    }
}

internal static class CliOptions
{
    public const string IngestionPausedSetting = "ingestion-paused";

    public static bool TryParse(string[] args, out IReadOnlyDictionary<string, bool> settings, out string? error)
    {
        Dictionary<string, bool> values = new(StringComparer.Ordinal);
        settings = values;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            string name;
            bool value;

            if (argument is "--enable" or "--disable")
            {
                name = IngestionPausedSetting;
                value = argument == "--disable";
            }
            else
            {
                int separator = argument.IndexOf('=');
                string flag = separator < 0 ? argument : argument[..separator];
                if (!flag.StartsWith("--", StringComparison.Ordinal) ||
                    !IsSettingName(flag[2..]) ||
                    flag is "--help" or "--enable" or "--disable")
                {
                    error = $"Invalid setting flag: {flag}. Use --<kebab-case-name> <true|false>.";
                    return false;
                }

                string valueText;
                if (separator >= 0)
                {
                    valueText = argument[(separator + 1)..];
                }
                else if (index + 1 < args.Length)
                {
                    valueText = args[++index];
                }
                else
                {
                    error = $"Missing boolean value for {flag}. Specify true or false.";
                    return false;
                }

                if (!bool.TryParse(valueText, out value))
                {
                    error = $"Invalid boolean value for {flag}. Specify true or false.";
                    return false;
                }

                name = flag[2..] == "ingest-on-startup-only"
                    ? "run-ingestion-on-startup-only"
                    : flag[2..];
            }

            if (!values.TryAdd(name, value))
            {
                error = $"Setting --{name} was specified more than once (possibly through an alias).";
                return false;
            }
        }

        if (values.Count == 0)
        {
            error = "Specify at least one --<setting-name> <true|false>, or --enable/--disable.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsSettingName(string name) =>
        name.Length > 0 &&
        name[0] is >= 'a' and <= 'z' &&
        name.Split('-').All(part =>
            part.Length > 0 && part.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'));
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
    private const string SourceProjectPrefix = "FhirAugury.Source.";

    private static readonly byte[] _utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] _trueLiteral = "true"u8.ToArray();
    private static readonly byte[] _falseLiteral = "false"u8.ToArray();

    public static int Run(string repoRoot, bool enableIngestion, TextWriter output, TextWriter errorOutput) =>
        Run(repoRoot, new Dictionary<string, bool> { [CliOptions.IngestionPausedSetting] = !enableIngestion }, output, errorOutput);

    public static int Run(
        string repoRoot,
        IReadOnlyDictionary<string, bool> settings,
        TextWriter output,
        TextWriter errorOutput)
    {
        string root = string.IsNullOrWhiteSpace(repoRoot) ? RepositoryRootLocator.Find() : repoRoot;
        string srcDirectory = Path.Combine(root, "src");

        if (!Directory.Exists(srcDirectory))
        {
            errorOutput.WriteLine($"Repository source directory not found: {srcDirectory}");
            return 1;
        }

        string[] candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(srcDirectory, $"{SourceProjectPrefix}*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errorOutput.WriteLine($"Unable to enumerate source projects: {ex.Message}");
            return 1;
        }

        List<FileResult> results = [];
        foreach (string candidate in candidates)
        {
            string filePath = Path.Combine(candidate, LocalSettingsFileName);
            string sectionName = Path.GetFileName(candidate)[SourceProjectPrefix.Length..];
            results.Add(ToggleFile(filePath, sectionName, settings));
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

    public static FileResult ToggleFile(
        string filePath,
        string sectionName,
        IReadOnlyDictionary<string, bool> settings)
    {
        bool foundFile = false;
        try
        {
            FileAttributes attributes = File.GetAttributes(filePath);
            foundFile = true;
            string? directory = Path.GetDirectoryName(filePath);
            if (directory is null)
            {
                return new FileResult(filePath, FileStatus.Failed, "The settings path must include a source directory.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return new FileResult(filePath, FileStatus.Failed, "Linked settings files or source directories are not supported.");
            }

            byte[] content = File.ReadAllBytes(filePath);
            ToggleOutcome outcome = Apply(content, sectionName, settings, out byte[] updated, out IReadOnlyList<string> descriptions);

            switch (outcome)
            {
                case ToggleOutcome.NotFound:
                    return new FileResult(filePath, FileStatus.Skipped,
                        $"No requested setting found under {sectionName}: {string.Join(", ", settings.Keys.Select(name => $"--{name}"))}.");

                case ToggleOutcome.AlreadySet:
                    return new FileResult(filePath, FileStatus.Skipped, $"Already set: {string.Join(", ", descriptions)}.");

                case ToggleOutcome.NotBoolean:
                    return new FileResult(filePath, FileStatus.Failed, $"{string.Join(", ", descriptions)} exists but is not a boolean value.");

                case ToggleOutcome.RootNotObject:
                    return new FileResult(filePath, FileStatus.Failed, "JSON root is not an object.");
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                return new FileResult(filePath, FileStatus.Failed, "The settings file is read-only.");
            }

            WriteReplacement(filePath, content, updated);
            return new FileResult(filePath, FileStatus.Changed, string.Join(", ", descriptions));
        }
        catch (FileNotFoundException) when (!foundFile)
        {
            return new FileResult(filePath, FileStatus.Skipped, "File does not exist.");
        }
        catch (JsonException ex)
        {
            return new FileResult(filePath, FileStatus.Failed,
                $"Malformed JSON (line {ex.LineNumber}, byte {ex.BytePositionInLine}).");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new FileResult(filePath, FileStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Rewrites only selected boolean literal spans; JSON serialization would also change unrelated bytes.
    /// </summary>
    internal static ToggleOutcome Apply(
        byte[] content,
        string sectionName,
        IReadOnlyDictionary<string, bool> settings,
        out byte[] updated,
        out IReadOnlyList<string> descriptions)
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
            descriptions = [];
            return ToggleOutcome.RootNotObject;
        }

        List<string> found = [];
        List<string> nonBoolean = [];
        List<(int Start, int Length, bool Value)> edits = [];
        HashSet<string> matchedSettings = new(StringComparer.Ordinal);
        bool sourceSectionSeen = false;
        string? currentSection = null;

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.CurrentDepth == 1)
            {
                string? propertyName = reader.GetString();
                currentSection = string.Equals(propertyName, sectionName, StringComparison.OrdinalIgnoreCase)
                    ? propertyName
                    : null;
                if (currentSection is not null)
                {
                    if (sourceSectionSeen)
                    {
                        throw new InvalidDataException($"Duplicate source section: {sectionName}.");
                    }

                    sourceSectionSeen = true;
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        throw new InvalidDataException($"Source section {sectionName} is not an object.");
                    }
                }

                continue;
            }

            if (reader.CurrentDepth != 2 || currentSection is null)
            {
                continue;
            }

            string property = reader.GetString() ?? throw new JsonException("Missing property name.");
            string name = JsonNamingPolicy.KebabCaseLower.ConvertName(property);
            if (!settings.TryGetValue(name, out bool targetValue))
            {
                continue;
            }

            string path = $"{currentSection}.{property}";
            if (!matchedSettings.Add(name))
            {
                throw new InvalidDataException($"Duplicate setting: {path}.");
            }

            reader.Read();

            if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
            {
                nonBoolean.Add(path);
                continue;
            }

            found.Add($"{path}={FormatBool(targetValue)}");
            bool currentValue = reader.TokenType == JsonTokenType.True;
            if (currentValue != targetValue)
            {
                edits.Add((offset + (int)reader.TokenStartIndex, currentValue ? _trueLiteral.Length : _falseLiteral.Length, targetValue));
            }
        }

        if (nonBoolean.Count > 0)
        {
            descriptions = nonBoolean;
            return ToggleOutcome.NotBoolean;
        }

        descriptions = found;
        if (found.Count == 0)
        {
            return ToggleOutcome.NotFound;
        }

        if (edits.Count == 0)
        {
            return ToggleOutcome.AlreadySet;
        }

        using MemoryStream buffer = new(content.Length + edits.Count);
        int position = 0;
        foreach ((int start, int length, bool value) in edits)
        {
            buffer.Write(content, position, start - position);
            buffer.Write(value ? _trueLiteral : _falseLiteral);
            position = start + length;
        }

        buffer.Write(content, position, content.Length - position);
        updated = buffer.ToArray();
        return ToggleOutcome.Changed;
    }

    private static void WriteReplacement(string filePath, byte[] original, byte[] updated)
    {
        string temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (FileStream temporaryFile = new(temporaryPath, options))
            {
                temporaryFile.Write(updated);
                temporaryFile.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(filePath));
            }

            if (!File.ReadAllBytes(filePath).AsSpan().SequenceEqual(original))
            {
                throw new IOException("The settings file changed during the update; no replacement was made.");
            }

            File.Replace(temporaryPath, filePath, destinationBackupFileName: null);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string FormatBool(bool value) => value ? "true" : "false";

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
