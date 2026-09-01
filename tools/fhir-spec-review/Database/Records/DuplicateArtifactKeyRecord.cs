using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>
/// A collision on the artifacts' <c>(RepoFullName, FhirId)</c> key: two (or
/// more) distinct StructureDefinitions resolve to the same derived
/// <see cref="FhirId"/> (last URL segment), typically because they share a
/// canonical URL in the source build. The first artifact for a given
/// <see cref="FhirId"/> is retained; each subsequent one is skipped and recorded
/// here. Advisory (report-only), surfaced under the Unassigned bucket — it flags
/// a genuine source-data defect rather than silently dropping the duplicate.
/// </summary>
[LdgSQLiteTable("duplicate_artifact_keys")]
[LdgSQLiteIndex(nameof(FhirId))]
public partial record class DuplicateArtifactKeyRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }

    public required string FhirId { get; set; }

    /// <summary>The first artifact for this <see cref="FhirId"/>, retained.</summary>
    public required string KeptName { get; set; }

    /// <summary>The subsequent artifact for this <see cref="FhirId"/>, skipped.</summary>
    public required string DuplicateName { get; set; }

    public string? KeptCanonicalUrl { get; set; } = null;
    public string? DuplicateCanonicalUrl { get; set; } = null;

    public string? ArtifactType { get; set; } = null;
    public string? WorkGroupCode { get; set; } = null;
}
