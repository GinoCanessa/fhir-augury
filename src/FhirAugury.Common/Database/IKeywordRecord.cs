namespace FhirAugury.Common.Database;

/// <summary>
/// Common interface for per-document keyword records used in BM25 indexing.
/// Each source defines its own concrete record type with CsLightDbGen attributes.
/// </summary>
public interface IKeywordRecord
{
    int Id { get; set; }
    string SourceType { get; set; }
    string SourceId { get; set; }
    string Keyword { get; set; }
    int Count { get; set; }
    string KeywordType { get; set; }
    double Bm25Score { get; set; }
}

/// <summary>
/// Common interface for corpus-level keyword statistics records.
/// </summary>
public interface ICorpusKeywordRecord
{
    int Id { get; set; }
    string Keyword { get; set; }
    string KeywordType { get; set; }
    int DocumentFrequency { get; set; }
    double Idf { get; set; }
}

/// <summary>
/// Common interface for per-source-type document statistics records.
/// </summary>
public interface IDocStatsRecord
{
    int Id { get; set; }
    string SourceType { get; set; }
    int TotalDocuments { get; set; }
    double AverageDocLength { get; set; }
}
