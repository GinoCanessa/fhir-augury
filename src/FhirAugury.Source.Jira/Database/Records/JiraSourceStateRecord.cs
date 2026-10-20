using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// Singleton generation fence for source reads. A mutation advances the
/// revision before and after its writes so readers can identify mixed or
/// in-progress generations without treating a refresh timestamp as a token.
/// </summary>
[LdgSQLiteTable("jira_source_state")]
public partial record class JiraSourceStateRecord
{
    public const int SingletonId = 1;

    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required long ContentRevision { get; set; }
    public required bool MutationInProgress { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
