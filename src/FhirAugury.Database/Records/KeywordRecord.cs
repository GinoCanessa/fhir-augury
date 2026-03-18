using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Database.Records;

/// <summary>Per-document keyword with term frequency and BM25 score.</summary>
[LdgSQLiteTable("index_keywords")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(Keyword))]
[LdgSQLiteIndex(nameof(Keyword), nameof(KeywordType))]
public partial record class KeywordRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>The source type (e.g., "jira", "zulip").</summary>
    public required string SourceType { get; set; }

    /// <summary>The source-specific identifier.</summary>
    public required string SourceId { get; set; }

    /// <summary>The keyword (lowercased token).</summary>
    public required string Keyword { get; set; }

    /// <summary>Term frequency count in this document.</summary>
    public required int Count { get; set; }

    /// <summary>Token classification: word, fhir_path, fhir_operation.</summary>
    public required string KeywordType { get; set; }

    /// <summary>Computed BM25 score for this keyword in this document.</summary>
    public required double Bm25Score { get; set; }
}
