using System.Globalization;

namespace FhirAugury.Tools.TicketMdToDb;

public sealed record CliOptions(
    string InputRoot,
    string DatabasePath,
    Uri Orchestrator,
    int ExpectedCount,
    string? OverridesPath,
    string AuditPath,
    string OverrideTemplatePath,
    bool DryRun,
    bool ReplaceExisting,
    bool AcceptUnresolvedHydration)
{
    public const string Usage =
        "ticket-md-to-db --input <root> --db <path> --orchestrator <url> --expected-count <n> " +
        "[--overrides <json>] [--audit <json>] [--override-template <json>] [--dry-run] " +
        "[--replace-existing] [--accept-unresolved-hydration]";

    public static bool TryParse(
        IReadOnlyList<string> args,
        out CliOptions? options,
        out string? error)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        HashSet<string> switches = new(StringComparer.Ordinal);
        HashSet<string> valueOptions = new(StringComparer.Ordinal)
        {
            "--input",
            "--db",
            "--orchestrator",
            "--expected-count",
            "--overrides",
            "--audit",
            "--override-template",
        };
        HashSet<string> switchOptions = new(StringComparer.Ordinal)
        {
            "--dry-run",
            "--replace-existing",
            "--accept-unresolved-hydration",
        };

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (valueOptions.Contains(argument))
            {
                if (values.ContainsKey(argument))
                {
                    options = null;
                    error = $"Option {argument} was supplied more than once.";
                    return false;
                }
                if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    options = null;
                    error = $"Option {argument} requires a value.";
                    return false;
                }

                values[argument] = args[index];
                continue;
            }
            if (switchOptions.Contains(argument))
            {
                if (!switches.Add(argument))
                {
                    options = null;
                    error = $"Switch {argument} was supplied more than once.";
                    return false;
                }
                continue;
            }

            options = null;
            error = $"Unknown option: {argument}.";
            return false;
        }

        foreach (string required in new[] { "--input", "--db", "--orchestrator", "--expected-count" })
        {
            if (!values.TryGetValue(required, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                options = null;
                error = $"Missing required option {required}.";
                return false;
            }
        }

        if (!int.TryParse(
                values["--expected-count"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int expectedCount)
            || expectedCount <= 0)
        {
            options = null;
            error = "--expected-count must be a positive integer.";
            return false;
        }
        if (!Uri.TryCreate(values["--orchestrator"], UriKind.Absolute, out Uri? orchestrator)
            || (orchestrator.Scheme != Uri.UriSchemeHttp && orchestrator.Scheme != Uri.UriSchemeHttps))
        {
            options = null;
            error = "--orchestrator must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        string databasePath = values["--db"];
        string auditPath = values.GetValueOrDefault("--audit")
            ?? $"{databasePath}.import-audit.json";
        string overrideTemplatePath = values.GetValueOrDefault("--override-template")
            ?? $"{databasePath}.override-template.json";
        options = new CliOptions(
            values["--input"],
            databasePath,
            orchestrator,
            expectedCount,
            values.GetValueOrDefault("--overrides"),
            auditPath,
            overrideTemplatePath,
            switches.Contains("--dry-run"),
            switches.Contains("--replace-existing"),
            switches.Contains("--accept-unresolved-hydration"));
        error = null;
        return true;
    }
}
