namespace FhirAugury.Tools.FhirSpecReview;

/// <summary>
/// Entry point for the <c>fhir-spec-review</c> tool — a read-only consumer of
/// fhir-augury's GitHub source cache (the current HL7/fhir build under review),
/// the external <c>fhir-spec.db</c> baseline vocabulary, a published baseline
/// site, and <c>dictionary.db</c>. It runs the FMG-style spec-review content
/// checks (<c>process</c>) and emits a per-workgroup static HTML report
/// (<c>report</c>).
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

            case "process":
            {
                if (HasHelpFlag(rest))
                {
                    WriteUsage(Console.Out);
                    return 0;
                }
                if (!CliOptions.TryParseProcess(rest, out ProcessOptions options, out string? error))
                {
                    await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
                    WriteUsage(Console.Error);
                    return 2;
                }
                return await RunProcessAsync(options).ConfigureAwait(false);
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
                return await RunReportAsync(options).ConfigureAwait(false);
            }

            default:
                await Console.Error.WriteLineAsync($"Unknown verb: {verb}").ConfigureAwait(false);
                WriteUsage(Console.Error);
                return 2;
        }
    }

    // Phase 1 skeleton: the verb bodies are filled in by later phases.
    private static Task<int> RunProcessAsync(ProcessOptions options)
    {
        return ProcessRunner.RunAsync(options, ConsoleLogger.Instance);
    }

    private static Task<int> RunReportAsync(ReportOptions options)
    {
        return ReportRunner.RunAsync(options);
    }

    private static bool HasHelpFlag(string[] args) =>
        Array.Exists(args, a => a is "--help" or "-h");

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("""
            fhir-spec-review — FMG-style content-quality review of the current HL7/fhir build.

            Usage:
              fhir-spec-review process [options]
              fhir-spec-review report  [options]
              fhir-spec-review --help

            process options:
              --github-db <path>         GitHub source SQLite DB (default: ./data/github.db)
              --github-cache <path>      GitHub source cache root (default: ./cache)
              --repo <owner/name>        Repository under review (default: HL7/fhir)
              --fhir-spec-db <path>      Baseline vocabulary DB (default: ./cache/fhir-spec.db)
              --baseline-release <rel>   Baseline FHIR release, e.g. R5 (default: R5)
              --baseline-site <path>     Published baseline site folder (required)
              --dictionary-db <path>     Dictionary DB (default: ./cache/dictionary.db)
              --review-db <path>         Output review SQLite DB (default: ./cache/fhir-spec-review.db)
              --drop-tables              Drop and recreate the review schema first

            report options:
              --review-db <path>         Review SQLite DB to read (default: ./cache/fhir-spec-review.db)
              --out <dir>                Output directory for the static HTML site (default: ./cache/fhir-spec-review-site)
              --force                    Overwrite an existing output directory
            """);
    }
}
