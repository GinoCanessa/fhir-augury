using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>
/// Authoritative HL7 work group, sourced from the
/// <c>CodeSystem-hl7-work-group</c> FHIR resource. Populated by the GitHub
/// source's pipeline (<see cref="Ingestion.GitHubHl7WorkGroupIndexer"/>) from
/// the support XML acquired by
/// <see cref="Ingestion.GitHubWorkGroupSupportFileAcquirer"/>.
/// </summary>
/// <remarks>
/// Deliberately a copy of the Jira-side record (same table name, same
/// columns and indexes) so each source service owns its local copy of the
/// authoritative codeset and neither cross-database attaches nor an HTTP
/// hop is required at resolution time.
/// </remarks>
[LdgSQLiteTable("hl7_workgroups")]
[LdgSQLiteIndex(nameof(Code))]
[LdgSQLiteIndex(nameof(NameClean))]
public partial record class Hl7WorkGroupRecord
{
    /// <summary>
    /// Surrogate key. Aliased to <c>ROWID</c> so SQLite auto-increments it,
    /// and preserved across re-loads (existing rows are matched by
    /// <see cref="Code"/> and updated in place).
    /// </summary>
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Code { get; set; }

    public required string Name { get; set; }

    public required string? Definition { get; set; }

    public required bool Retired { get; set; }

    public required string NameClean { get; set; }
}
