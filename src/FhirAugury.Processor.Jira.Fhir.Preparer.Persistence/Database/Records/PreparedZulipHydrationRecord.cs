using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_zulip_hydration")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(ZulipThreadId))]
[LdgSQLiteIndex(nameof(HydrationStatus))]
public partial record class PreparedZulipHydrationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string TicketKey { get; set; }
    public required string ZulipThreadId { get; set; }
    public int? StreamId { get; set; }
    public string? StreamName { get; set; }
    public string? Topic { get; set; }
    public int? MessageCount { get; set; }
    public DateTimeOffset? FirstMessageAt { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public string? FirstMessageExcerpt { get; set; }
    public string? Url { get; set; }
    public required DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
}
