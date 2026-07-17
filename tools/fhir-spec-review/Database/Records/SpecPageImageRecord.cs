using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>An image on a reviewed page flagged for a missing alt or for not being inside a figure.</summary>
[LdgSQLiteTable("page_images")]
[LdgSQLiteIndex(nameof(PageId))]
public partial record class SpecPageImageRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required int PageId { get; set; }

    public required string Source { get; set; }
    public required bool MissingAlt { get; set; }
    public required bool NotInFigure { get; set; }

    /// <summary>Short single-line snippet of the image element's surrounding markup.</summary>
    public string? ContextSnippet { get; set; } = null;
}
