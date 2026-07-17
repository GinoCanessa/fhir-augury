using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Indexed StructureDefinition metadata extracted from a repository clone.</summary>
[LdgSQLiteTable("github_structure_definitions")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(Url))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(Name))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(ArtifactClass))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(FhirType))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(FilePath))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(WorkGroupRaw))]
public partial record class GitHubStructureDefinitionRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }

    /// <summary>Relative to clone root, forward slashes.</summary>
    public required string FilePath { get; set; }

    /// <summary>Canonical URL of the StructureDefinition.</summary>
    public required string Url { get; set; }

    /// <summary>Computer-friendly name (e.g., "Patient").</summary>
    public required string Name { get; set; }

    /// <summary>Human-friendly title.</summary>
    public string? Title { get; set; }

    /// <summary>Publication status: draft, active, retired, unknown.</summary>
    public string? Status { get; set; }

    /// <summary>Classified artifact type: Resource, Profile, Extension, ComplexType, PrimitiveType, LogicalModel.</summary>
    public required string ArtifactClass { get; set; }

    /// <summary>StructureDefinition.kind: resource, complex-type, primitive-type, logical.</summary>
    public required string Kind { get; set; }

    /// <summary>Whether the definition is abstract (1 = true, 0 = false, null = unknown).</summary>
    public int? IsAbstract { get; set; }

    /// <summary>The FHIR type this definition constrains/defines.</summary>
    public string? FhirType { get; set; }

    /// <summary>Canonical URL of the base definition.</summary>
    public string? BaseDefinition { get; set; }

    /// <summary>Derivation mode: specialization or constraint.</summary>
    public string? Derivation { get; set; }

    /// <summary>FHIR version this definition targets.</summary>
    public string? FhirVersion { get; set; }

    /// <summary>Human-readable description / purpose.</summary>
    public string? Description { get; set; }

    /// <summary>Organization that published this definition.</summary>
    public string? Publisher { get; set; }

    /// <summary>HL7 work group (from structuredefinition-wg extension).</summary>
    public string? WorkGroup { get; set; }

    /// <summary>
    /// Original (pre-resolution) work-group input preserved by
    /// <c>WorkGroupResolutionPass</c> when the parsed value didn't resolve
    /// to a canonical HL7 code or resolved to a different code. Null when
    /// <c>WorkGroup</c> already matches the canonical code.
    /// </summary>
    public string? WorkGroupRaw { get; set; }

    /// <summary>FHIR Maturity Model level (from structuredefinition-fmm extension).</summary>
    public int? FhirMaturity { get; set; }

    /// <summary>Standards status: trial-use, normative, informative, draft, deprecated.</summary>
    public string? StandardsStatus { get; set; }

    /// <summary>Category (from structuredefinition-category extension).</summary>
    public string? Category { get; set; }

    /// <summary>Serialized extension contexts (JSON array), null if not an extension.</summary>
    public string? Contexts { get; set; }
}
