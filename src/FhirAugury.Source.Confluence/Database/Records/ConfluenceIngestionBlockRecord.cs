using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Confluence.Database.Records;

/// <summary>
/// The durable record of an ingestion block, so a service restart re-learns that
/// a human still has to clear an edge challenge.
/// </summary>
/// <remarks>
/// One row, keyed by <see cref="Scope"/>. It is deliberately <b>not</b> dropped
/// by <c>ConfluenceDatabase.ResetDatabase()</c>: a cache rebuild is a local
/// operation and must not silently discard an operator-visible block.
/// </remarks>
[LdgSQLiteTable("confluence_ingestion_block")]
public partial record class ConfluenceIngestionBlockRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>Always <c>ingestion</c> today; the unique key for the single row.</summary>
    [LdgSQLiteUnique]
    public required string Scope { get; set; }

    public required bool Blocked { get; set; }
    public required DateTimeOffset BlockedAt { get; set; }
    public required string Reason { get; set; }
    public required int HttpStatus { get; set; }
    public required string? ReasonPhrase { get; set; }

    /// <summary>The compact fingerprint that identified the challenge.</summary>
    public required string? Fingerprint { get; set; }

    public required string? RequestUrl { get; set; }
    public required DateTimeOffset? ClearedAt { get; set; }
    public required string? ClearedBy { get; set; }
}
