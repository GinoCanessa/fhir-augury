using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Common.Database;

namespace FhirAugury.Common.Database.Records;

/// <summary>A Jira ticket reference extracted from text in any source.</summary>
[LdgSQLiteTable("xref_jira")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(JiraKey))]
[LdgSQLiteIndex(nameof(JiraKey), nameof(SourceType))]
public partial record class JiraXRefRecord : ICrossReferenceRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }
    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public required string LinkType { get; set; }
    public required string? Context { get; set; }
    public required string JiraKey { get; set; }

    [LdgSQLiteIgnore] public string TargetType => "jira";
    [LdgSQLiteIgnore] public string TargetId => JiraKey;
}
