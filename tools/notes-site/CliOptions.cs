namespace FhirAugury.Tools.NotesSite;

/// <summary>Parsed options for the <c>report</c> verb.</summary>
internal sealed record ReportOptions(
    string DbPath,
    string OutPath,
    string Title,
    bool Force,
    string? SnapshotDbPath = null,
    string? SnapshotDescriptorPath = null,
    bool DbSupplied = false)
{
    public bool SnapshotMode => SnapshotDbPath is not null;
}

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
        string? snapshotDb = null;
        string? snapshotDescriptor = null;
        bool dbSupplied = false;
        bool force = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--db":
                    if (!TryTakeValue(args, ref i, arg, out db, out error)) { options = DefaultReport(); return false; }
                    dbSupplied = true;
                    break;
                case "--snapshot-db":
                    if (!TryTakeValue(args, ref i, arg, out string snapshotValue, out error))
                    {
                        options = DefaultReport();
                        return false;
                    }
                    snapshotDb = snapshotValue;
                    break;
                case "--snapshot-descriptor":
                    if (!TryTakeValue(args, ref i, arg, out string descriptorValue, out error))
                    {
                        options = DefaultReport();
                        return false;
                    }
                    snapshotDescriptor = descriptorValue;
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

        if (snapshotDb is not null && dbSupplied)
        {
            options = DefaultReport();
            error = "--db and --snapshot-db are mutually exclusive.";
            return false;
        }
        if (snapshotDb is not null && snapshotDescriptor is null)
        {
            options = DefaultReport();
            error = "Snapshot mode requires --snapshot-descriptor <path>.";
            return false;
        }
        if (snapshotDb is null && snapshotDescriptor is not null)
        {
            options = DefaultReport();
            error = "--snapshot-descriptor is valid only with --snapshot-db.";
            return false;
        }

        options = new ReportOptions(
            db,
            outPath,
            title,
            force,
            snapshotDb,
            snapshotDescriptor,
            dbSupplied);
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

    private static ReportOptions DefaultReport() =>
        new(DefaultDb, DefaultOut, DefaultTitle, false);
}
