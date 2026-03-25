namespace FhirAugury.Common.Configuration;

/// <summary>
/// Configuration for the shared dictionary database built from word lists and typo mappings.
/// The database is auto-created on startup if missing and can be force-rebuilt.
/// </summary>
public class DictionaryDatabaseOptions
{
    /// <summary>
    /// Path to the directory containing dictionary source files (*.words.txt, *.typo.txt).
    /// </summary>
    public string SourcePath { get; set; } = "./cache/dictionary";

    /// <summary>
    /// Path to the output SQLite dictionary database file.
    /// </summary>
    public string DatabasePath { get; set; } = "./data/dictionary.db";

    /// <summary>
    /// When true, forces a full rebuild of the dictionary database even if it already exists.
    /// </summary>
    public bool ForceRebuild { get; set; } = false;
}
