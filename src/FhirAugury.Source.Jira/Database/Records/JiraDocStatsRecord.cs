using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Common.Database;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>Per-source-type document statistics for BM25 normalization.</summary>
[LdgSQLiteTable("index_doc_stats")]
[LdgSQLiteIndex(nameof(ContentType))]
public partial record class JiraDocStatsRecord : IDocStatsRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string ContentType { get; set; }

    public required int TotalDocuments { get; set; }
    public required double AverageDocLength { get; set; }
}
