using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;

/// <summary>
/// Per-repo baseline state: the most recent commit SHA the baseline was built from and
/// when it was built. Used by <c>BaselineSyncService</c> to skip rebuilds that would
/// fall inside the configured <c>BaselineMinSyncAge</c>.
/// </summary>
[LdgSQLiteTable("repo_baselines")]
[LdgSQLiteIndex(nameof(LastBuiltAt))]
public partial record class RepoBaselineRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string RepoKey { get; set; }

    public required string BaselineCommitSha { get; set; }
    public DateTimeOffset LastBuiltAt { get; set; } = DateTimeOffset.UtcNow;
}
