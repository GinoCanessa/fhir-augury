using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>A GitHub repository being tracked.</summary>
[LdgSQLiteTable("github_repos")]
[LdgSQLiteIndex(nameof(Owner))]
public partial record class GitHubRepoRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string FullName { get; set; }

    public required string Owner { get; set; }
    public required string Name { get; set; }
    public required string? Description { get; set; }
    public required bool HasIssues { get; set; }
    public required DateTimeOffset LastFetchedAt { get; set; }

    /// <summary>Repository category (e.g., "FhirCore", "Utg"). Stored as the enum string name.</summary>
    public required string Category { get; set; }
}
