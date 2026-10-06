using System.Globalization;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Maintenance;

internal static class PlannerRetainedStateCommand
{
    internal const string Help =
        """
        Planner owner-only offline operation (Windows local files only).
        retained-state capture
          --database <existing-absolute-file>
          --pre-cutover-backup <absolute-file-or-empty-if-unset-and-activation-disabled>
          --snapshots <absolute-directory>
          --activate-run-backed <true|false>
          --snapshot-schema-version <positive-integer>
          --start-processing-on-startup <true|false>
          --reconcile-snapshots-on-startup <true|false>
          --hydration-backfill-on-startup <true|false>
          --bundle <new-absolute-directory>
          --candidate-commit <40-hex> --candidate-tree <40-hex>
          [--retained-root <absolute-Planner-owned-directory>]...
          --confirm-owner-settings
        retained-state --help
        retained-state capture --help
        Settings are owner-attested, not discovered from service configuration.
        No host, initialization, recovery, activation, workers, or network.
        Exit codes: 0 verified/help; 2 arguments; 20 safety refusal; 1 error/mismatch/cancellation.
        Retain incomplete evidence. Never reuse a bundle. Rehearsal is not implemented here.
        """;

    internal static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "retained-state")
        {
            return null;
        }

        if (args is ["retained-state", "--help"] or ["retained-state", "capture", "--help"])
        {
            Console.Out.WriteLine(Help);
            return 0;
        }

        PlannerRetainedStateSettings settings;
        try
        {
            settings = Parse(args);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("retained-state arguments: invalid-arguments. Use retained-state --help.");
            return 2;
        }

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            await new PlannerRetainedStateCapture().CaptureAsync(settings, cancellation.Token, configureProcessTemp: true);
            Console.Out.WriteLine("retained-state capture: complete. Live activation unverified.");
            return 0;
        }
        catch (PlannerRetainedStateException ex)
        {
            Console.Error.WriteLine($"retained-state {ex.Stage}: {ex.Category}.");
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("retained-state capture: cancelled; evidence incomplete.");
            return 1;
        }
        catch (Exception)
        {
            // Payloads, connection strings and ambient configuration never go to the console.
            Console.Error.WriteLine("retained-state capture: unexpected-error; evidence incomplete.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    internal static PlannerRetainedStateSettings Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "retained-state" || args[1] != "capture")
        {
            throw new ArgumentException("Expected the capture subcommand.");
        }
        string[] required =
        [
            "--database", "--pre-cutover-backup", "--snapshots", "--activate-run-backed",
            "--snapshot-schema-version", "--start-processing-on-startup",
            "--reconcile-snapshots-on-startup", "--hydration-backfill-on-startup",
            "--bundle", "--candidate-commit", "--candidate-tree",
        ];
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        List<string> retainedRoots = [];
        bool confirmed = false;
        for (int index = 2; index < args.Length; index++)
        {
            string name = args[index];
            if (name == "--confirm-owner-settings")
            {
                if (confirmed)
                {
                    throw new ArgumentException("Duplicate attestation.");
                }
                confirmed = true;
                continue;
            }
            if ((!required.Contains(name, StringComparer.Ordinal) && name != "--retained-root") ||
                ++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Unknown option or missing value.");
            }
            string value = args[index];
            if (name == "--retained-root")
            {
                if (string.IsNullOrWhiteSpace(value) || retainedRoots.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Invalid retained root.");
                }
                retainedRoots.Add(value);
            }
            else if (!values.TryAdd(name, value))
            {
                throw new ArgumentException("Duplicate option.");
            }
        }
        if (!confirmed || required.Any(name => !values.ContainsKey(name)) ||
            values.Any(pair => pair.Key != "--pre-cutover-backup" && string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw new ArgumentException("Every setting and the owner attestation are required.");
        }
        bool activate = Boolean("--activate-run-backed");
        if ((activate && values["--pre-cutover-backup"].Length == 0) ||
            !int.TryParse(values["--snapshot-schema-version"], NumberStyles.None, CultureInfo.InvariantCulture,
                out int schemaVersion) || schemaVersion < 1)
        {
            throw new ArgumentException("Invalid activation or schema version.");
        }
        string commit = values["--candidate-commit"];
        string tree = values["--candidate-tree"];
        if (!IsObjectId(commit) || !IsObjectId(tree))
        {
            throw new ArgumentException("Full candidate commit and tree identities are required.");
        }
        return new(
            values["--database"], values["--pre-cutover-backup"], values["--snapshots"],
            activate, schemaVersion, Boolean("--start-processing-on-startup"),
            Boolean("--reconcile-snapshots-on-startup"), Boolean("--hydration-backfill-on-startup"),
            values["--bundle"], commit.ToLowerInvariant(), tree.ToLowerInvariant(), retainedRoots);

        bool Boolean(string name) => values[name] switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ArgumentException("Booleans must be exactly true or false."),
        };
    }

    internal static bool IsObjectId(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
}
