namespace FhirAugury.Tools.NotesSite;

/// <summary>Parsed options for the <c>report</c> verb.</summary>
internal sealed record ReportOptions(
    string DbPath,
    string OutPath,
    string Title,
    bool Force);

internal static class CliOptions
{
    public const string DefaultDb = "./cache/ballot-notes.db";
    public const string DefaultOut = "./cache/notes-site";
    public const string DefaultTitle = "FHIR Ballot Notes";

    /// <summary>Parses <c>report</c> verb arguments (everything after the verb token).</summary>
    public static bool TryParseReport(string[] args, out ReportOptions options, out string? error)
    {
        string db = DefaultDb;
        string outPath = DefaultOut;
        string title = DefaultTitle;
        bool force = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--db":
                    if (!TryTakeValue(args, ref i, arg, out db, out error)) { options = DefaultReport(); return false; }
                    break;
                case "--out":
                    if (!TryTakeValue(args, ref i, arg, out outPath, out error)) { options = DefaultReport(); return false; }
                    break;
                case "--title":
                    if (!TryTakeValue(args, ref i, arg, out title, out error)) { options = DefaultReport(); return false; }
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

        options = new ReportOptions(db, outPath, title, force);
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

    private static ReportOptions DefaultReport() => new(DefaultDb, DefaultOut, DefaultTitle, false);
}
