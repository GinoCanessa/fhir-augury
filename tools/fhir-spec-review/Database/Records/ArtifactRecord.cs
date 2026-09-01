using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>
/// Inventory metadata for a reviewed FHIR artifact (resource / interface /
/// profile). Content checks live on the child <see cref="SpecPageRecord"/>
/// intro/notes rows, not here. Faithful port of the legacy
/// <c>ArtifactRecord</c>, minus the FMG feedback-sheet / disposition fields
/// (dropped for v1) and with baseline-site presence tracking added.
/// </summary>
[LdgSQLiteTable("artifacts")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(FhirId))]
[LdgSQLiteIndex(nameof(Name))]
[LdgSQLiteIndex(nameof(ResponsibleWorkGroupCode))]
public partial record class ArtifactRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }

    public required string FhirId { get; set; }

    public required string Name { get; set; }

    public string? ArtifactType { get; set; } = null;

    public bool? SourceDirectoryExists { get; set; } = null;
    public bool? SourceDefinitionExists { get; set; } = null;

    public string? IntroPageFilename { get; set; } = null;
    public string? NotesPageFilename { get; set; } = null;

    public bool? ExistsInBaselineSite { get; set; } = null;

    public string? ResponsibleWorkGroupCode { get; set; } = null;
    public string? ResponsibleWorkGroupName { get; set; } = null;

    public string? Status { get; set; } = null;
    public int? MaturityLevel { get; set; } = null;
    public string? StandardsStatus { get; set; } = null;
}
