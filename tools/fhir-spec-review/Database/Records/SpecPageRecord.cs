using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>
/// A reviewed spec page. Either a narrative page (<see cref="ArtifactId"/> is
/// null) enumerated from <c>publish.ini</c>, or an artifact intro/notes page
/// linked to an <see cref="ArtifactRecord"/>. Holds the per-page content-check
/// results. Faithful port of the legacy <c>SpecPageRecord</c>, minus the FMG
/// feedback-sheet / disposition fields (dropped for v1) and with baseline-site
/// presence tracking added.
/// </summary>
[LdgSQLiteTable("pages")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(PageFileName))]
[LdgSQLiteIndex(nameof(ArtifactId))]
[LdgSQLiteIndex(nameof(ResponsibleWorkGroupCode))]
public partial record class SpecPageRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }

    /// <summary>Parent artifact id, or null for narrative pages.</summary>
    public required int? ArtifactId { get; set; }

    public required string? FhirArtifactId { get; set; }

    public required string PageFileName { get; set; }

    public required bool? ExistsInPublishIni { get; set; }
    public required bool ExistsInSource { get; set; }

    /// <summary>Whether the corresponding page exists in the published baseline site.</summary>
    public bool? ExistsInBaselineSite { get; set; } = null;

    public string? ResponsibleWorkGroupCode { get; set; } = null;
    public string? ResponsibleWorkGroupName { get; set; } = null;

    public string? MaturityLabel { get; set; } = null;
    public int? MaturityLevel { get; set; } = null;
    public string? StandardsStatus { get; set; } = null;

    public int? ConformantShallCount { get; set; } = null;
    public int? ConformantShallNotCount { get; set; } = null;
    public int? ConformantShouldCount { get; set; } = null;
    public int? ConformantShouldNotCount { get; set; } = null;
    public int? ConformantMayCount { get; set; } = null;
    public int? ConformantMayNotCount { get; set; } = null;
    public int? ConformantTotalCount { get; set; } = null;

    public int? NonConformantShallCount { get; set; } = null;
    public int? NonConformantShallNotCount { get; set; } = null;
    public int? NonConformantShouldCount { get; set; } = null;
    public int? NonConformantShouldNotCount { get; set; } = null;
    public int? NonConformantMayCount { get; set; } = null;
    public int? NonConformantMayNotCount { get; set; } = null;
    public int? NonConformantTotalCount { get; set; } = null;

    public int? RemovedFhirArtifactCount { get; set; } = null;

    public int? UnknownWordCount { get; set; } = null;
    public int? TypoWordCount { get; set; } = null;

    public int? PriorFhirVersionReferenceCount { get; set; } = null;
    public int? DeprecatedLiteralCount { get; set; } = null;

    public int? ImagesWithIssuesCount { get; set; } = null;

    public int? StuLiteralsCount { get; set; } = null;

    public int? ZulipLinkCount { get; set; } = null;
    public int? ConfluenceLinkCount { get; set; } = null;

    /// <summary>JSON array (TEXT) of "possible incomplete" marker strings.</summary>
    public string? PossibleIncompleteMarkers { get; set; } = null;

    /// <summary>JSON array (TEXT) of reader-review note strings.</summary>
    public string? ReaderReviewNotes { get; set; } = null;
}
