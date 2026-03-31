using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Common.Database;

namespace FhirAugury.Common.Database.Records;

/// <summary>A GitHub issue/PR reference extracted from text in any source.</summary>
[LdgSQLiteTable("xref_github")]
[LdgSQLiteIndex(nameof(ContentType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(IssueNumber))]
[LdgSQLiteIndex(nameof(RepoFullName))]
public partial record class GitHubXRefRecord : ICrossReferenceRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }
    public required string ContentType { get; set; }
    public required string SourceId { get; set; }
    public required string LinkType { get; set; }
    public required string? Context { get; set; }
    public required string RepoFullName { get; set; }
    public required int IssueNumber { get; set; }

    [LdgSQLiteIgnore] public string TargetType => SourceSystems.GitHub;
    [LdgSQLiteIgnore] public string TargetId => $"{RepoFullName}#{IssueNumber}";
}
