using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Maps a FHIR artifact to a file path in a repository.</summary>
[LdgSQLiteTable("github_spec_file_map")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(ArtifactKey))]
[LdgSQLiteIndex(nameof(FilePath))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(WorkGroup))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(WorkGroupRaw))]
public partial record class GitHubSpecFileMapRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }
    public required string ArtifactKey { get; set; }
    public required string FilePath { get; set; }

    /// <summary>Mapping type: directory, file, page, or element.</summary>
    public required string MapType { get; set; }

    /// <summary>
    /// Canonical HL7 work-group <c>code</c> attributed to this file/page,
    /// resolved by <c>WorkGroupResolutionPass</c>. <c>null</c> when no match
    /// could be derived along the source-of-truth chain.
    /// </summary>
    public string? WorkGroup { get; set; }

    /// <summary>
    /// Original (pre-resolution) work-group input preserved for review when
    /// it didn't resolve, or when it resolved to a code that differed from
    /// the input. Surfaced via <c>/workgroups/unresolved</c>.
    /// </summary>
    public string? WorkGroupRaw { get; set; }
}
