using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_github_hydration")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(GitHubItemId))]
[LdgSQLiteIndex(nameof(State))]
[LdgSQLiteIndex(nameof(IsPullRequest))]
[LdgSQLiteIndex(nameof(HydrationStatus))]
public partial record class PreparedGitHubHydrationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string TicketKey { get; set; }
    public required string GitHubItemId { get; set; }
    public string? Owner { get; set; }
    public string? Repo { get; set; }
    public int? Number { get; set; }
    public string? Path { get; set; }
    public string? Title { get; set; }
    public string? State { get; set; }
    public bool? IsPullRequest { get; set; }
    public string? Labels { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Url { get; set; }
    public required DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
}
