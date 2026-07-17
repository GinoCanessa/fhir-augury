namespace FhirAugury.Tools.BallotNotesReallocateWg;

/// <summary>Parsed options for the <c>reallocate</c> verb.</summary>
internal sealed record ReallocateOptions(
    string DbPath,
    string ClonePath,
    string? Repo,
    bool DryRun,
    string GitHubDbPath,
    string FhirR6DbPath,
    string FhirSpecDbPath,
    string WorkGroupHint,
    bool AllowStaleClone,
    bool AllowMixedHeads);

internal static class CliOptions
{
    public const string DefaultDb = "./cache/ballot-notes.db";
    public const string DefaultGitHubDb = "./cache/github.db";
    public const string DefaultFhirR6Db = "./cache/fhir-r6.db";
    public const string DefaultFhirSpecDb = "./cache/fhir-spec.db";

    /// <summary>Parses <c>reallocate</c> verb arguments (everything after the verb token).</summary>
    public static bool TryParseReallocate(string[] args, out ReallocateOptions options, out string? error)
    {
        string db = DefaultDb;
        string? clone = null;
        string? repo = null;
        bool dryRun = false;
        string githubDb = DefaultGitHubDb;
        string fhirR6Db = DefaultFhirR6Db;
        string fhirSpecDb = DefaultFhirSpecDb;
        string hint = string.Empty;
        bool allowStaleClone = false;
        bool allowMixedHeads = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--db":
                    if (!TryTakeValue(args, ref i, arg, out db, out error)) { options = Default(); return false; }
                    break;
                case "--clone":
                    if (!TryTakeValue(args, ref i, arg, out clone, out error)) { options = Default(); return false; }
                    break;
                case "--repo":
                    if (!TryTakeValue(args, ref i, arg, out repo, out error)) { options = Default(); return false; }
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--github-db":
                    if (!TryTakeValue(args, ref i, arg, out githubDb, out error)) { options = Default(); return false; }
                    break;
                case "--fhir-r6-db":
                    if (!TryTakeValue(args, ref i, arg, out fhirR6Db, out error)) { options = Default(); return false; }
                    break;
                case "--fhir-spec-db":
                    if (!TryTakeValue(args, ref i, arg, out fhirSpecDb, out error)) { options = Default(); return false; }
                    break;
                case "--work-group-hint":
                    if (!TryTakeValue(args, ref i, arg, out hint, out error)) { options = Default(); return false; }
                    break;
                case "--allow-stale-clone":
                    allowStaleClone = true;
                    break;
                case "--allow-mixed-heads":
                    allowMixedHeads = true;
                    break;
                default:
                    options = Default();
                    error = $"Unknown option for 'reallocate': {arg}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(clone))
        {
            options = Default();
            error = "Missing required option --clone <repo-clone-path>.";
            return false;
        }

        options = new ReallocateOptions(
            db, clone, repo, dryRun, githubDb, fhirR6Db, fhirSpecDb, hint, allowStaleClone, allowMixedHeads);
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

    private static ReallocateOptions Default() => new(
        DefaultDb, string.Empty, null, false, DefaultGitHubDb, DefaultFhirR6Db, DefaultFhirSpecDb,
        string.Empty, false, false);
}
