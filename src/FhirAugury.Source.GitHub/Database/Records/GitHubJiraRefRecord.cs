using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>A Jira issue reference found in GitHub content.</summary>
[LdgSQLiteTable("github_jira_refs")]
[LdgSQLiteIndex(nameof(JiraKey))]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(RepoFullName))]
public partial record class GitHubJiraRefRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public required string RepoFullName { get; set; }
    public required string JiraKey { get; set; }
    public required string? Context { get; set; }
}
