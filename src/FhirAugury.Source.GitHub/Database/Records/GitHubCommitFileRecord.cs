using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>A file changed by a specific commit.</summary>
[LdgSQLiteTable("github_commit_files")]
[LdgSQLiteIndex(nameof(CommitSha))]
[LdgSQLiteIndex(nameof(FilePath))]
public partial record class GitHubCommitFileRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string CommitSha { get; set; }
    public required string FilePath { get; set; }
    public required string ChangeType { get; set; }
}
