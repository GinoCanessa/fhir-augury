using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.DictionaryBuild;

/// <summary>
/// Entry point for the <c>dictionary-build</c> tool — a one-shot, deterministic
/// full rebuild of <c>cache/dictionary.db</c> from the spell-check source files
/// under <c>dictionary/</c>. Reuses <see cref="DictionaryDatabase"/> (the same
/// builder the services use on startup) with <c>ForceRebuild = true</c>.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (HasHelpVerb(args))
        {
            WriteUsage(Console.Out);
            return 0;
        }

        if (!CliOptions.TryParse(args, out BuildOptions options, out string? error))
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            WriteUsage(Console.Error);
            return 2;
        }

        string sourcePath = Path.GetFullPath(options.SourcePath);
        string outPath = Path.GetFullPath(options.OutPath);

        // Pre-validate the source dir up front. EnsureCreatedAsync only logs a
        // warning and returns when the source is missing, which would otherwise
        // look like success (exit 0) to a contributor running from the wrong CWD.
        if (!Directory.Exists(sourcePath))
        {
            await Console.Error.WriteLineAsync(
                $"Dictionary source directory not found: {sourcePath}\n" +
                "Pass --source <dir>, or run this tool from the repository root " +
                "(where ./dictionary lives).").ConfigureAwait(false);
            return 1;
        }

        bool hasSourceFiles =
            Directory.EnumerateFiles(sourcePath, "*.words.txt").Any()
            || Directory.EnumerateFiles(sourcePath, "*.typo.txt").Any();
        if (!hasSourceFiles)
        {
            await Console.Error.WriteLineAsync(
                $"No dictionary source files (*.words.txt / *.typo.txt) found in: {sourcePath}\n" +
                "Pass --source <dir>, or run this tool from the repository root " +
                "(where ./dictionary lives).").ConfigureAwait(false);
            return 1;
        }

        DictionaryDatabaseOptions buildOptions = new()
        {
            SourcePath = sourcePath,
            DatabasePath = outPath,
            ForceRebuild = true,
        };

        try
        {
            await DictionaryDatabase.EnsureCreatedAsync(buildOptions, ConsoleLogger.Instance).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"Dictionary rebuild failed: {ex.Message}\n" +
                "If a running service or tool holds cache/dictionary.db open, close " +
                "it and retry.").ConfigureAwait(false);
            return 1;
        }

        // Post-validate: the build is only a success if the output DB now exists.
        if (!File.Exists(outPath))
        {
            await Console.Error.WriteLineAsync(
                $"Dictionary rebuild reported no error but the output DB was not " +
                $"created: {outPath}").ConfigureAwait(false);
            return 1;
        }

        (long words, long typos) = CountRows(outPath);
        Console.WriteLine($"Dictionary rebuilt: {outPath} ({words} words, {typos} typos)");
        return 0;
    }

    private static (long Words, long Typos) CountRows(string dbPath)
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using SqliteConnection connection = new(connectionString);
        connection.Open();
        return (Count(connection, "words"), Count(connection, "typos"));
    }

    private static long Count(SqliteConnection connection, string table)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static bool HasHelpVerb(string[] args) =>
        args.Length > 0 && args[0] is "--help" or "-h" or "help";

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("""
            dictionary-build — rebuild cache/dictionary.db from the dictionary/ source files.

            Performs a full, deterministic rebuild (always overwrites the output DB).
            Run after editing anything under dictionary/.

            Usage:
              dictionary-build [options]
              dictionary-build --help

            Options:
              --source <dir>    Dictionary source directory (default: ./dictionary)
              --out <path>      Output SQLite DB path (default: ./cache/dictionary.db)
              --force           No-op alias; the tool always performs a full rebuild

            Run from the repository root so the relative defaults resolve.
            """);
    }
}
