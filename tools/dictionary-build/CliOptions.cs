namespace FhirAugury.Tools.DictionaryBuild;

/// <summary>Parsed options for a dictionary rebuild.</summary>
internal sealed record BuildOptions(string SourcePath, string OutPath);

internal static class CliOptions
{
    public const string DefaultSource = "./dictionary";
    public const string DefaultOut = "./cache/dictionary.db";

    /// <summary>
    /// Parses the tool's arguments. Supports <c>--source &lt;dir&gt;</c>,
    /// <c>--out &lt;path&gt;</c>, and <c>--force</c> (a documented no-op alias —
    /// the tool always performs a full rebuild). Unknown flags return an error.
    /// </summary>
    public static bool TryParse(string[] args, out BuildOptions options, out string? error)
    {
        string source = DefaultSource;
        string outPath = DefaultOut;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--source":
                    if (!TryTakeValue(args, ref i, arg, out source, out error)) { options = Default(); return false; }
                    break;
                case "--out":
                    if (!TryTakeValue(args, ref i, arg, out outPath, out error)) { options = Default(); return false; }
                    break;
                case "--force":
                    // No-op: the tool always performs a full rebuild. Accepted so
                    // the documented invocation (... --out <db> [--force]) works.
                    break;
                default:
                    options = Default();
                    error = $"Unknown option: {arg}";
                    return false;
            }
        }

        options = new BuildOptions(source, outPath);
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

    private static BuildOptions Default() => new(DefaultSource, DefaultOut);
}
