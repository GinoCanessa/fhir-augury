using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff;

/// <summary>Shared, parsed command-line options for the tool.</summary>
internal sealed record ToolOptions(
    string FhirSpecDbPath,
    string FhirR6DbPath,
    string JiraDbPath,
    string ClonePath,
    string OutDir,
    string Increment,
    bool NoAttribution,
    string? SinceOverride,
    string? UntilOverride,
    ReleaseId? DumpRelease)
{
    /// <summary>The DB file that holds a given release (R6 → <c>fhir-r6.db</c>; else <c>fhir-spec.db</c>).</summary>
    public string DbPathFor(ReleaseId id) => Release.IsR6(id) ? FhirR6DbPath : FhirSpecDbPath;
}

internal static class CliOptions
{
    public const string DefaultFhirSpecDb = "./cache/fhir-spec.db";
    public const string DefaultFhirR6Db = "./cache/fhir-r6.db";
    public const string DefaultJiraDb = "./cache/jira.db";
    public const string DefaultClone = "./cache/github/repos/HL7_fhir/clone";
    public const string DefaultOut = "./scratch/0714-03/reports";
    public const string DefaultIncrement = "all";

    /// <summary>Parses all tool arguments into a single <see cref="ToolOptions"/>.</summary>
    public static bool TryParse(string[] args, out ToolOptions options, out string? error)
    {
        string fhirSpecDb = DefaultFhirSpecDb;
        string fhirR6Db = DefaultFhirR6Db;
        string jiraDb = DefaultJiraDb;
        string clone = DefaultClone;
        string outDir = DefaultOut;
        string increment = DefaultIncrement;
        bool noAttribution = false;
        string? since = null;
        string? until = null;
        ReleaseId? dumpRelease = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--fhir-spec-db":
                    if (!TryTakeValue(args, ref i, arg, out fhirSpecDb, out error)) { options = Default(); return false; }
                    break;
                case "--fhir-r6-db":
                    if (!TryTakeValue(args, ref i, arg, out fhirR6Db, out error)) { options = Default(); return false; }
                    break;
                case "--jira-db":
                    if (!TryTakeValue(args, ref i, arg, out jiraDb, out error)) { options = Default(); return false; }
                    break;
                case "--clone":
                    if (!TryTakeValue(args, ref i, arg, out clone, out error)) { options = Default(); return false; }
                    break;
                case "--out":
                    if (!TryTakeValue(args, ref i, arg, out outDir, out error)) { options = Default(); return false; }
                    break;
                case "--increment":
                    if (!TryTakeValue(args, ref i, arg, out increment, out error)) { options = Default(); return false; }
                    break;
                case "--no-attribution":
                    noAttribution = true;
                    break;
                case "--since":
                    if (!TryTakeValue(args, ref i, arg, out string sinceValue, out error)) { options = Default(); return false; }
                    since = sinceValue;
                    break;
                case "--until":
                    if (!TryTakeValue(args, ref i, arg, out string untilValue, out error)) { options = Default(); return false; }
                    until = untilValue;
                    break;
                case "--dump":
                    if (!TryTakeValue(args, ref i, arg, out string dumpValue, out error)) { options = Default(); return false; }
                    if (!Release.TryParse(dumpValue, out ReleaseId parsed))
                    {
                        options = Default();
                        error = $"Unknown release for --dump: {dumpValue} (expected R4, R4B, R5, or R6)";
                        return false;
                    }
                    dumpRelease = parsed;
                    break;
                default:
                    options = Default();
                    error = $"Unknown option: {arg}";
                    return false;
            }
        }

        options = new ToolOptions(
            fhirSpecDb, fhirR6Db, jiraDb, clone, outDir, increment, noAttribution, since, until, dumpRelease);
        error = null;
        return true;
    }

    private static bool TryTakeValue(string[] args, ref int i, string flag, out string value, out string? error)
    {
        if (i + 1 >= args.Length)
        {
            value = string.Empty;
            error = $"Missing value for {flag}";
            return false;
        }
        value = args[++i];
        error = null;
        return true;
    }

    private static ToolOptions Default() => new(
        DefaultFhirSpecDb, DefaultFhirR6Db, DefaultJiraDb, DefaultClone,
        DefaultOut, DefaultIncrement, false, null, null, null);
}
