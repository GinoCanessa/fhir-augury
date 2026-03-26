namespace FhirAugury.Common.Configuration;

/// <summary>
/// Configuration for auxiliary databases used by the keyword extraction pipeline.
/// These databases are opened read-only and provide stop words, lemmatization data,
/// and FHIR domain knowledge.
/// </summary>
public class AuxiliaryDatabaseOptions
{
    /// <summary>
    /// Path to the auxiliary SQLite database containing stop_words and lemmas tables.
    /// When null, the system falls back to hardcoded stop words and no lemmatization.
    /// </summary>
    public string? AuxiliaryDatabasePath { get; set; }

    /// <summary>
    /// Path to the FHIR specification SQLite database containing elements and operations tables.
    /// When null, the system falls back to hardcoded FHIR vocabulary.
    /// </summary>
    public string? FhirSpecDatabasePath { get; set; }
}
