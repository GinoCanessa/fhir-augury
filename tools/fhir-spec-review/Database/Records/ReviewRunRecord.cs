using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>Provenance for a single review run, surfaced in the report header.</summary>
[LdgSQLiteTable("review_runs")]
public partial record class ReviewRunRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }

    public required string BuildVersion { get; set; }

    public required string BaselineRelease { get; set; }

    /// <summary>ISO-8601 UTC timestamp of when the run was recorded.</summary>
    public required string RunAt { get; set; }
}
