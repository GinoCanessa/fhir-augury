using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Database.Records;

/// <summary>A GitHub repository being tracked.</summary>
[LdgSQLiteTable("github_repos")]
public partial record class GitHubRepoRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string FullName { get; set; }

    public required string Owner { get; set; }
    public required string Name { get; set; }
    public required string? Description { get; set; }
    public required DateTimeOffset LastFetchedAt { get; set; }
}
