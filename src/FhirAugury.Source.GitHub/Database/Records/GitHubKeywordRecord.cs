using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Common.Database;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Per-document keyword with term frequency and BM25 score.</summary>
[LdgSQLiteTable("index_keywords")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(Keyword))]
[LdgSQLiteIndex(nameof(Keyword), nameof(KeywordType))]
public partial record class GitHubKeywordRecord : IKeywordRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public required string Keyword { get; set; }
    public required int Count { get; set; }
    public required string KeywordType { get; set; }
    public required double Bm25Score { get; set; }
}
