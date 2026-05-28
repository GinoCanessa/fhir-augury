using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Server.Terminology.Database.Records;

/// <summary>
/// One row per indexed CodeSystem or ValueSet. Holds the searchable
/// metadata plus a gzipped copy of the original JSON so the
/// <c>/check</c> endpoint can lift a small set of sample concepts for
/// the response payload without re-fetching the package.
/// </summary>
[LdgSQLiteTable("terminology_artifacts")]
[LdgSQLiteIndex(nameof(CanonicalUrlNormalized))]
[LdgSQLiteIndex(nameof(Kind))]
[LdgSQLiteIndex(nameof(FhirVersion))]
[LdgSQLiteIndex(nameof(PackageId))]
public partial record class TerminologyArtifactRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary><c>"CodeSystem"</c> or <c>"ValueSet"</c>.</summary>
    public required string Kind { get; set; }

    public required string CanonicalUrl { get; set; }

    /// <summary>Lowercased + trailing-slash-stripped form of <see cref="CanonicalUrl"/>.</summary>
    public required string CanonicalUrlNormalized { get; set; }

    public required string? Version { get; set; }

    public required string FhirVersion { get; set; }

    public required string? Title { get; set; }

    public required string? Name { get; set; }

    public required string? Status { get; set; }

    public required bool Experimental { get; set; }

    public required string? Publisher { get; set; }

    public required string? Description { get; set; }

    public required string? Purpose { get; set; }

    /// <summary>Comma-joined `useContext`/`jurisdiction` / `keyword` tokens.</summary>
    public required string? Keywords { get; set; }

    public required string PackageId { get; set; }

    public required string PackageVersion { get; set; }

    /// <summary>
    /// Original FHIR JSON of the resource, stored as text.
    /// </summary>
    /// <remarks>
    /// The plan originally specified a gzipped BLOB column, but
    /// CsLightDbGen 2026.416.1848 does not emit a working
    /// <c>byte[]</c> read path against the Microsoft.Data.Sqlite
    /// <c>SqliteDataReader</c> (it calls <c>GetBytes(ordinal)</c>
    /// without the required <c>fieldOffset</c> argument). Switching
    /// to <c>string</c> keeps the generator path clean and trades
    /// a small amount of disk for far less custom code; the
    /// terminology corpus is bounded (~tens of MB per package).
    /// </remarks>
    public required string Json { get; set; }
}
