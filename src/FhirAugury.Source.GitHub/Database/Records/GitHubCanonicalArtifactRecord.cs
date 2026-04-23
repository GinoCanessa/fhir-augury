using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Canonical artifact metadata extracted from FHIR XML, JSON, or FSH files.</summary>
[LdgSQLiteTable("github_canonical_artifacts")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(ResourceType))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(Url))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(Name))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(FilePath), nameof(Url))]
[LdgSQLiteIndex(nameof(ResourceType))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(WorkGroupRaw))]
public partial record class GitHubCanonicalArtifactRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }
    public required string FilePath { get; set; }
    public required string ResourceType { get; set; }
    public required string Url { get; set; }
    public required string Name { get; set; }
    public string? Title { get; set; }
    public string? Version { get; set; }
    public string? Status { get; set; }
    public string? Description { get; set; }
    public string? Publisher { get; set; }
    public string? WorkGroup { get; set; }

    /// <summary>
    /// Original (pre-resolution) work-group input preserved by
    /// <c>WorkGroupResolutionPass</c> when the parsed value didn't resolve
    /// to a canonical HL7 code or resolved to a different code. Null when
    /// <c>WorkGroup</c> already matches the canonical code.
    /// </summary>
    public string? WorkGroupRaw { get; set; }
    public int? FhirMaturity { get; set; }
    public string? StandardsStatus { get; set; }
    public string? TypeSpecificData { get; set; }
    public required string Format { get; set; }
}
