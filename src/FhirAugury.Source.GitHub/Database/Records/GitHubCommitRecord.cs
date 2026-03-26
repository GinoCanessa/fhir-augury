using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>A Git commit from a tracked repository.</summary>
[LdgSQLiteTable("github_commits")]
[LdgSQLiteIndex(nameof(RepoFullName))]
[LdgSQLiteIndex(nameof(Author))]
[LdgSQLiteIndex(nameof(Date))]
public partial record class GitHubCommitRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Sha { get; set; }

    public required string RepoFullName { get; set; }
    public required string Message { get; set; }
    public string? Body { get; set; }
    public required string Author { get; set; }
    public string? AuthorEmail { get; set; }
    public string? CommitterName { get; set; }
    public string? CommitterEmail { get; set; }
    public required DateTimeOffset Date { get; set; }
    public required string Url { get; set; }
    public int FilesChanged { get; set; }
    public int Insertions { get; set; }
    public int Deletions { get; set; }
    public string? Refs { get; set; }
}
