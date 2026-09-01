namespace FhirAugury.Tools.FhirXverElementDiff.Model;

/// <summary>The four core FHIR releases this tool diffs across.</summary>
internal enum ReleaseId
{
    R4,
    R4B,
    R5,
    R6,
}

/// <summary>
/// A release resolved against a concrete SQLite package row: which DB file holds
/// it, the DB-local surrogate <c>Packages.Key</c>, and the build metadata recorded
/// in each report header. Surrogate keys are resolved dynamically (never hard-coded)
/// — see <see cref="Readers.ReleaseReader"/>.
/// </summary>
internal sealed record ResolvedRelease(
    ReleaseId Id,
    string DbPath,
    int PackageKey,
    string PackageId,
    string DisplayVersion,
    string? ProcessDate);

/// <summary>Static helpers over <see cref="ReleaseId"/> (labels, aliases, parsing).</summary>
internal static class Release
{
    /// <summary>The human label used in report headings (<c>R4</c>/<c>R4B</c>/<c>R5</c>/<c>R6</c>).</summary>
    public static string DisplayLabel(ReleaseId id) => id switch
    {
        ReleaseId.R4 => "R4",
        ReleaseId.R4B => "R4B",
        ReleaseId.R5 => "R5",
        ReleaseId.R6 => "R6",
        _ => id.ToString(),
    };

    /// <summary>The <c>Packages.ShortName</c> token used to resolve the package row.</summary>
    public static string ShortName(ReleaseId id) => DisplayLabel(id);

    /// <summary>True for R6, which lives in <c>fhir-r6.db</c> rather than <c>fhir-spec.db</c>.</summary>
    public static bool IsR6(ReleaseId id) => id == ReleaseId.R6;

    /// <summary>Parses a release token (case-insensitive), accepting a few common aliases.</summary>
    public static bool TryParse(string token, out ReleaseId id)
    {
        switch (token.Trim().ToUpperInvariant())
        {
            case "R4":
            case "4.0":
                id = ReleaseId.R4;
                return true;
            case "R4B":
            case "4.3":
                id = ReleaseId.R4B;
                return true;
            case "R5":
            case "5.0":
                id = ReleaseId.R5;
                return true;
            case "R6":
            case "6.0":
                id = ReleaseId.R6;
                return true;
            default:
                id = ReleaseId.R5;
                return false;
        }
    }
}
