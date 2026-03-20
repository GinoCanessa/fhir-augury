using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Corpus-level keyword statistics for IDF computation.</summary>
[LdgSQLiteTable("index_corpus")]
[LdgSQLiteIndex(nameof(Keyword), nameof(KeywordType))]
public partial record class GitHubCorpusKeywordRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Keyword { get; set; }
    public required string KeywordType { get; set; }
    public required int DocumentFrequency { get; set; }
    public required double Idf { get; set; }
}
