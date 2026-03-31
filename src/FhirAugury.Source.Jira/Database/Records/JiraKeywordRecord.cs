using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Common.Database;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>Per-document keyword with term frequency and BM25 score.</summary>
[LdgSQLiteTable("index_keywords")]
[LdgSQLiteIndex(nameof(ContentType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(Keyword), nameof(KeywordType))]
public partial record class JiraKeywordRecord : IKeywordRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string ContentType { get; set; }
    public required string SourceId { get; set; }
    public required string Keyword { get; set; }
    public required int Count { get; set; }
    public required string KeywordType { get; set; }
    public required double Bm25Score { get; set; }
}
