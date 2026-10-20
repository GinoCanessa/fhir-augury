namespace FhirAugury.Tools.NotesSite;

/// <summary>
/// Entry point for the <c>notes-site</c> tool — a read-only ballot-note review
/// surface. It reads the notes SQLite database owned by the BallotNotes
/// processor (<c>FhirAugury.Processor.GitHub.Fhir.BallotNotes</c>) and emits a
/// single self-contained static HTML SPA (<c>report</c>), modelled on
/// <c>fhir-spec-review</c> and <c>ticket-site</c>.
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
            notes-site — self-contained ballot-note review SPA (read-only renderer).

            Reads a verified immutable snapshot from the BallotNotes processor and
            emits a static HTML review site.

            Usage:
              notes-site report [options]   Emit the static HTML review site from a snapshot.
              notes-site --help

            report options:
              --snapshot-db <path> Immutable BallotNotes snapshot SQLite file (required).
              --snapshot-descriptor <path> Trusted snapshot descriptor JSON (required).
              --out <dir>     Output directory for the static site (default: ./cache/notes-site).
              --title <text>  Site title (default: "FHIR Ballot Notes").
              --force         Overwrite an existing output directory.
            """);
    }
}
