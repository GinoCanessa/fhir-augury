using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Maps a FHIR artifact to a file path in a repository.</summary>
[LdgSQLiteTable("github_spec_file_map")]
[LdgSQLiteIndex(nameof(RepoFullName))]
[LdgSQLiteIndex(nameof(ArtifactKey))]
[LdgSQLiteIndex(nameof(FilePath))]
public partial record class GitHubSpecFileMapRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }
    public required string ArtifactKey { get; set; }
    public required string FilePath { get; set; }

    /// <summary>Mapping type: directory, file, page, or element.</summary>
    public required string MapType { get; set; }
}
