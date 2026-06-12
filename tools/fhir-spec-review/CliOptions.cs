namespace FhirAugury.Tools.FhirSpecReview;

/// <summary>Parsed options for the <c>process</c> verb.</summary>
internal sealed record ProcessOptions(
    string GitHubDbPath,
    string GitHubCachePath,
    string Repo,
    string FhirSpecDbPath,
    string BaselineRelease,
    string? BaselineSitePath,
    string DictionaryDbPath,
    string ReviewDbPath,
    bool DropTables);

/// <summary>Parsed options for the <c>report</c> verb.</summary>
internal sealed record ReportOptions(
    string ReviewDbPath,
    string OutPath,
    bool Force);

internal static class CliOptions
{
    public const string DefaultGitHubDb = "./data/github.db";
    public const string DefaultGitHubCache = "./cache";
    public const string DefaultRepo = "HL7/fhir";
    public const string DefaultFhirSpecDb = "./cache/fhir-spec.db";
    public const string DefaultBaselineRelease = "R5";
    public const string DefaultDictionaryDb = "./cache/dictionary.db";
    public const string DefaultReviewDb = "./cache/fhir-spec-review.db";
    public const string DefaultOut = "./cache/fhir-spec-review-site";

    /// <summary>Parses <c>process</c> verb arguments (everything after the verb token).</summary>
    public static bool TryParseProcess(string[] args, out ProcessOptions options, out string? error)
    {
        string githubDb = DefaultGitHubDb;
        string githubCache = DefaultGitHubCache;
        string repo = DefaultRepo;
        string fhirSpecDb = DefaultFhirSpecDb;
        string baselineRelease = DefaultBaselineRelease;
        string? baselineSite = null;
        string dictionaryDb = DefaultDictionaryDb;
        string reviewDb = DefaultReviewDb;
        bool dropTables = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--github-db":
                    if (!TryTakeValue(args, ref i, arg, out githubDb, out error)) { options = DefaultProcess(); return false; }
                    break;
                case "--github-cache":
                    if (!TryTakeValue(args, ref i, arg, out githubCache, out error)) { options = DefaultProcess(); return false; }
                    break;
                case "--repo":
                    if (!TryTakeValue(args, ref i, arg, out repo, out error)) { options = DefaultProcess(); return false; }
                    break;
                case "--fhir-spec-db":
                    if (!TryTakeValue(args, ref i, arg, out fhirSpecDb, out error)) { options = DefaultProcess(); return false; }
                    break;
                case "--baseline-release":
                    if (!TryTakeValue(args, ref i, arg, out baselineRelease, out error)) { options = DefaultProcess(); return false; }
                    break;
                case "--baseline-site":
                    if (!TryTakeValue(args, ref i, arg, out string siteValue, out error)) { options = DefaultProcess(); return false; }
                    baselineSite = siteValue;
                    break;
                case "--dictionary-db":
                    if (!TryTakeValue(args, ref i, arg, out dictionaryDb, out error)) { options = DefaultProcess(); return false; }
                    break;
                case "--review-db":
                    if (!TryTakeValue(args, ref i, arg, out reviewDb, out error)) { options = DefaultProcess(); return false; }
                    break;
                case "--drop-tables":
                    dropTables = true;
                    break;
                default:
                    options = DefaultProcess();
                    error = $"Unknown option for 'process': {arg}";
                    return false;
            }
        }

        options = new ProcessOptions(
            githubDb, githubCache, repo, fhirSpecDb, baselineRelease,
            baselineSite, dictionaryDb, reviewDb, dropTables);
        error = null;
        return true;
    }

    /// <summary>Parses <c>report</c> verb arguments (everything after the verb token).</summary>
    public static bool TryParseReport(string[] args, out ReportOptions options, out string? error)
    {
        string reviewDb = DefaultReviewDb;
        string outPath = DefaultOut;
        bool force = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--review-db":
                    if (!TryTakeValue(args, ref i, arg, out reviewDb, out error)) { options = DefaultReport(); return false; }
                    break;
                case "--out":
                    if (!TryTakeValue(args, ref i, arg, out outPath, out error)) { options = DefaultReport(); return false; }
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    options = DefaultReport();
                    error = $"Unknown option for 'report': {arg}";
                    return false;
            }
        }

        options = new ReportOptions(reviewDb, outPath, force);
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

    private static ProcessOptions DefaultProcess() => new(
        DefaultGitHubDb, DefaultGitHubCache, DefaultRepo, DefaultFhirSpecDb,
        DefaultBaselineRelease, null, DefaultDictionaryDb, DefaultReviewDb, false);

    private static ReportOptions DefaultReport() => new(DefaultReviewDb, DefaultOut, false);
}
