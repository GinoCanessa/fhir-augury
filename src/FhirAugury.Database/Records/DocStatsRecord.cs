using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Database.Records;

/// <summary>Per-source-type document statistics for BM25 normalization.</summary>
[LdgSQLiteTable("index_doc_stats")]
[LdgSQLiteIndex(nameof(SourceType))]
public partial record class DocStatsRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>The source type (e.g., "jira", "zulip").</summary>
    [LdgSQLiteUnique]
    public required string SourceType { get; set; }

    /// <summary>Total number of documents for this source type.</summary>
    public required int TotalDocuments { get; set; }

    /// <summary>Average document length (in tokens) for this source type.</summary>
    public required double AverageDocLength { get; set; }
}
