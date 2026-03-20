using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Orchestrator.Database.Records;

[LdgSQLiteTable("xref_links")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(TargetType), nameof(TargetId))]
public partial record class CrossRefLinkRecord
{
    [LdgSQLiteKey] public required int Id { get; set; }
    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string LinkType { get; set; }
    public required string? Context { get; set; }
    public required DateTimeOffset DiscoveredAt { get; set; }
}
