namespace FhirAugury.Tools.BallotNotesReallocateWg;

/// <summary>
/// Entry point for <c>ballotnotes-reallocate-wg</c> — a one-off, idempotent
/// maintenance command that re-runs <em>only</em> the merged
/// <c>OwningWorkGroupResolver</c> over existing ballot-note rows and re-stamps the
/// four owning-work-group columns. No commit-window walk, no structural diff, no
/// AI/prose authoring; every other field is preserved. Modelled on
/// <c>notes-site</c>.
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

            case "reallocate":
            {
                if (HasHelpFlag(rest))
                {
                    WriteUsage(Console.Out);
                    return 0;
                }
                if (!CliOptions.TryParseReallocate(rest, out ReallocateOptions options, out string? error))
                {
                    await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
                    WriteUsage(Console.Error);
                    return 2;
                }
                return await ReallocateRunner.RunAsync(options).ConfigureAwait(false);
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
            ballotnotes-reallocate-wg — re-stamp owning Work Groups on existing ballot notes.

            Re-runs ONLY the deterministic owning-WG resolver over the rows already in
            the notes DB and writes the corrected WorkGroup / WorkGroupCode /
            WorkGroupNames / WorkGroupCodes back in place. No commit-window walk, no
            structural diff, no AI/prose authoring; every other field is preserved.

            Usage:
              ballotnotes-reallocate-wg reallocate --clone <path> [options]
              ballotnotes-reallocate-wg --help

            reallocate options:
              --clone <path>        Local repo clone for repo-read + DataType HEAD listing (required).
              --db <path>           Notes SQLite DB to re-stamp (default: ./cache/ballot-notes.db).
              --repo <owner/name>   Restrict the run to one repository (required if the DB spans repos).
              --dry-run             Print intended per-note changes; write nothing (opens the DB read-only).
              --github-db <path>    Read-only GitHub source DB (default: ./cache/github.db).
              --fhir-r6-db <path>   Read-only current-build FHIR R6 DB (default: ./cache/fhir-r6.db).
              --fhir-spec-db <path> Read-only published FHIR spec DB (default: ./cache/fhir-spec.db).
              --work-group-hint <wg> Ticket-fallback hint (default: empty; not persisted per note).
              --allow-stale-clone   Skip the clone HEAD == note HeadSha guard.
              --allow-mixed-heads   Allow selected notes to span multiple HeadSha values.

            After a write run, regenerate the notes-site SPA / index-notes so groupings reflect the new owners.
            """);
    }
}
