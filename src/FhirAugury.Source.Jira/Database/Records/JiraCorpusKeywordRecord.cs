using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Common.Database;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>Corpus-level keyword statistics for IDF computation.</summary>
[LdgSQLiteTable("index_corpus")]
[LdgSQLiteIndex(nameof(Keyword), nameof(KeywordType))]
public partial record class JiraCorpusKeywordRecord : ICorpusKeywordRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Keyword { get; set; }
    public required string KeywordType { get; set; }
    public required int DocumentFrequency { get; set; }
    public required double Idf { get; set; }
}
