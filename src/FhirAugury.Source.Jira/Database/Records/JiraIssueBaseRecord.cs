using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// Shared base columns for every Jira-issue-shaped table (FHIR change
/// requests, Project Scope Statements, Ballot Definitions, Ballot votes).
/// CsLightDbGen emits base+derived columns for each <see
/// cref="LdgSQLiteTableAttribute"/>-annotated record that inherits from
/// this class so each derived table gets its own
/// <c>CreateTable</c>/<c>SelectList</c>/<c>Insert</c> surface that
/// includes these columns.
/// </summary>
[LdgSQLiteBaseClass]
[LdgSQLiteIndex(nameof(ProjectKey), nameof(Key))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(Type))]
[LdgSQLiteIndex(nameof(Priority))]
[LdgSQLiteIndex(nameof(Assignee))]
[LdgSQLiteIndex(nameof(Reporter))]
[LdgSQLiteIndex(nameof(CreatedAt))]
[LdgSQLiteIndex(nameof(ProcessedLocallyAt))]
public partial record class JiraIssueBaseRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    [LdgSQLiteMultiSelect]
    public required string Key { get; set; }

    public required string ProjectKey { get; set; }
    public required string Title { get; set; }
    public required string? Description { get; set; }
    public string? DescriptionPlain { get; set; }
    public required string? Summary { get; set; }
    public required string Type { get; set; }
    public required string Priority { get; set; }
    public required string Status { get; set; }
    public required string? Assignee { get; set; }
    public required string? Reporter { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public required DateTimeOffset? ResolvedAt { get; set; }
    public string? Labels { get; set; }
    public int CommentCount { get; set; } = 0;

    /// <summary>
    /// Local-only processing timestamp. Not synchronized to Jira.
    /// null = not yet processed (or explicitly unmarked).
    /// non-null = the UTC time the row was marked processed.
    /// </summary>
    public DateTimeOffset? ProcessedLocallyAt { get; set; }
}
