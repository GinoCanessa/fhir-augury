using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Server.Terminology.Database.Records;

/// <summary>
/// One embedding vector per artifact. Empty until Phase 5 plugs in a
/// non-null <c>IEmbeddingProvider</c>.
/// </summary>
[LdgSQLiteTable("terminology_artifact_embeddings")]
[LdgSQLiteIndex(nameof(Model))]
public partial record class TerminologyArtifactEmbeddingRecord
{
    [LdgSQLiteKey]
    public required int ArtifactId { get; set; }

    public required string Model { get; set; }

    /// <summary>
    /// Vector serialized as little-endian <c>float32</c> bytes, then
    /// base64-encoded for SQLite storage. Decoded by the embedding
    /// matcher (Phase 5).
    /// </summary>
    /// <remarks>
    /// Stored as <c>string</c> rather than <c>byte[]</c> because
    /// CsLightDbGen 2026.416.1848 does not emit a working
    /// <c>byte[]</c> read path — see the matching note on
    /// <c>TerminologyArtifactRecord.Json</c>.
    /// </remarks>
    public required string Vector { get; set; }
}
