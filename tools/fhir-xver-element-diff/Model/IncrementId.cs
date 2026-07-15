namespace FhirAugury.Tools.FhirXverElementDiff.Model;

/// <summary>The three pairwise release increments the tool reports on.</summary>
internal enum IncrementId
{
    R4ToR4B,
    R4BToR5,
    R5ToR6,
}

/// <summary>
/// An increment's earlier/later releases, its default git-window anchors on the
/// <c>HL7/fhir</c> clone (first-parent master line — see the plan's anchor table),
/// the report file slug, and an optional header note (used for the R5→R6 ballot4 gap).
/// </summary>
internal sealed record IncrementDefinition(
    IncrementId Id,
    ReleaseId Earlier,
    ReleaseId Later,
    string DefaultSince,
    string DefaultUntil,
    string Slug,
    string? HeaderNote);

internal static class Increments
{
    public static readonly IncrementDefinition R4ToR4B = new(
        IncrementId.R4ToR4B, ReleaseId.R4, ReleaseId.R4B,
        DefaultSince: "b6357157", DefaultUntil: "d685d85", Slug: "r4-r4b", HeaderNote: null);

    public static readonly IncrementDefinition R4BToR5 = new(
        IncrementId.R4BToR5, ReleaseId.R4B, ReleaseId.R5,
        DefaultSince: "959acd13", DefaultUntil: "eca054db", Slug: "r4b-r5", HeaderNote: null);

    public static readonly IncrementDefinition R5ToR6 = new(
        IncrementId.R5ToR6, ReleaseId.R5, ReleaseId.R6,
        DefaultSince: "eca054db", DefaultUntil: "94dbe68f", Slug: "r5-r6",
        HeaderNote: "The R6 `until` is the clone HEAD, which deliberately runs past the ballot4 DB "
            + "snapshot commit (~2026-06-24). Per-element attribution is facet-verified against the "
            + "DB value to reject post-snapshot over-writes; the R6 change tables reflect the frozen "
            + "6.0.0-ballot4 DB, not the moving `#current` build.");

    public static readonly IReadOnlyList<IncrementDefinition> All = [R4ToR4B, R4BToR5, R5ToR6];

    /// <summary>Resolves a selection token (<c>all</c> or a slug) to one or more increments.</summary>
    public static bool TryResolve(string selection, out IReadOnlyList<IncrementDefinition> increments, out string? error)
    {
        switch (selection.Trim().ToLowerInvariant())
        {
            case "all":
                increments = All;
                error = null;
                return true;
            case "r4-r4b":
                increments = [R4ToR4B];
                error = null;
                return true;
            case "r4b-r5":
                increments = [R4BToR5];
                error = null;
                return true;
            case "r5-r6":
                increments = [R5ToR6];
                error = null;
                return true;
            default:
                increments = [];
                error = $"Unknown --increment: {selection} (expected all, r4-r4b, r4b-r5, or r5-r6)";
                return false;
        }
    }
}
