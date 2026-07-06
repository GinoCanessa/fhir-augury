using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>A file changed by a specific commit.</summary>
[LdgSQLiteTable("github_commit_files")]
[LdgSQLiteIndex(nameof(CommitSha), nameof(FilePath), nameof(BlobSha), nameof(ChangeType))]
[LdgSQLiteIndex(nameof(FilePath))]
public partial record class GitHubCommitFileRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string CommitSha { get; set; }
    public required string FilePath { get; set; }
    public required string ChangeType { get; set; }

    /// <summary>
    /// Post-image (new) blob SHA for this <c>(commit, file)</c>, captured from
    /// <c>git log --raw --no-abbrev</c> during ingestion. Null for deletions
    /// (all-zero sentinel) and for rows written by an older extractor before
    /// this column existed. Consumers must tolerate its absence.
    /// </summary>
    public string? BlobSha { get; set; }
}
