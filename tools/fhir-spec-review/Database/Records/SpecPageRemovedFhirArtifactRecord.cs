using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>
/// A token on a reviewed page that matches a FHIR artifact present in the
/// published baseline vocabulary but absent from the current build — i.e. a
/// reference to a removed FHIR artifact.
/// </summary>
[LdgSQLiteTable("page_removed_fhir_artifacts")]
[LdgSQLiteIndex(nameof(PageId), nameof(Word))]
public partial record class SpecPageRemovedFhirArtifactRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required int PageId { get; set; }

    public required string Word { get; set; }

    /// <summary>Baseline artifact class (e.g. resource/element/searchparam), when known.</summary>
    public string? ArtifactClass { get; set; } = null;

    /// <summary>Short single-line snippet of surrounding visible text around the match.</summary>
    public string? ContextSnippet { get; set; } = null;
}
