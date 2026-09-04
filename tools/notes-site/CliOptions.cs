namespace FhirAugury.Tools.NotesSite;

/// <summary>Parsed options for the <c>report</c> verb.</summary>
internal sealed record ReportOptions(
    string SnapshotDbPath,
    string SnapshotDescriptorPath,
    string OutPath,
    string Title,
    bool Force);

internal static class CliOptions
{
    public const string DefaultOut = "./cache/notes-site";
    public const string DefaultTitle = "FHIR Ballot Notes";

    /// <summary>Parses <c>report</c> verb arguments (everything after the verb token).</summary>
    public static bool TryParseReport(string[] args, out ReportOptions options, out string? error)
    {
        string outPath = DefaultOut;
        string title = DefaultTitle;
        string? snapshotDb = null;
        string? snapshotDescriptor = null;
        bool force = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
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

        if (snapshotDb is null)
        {
            options = DefaultReport();
            error = "Missing required option --snapshot-db <path>.";
            return false;
        }
        if (snapshotDescriptor is null)
        {
            options = DefaultReport();
            error = "Missing required option --snapshot-descriptor <path>.";
            return false;
        }

        options = new ReportOptions(
            snapshotDb,
            snapshotDescriptor,
            outPath,
            title,
            force);
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
        new(string.Empty, string.Empty, DefaultOut, DefaultTitle, false);
}
