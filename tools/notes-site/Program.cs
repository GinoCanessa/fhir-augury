namespace FhirAugury.Tools.NotesSite;

/// <summary>
/// Entry point for the <c>notes-site</c> tool — a self-contained ballot-note
/// review surface. It owns a notes SQLite database written one unit at a time
/// by the <c>notes-artifact</c> / <c>notes-page</c> / <c>notes-datatype</c>
/// skills (<c>write</c>) and emits a single self-contained static HTML SPA
/// (<c>report</c>), modelled on <c>fhir-spec-review</c> and <c>ticket-site</c>.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            WriteUsage(Console.Error);
            return 2;
        }

        string verb = args[0];
        string[] rest = args[1..];

        switch (verb)
        {
            case "--help":
            case "-h":
            case "help":
                WriteUsage(Console.Out);
                return 0;

            case "write":
            {
                if (HasHelpFlag(rest))
                {
                    WriteUsage(Console.Out);
                    return 0;
                }
                if (!CliOptions.TryParseWrite(rest, out WriteOptions options, out string? error))
                {
                    await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
                    WriteUsage(Console.Error);
                    return 2;
                }
                return await WriteRunner.RunAsync(options).ConfigureAwait(false);
            }

            case "report":
            {
                if (HasHelpFlag(rest))
                {
                    WriteUsage(Console.Out);
                    return 0;
                }
                if (!CliOptions.TryParseReport(rest, out ReportOptions options, out string? error))
                {
                    await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
                    WriteUsage(Console.Error);
                    return 2;
                }
                return await ReportRunner.RunAsync(options).ConfigureAwait(false);
            }

            default:
                await Console.Error.WriteLineAsync($"Unknown verb: {verb}").ConfigureAwait(false);
                WriteUsage(Console.Error);
                return 2;
        }
    }

    private static bool HasHelpFlag(string[] args) =>
        Array.Exists(args, a => a is "--help" or "-h");

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("""
            notes-site — ballot-note persistence + self-contained review SPA.

            Usage:
              notes-site write  [options]   Persist one drafted ballot note into the notes DB.
              notes-site report [options]   Emit the static HTML review site from the notes DB.
              notes-site --help

            write options:
              --db <path>     Notes SQLite DB (default: ./cache/notes.db; created if absent).
              --in <path>     JSON payload file (a NoteWritePayload). Reads stdin when omitted.
              --drop-tables   Drop and recreate the notes schema first (clean re-run).

            report options:
              --db <path>     Notes SQLite DB to read (default: ./cache/notes.db).
              --out <dir>     Output directory for the static site (default: ./cache/notes-site).
              --title <text>  Site title (default: "FHIR Ballot Notes").
              --force         Overwrite an existing output directory.
            """);
    }
}
