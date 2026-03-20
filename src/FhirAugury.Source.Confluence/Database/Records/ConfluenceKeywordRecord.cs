using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Confluence.Database.Records;

/// <summary>Per-document keyword with term frequency and BM25 score.</summary>
[LdgSQLiteTable("index_keywords")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(Keyword))]
[LdgSQLiteIndex(nameof(Keyword), nameof(KeywordType))]
public partial record class ConfluenceKeywordRecord
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

/// <summary>Corpus-level keyword statistics for IDF computation.</summary>
[LdgSQLiteTable("index_corpus")]
[LdgSQLiteIndex(nameof(Keyword), nameof(KeywordType))]
public partial record class ConfluenceCorpusKeywordRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Keyword { get; set; }
    public required string KeywordType { get; set; }
    public required int DocumentFrequency { get; set; }
    public required double Idf { get; set; }
}

/// <summary>Per-source-type document statistics for BM25 normalization.</summary>
[LdgSQLiteTable("index_doc_stats")]
[LdgSQLiteIndex(nameof(SourceType))]
public partial record class ConfluenceDocStatsRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string SourceType { get; set; }

    public required int TotalDocuments { get; set; }
    public required double AverageDocLength { get; set; }
}
