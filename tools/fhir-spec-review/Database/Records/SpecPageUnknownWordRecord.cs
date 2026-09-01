using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>A word on a reviewed page that failed the spell-check (unknown word or a known typo).</summary>
[LdgSQLiteTable("page_unknown_words")]
[LdgSQLiteIndex(nameof(PageId))]
public partial record class SpecPageUnknownWordRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required int PageId { get; set; }

    public required string Word { get; set; }
    public required bool IsTypo { get; set; }

    /// <summary>Suggested correction when <see cref="IsTypo"/> is true; otherwise null.</summary>
    public string? Correction { get; set; } = null;

    /// <summary>Short single-line snippet of surrounding visible text around the match.</summary>
    public string? ContextSnippet { get; set; } = null;
}
