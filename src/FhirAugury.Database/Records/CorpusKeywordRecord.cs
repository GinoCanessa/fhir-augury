using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Database.Records;

/// <summary>Corpus-level keyword statistics for IDF computation.</summary>
[LdgSQLiteTable("index_corpus")]
[LdgSQLiteIndex(nameof(Keyword), nameof(KeywordType))]
public partial record class CorpusKeywordRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>The keyword.</summary>
    public required string Keyword { get; set; }

    /// <summary>Token classification: word, fhir_path, fhir_operation.</summary>
    public required string KeywordType { get; set; }

    /// <summary>Number of documents containing this keyword.</summary>
    public required int DocumentFrequency { get; set; }

    /// <summary>Inverse document frequency: log((N - df + 0.5) / (df + 0.5)).</summary>
    public required double Idf { get; set; }
}
