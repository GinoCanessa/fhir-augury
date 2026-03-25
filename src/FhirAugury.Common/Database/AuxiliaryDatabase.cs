using System.Collections.Frozen;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Database;

/// <summary>
/// Read-only SQLite database loader for auxiliary keyword extraction data:
/// stop words, lemmatization mappings, and FHIR domain knowledge.
/// Loaded once at startup; all collections are frozen for thread-safe access.
/// </summary>
public class AuxiliaryDatabase
{
    private const int MinTokenLength = 3;

    /// <summary>Merged stop words (hardcoded defaults + database-loaded).</summary>
    public FrozenSet<string> StopWords { get; }

    /// <summary>Lemmatizer built from database lemma mappings.</summary>
    public Lemmatizer Lemmatizer { get; }

    /// <summary>FHIR element paths (hardcoded defaults + database-loaded).</summary>
    public FrozenSet<string> FhirResourceNames { get; }

    /// <summary>FHIR operations (hardcoded defaults + database-loaded).</summary>
    public FrozenSet<string> FhirOperations { get; }

    public AuxiliaryDatabase(AuxiliaryDatabaseOptions options, ILogger logger)
    {
        List<string> dbStopWords = [];
        Dictionary<string, string> dbLemmas = new(StringComparer.Ordinal);
        List<string> dbFhirPaths = [];
        List<string> dbFhirOps = [];

        // Load from auxiliary database (stop words + lemmas)
        if (!string.IsNullOrEmpty(options.AuxiliaryDatabasePath))
        {
            string auxPath = Path.GetFullPath(options.AuxiliaryDatabasePath);
            if (File.Exists(auxPath))
            {
                using SqliteConnection connection = OpenReadOnly(auxPath);
                dbStopWords = LoadStopWords(connection, logger);
                dbLemmas = LoadLemmas(connection, logger);
            }
            else
            {
                logger.LogWarning("Auxiliary database not found: {Path}", auxPath);
            }
        }

        // Load from FHIR specification database (element paths + operations)
        if (!string.IsNullOrEmpty(options.FhirSpecDatabasePath))
        {
            string specPath = Path.GetFullPath(options.FhirSpecDatabasePath);
            if (File.Exists(specPath))
            {
                using SqliteConnection connection = OpenReadOnly(specPath);
                dbFhirPaths = LoadFhirElementPaths(connection, logger);
                dbFhirOps = LoadFhirOperationNames(connection, logger);
            }
            else
            {
                logger.LogWarning("FHIR spec database not found: {Path}", specPath);
            }
        }

        // Build merged collections
        StopWords = Text.StopWords.CreateMergedSet(dbStopWords);
        Lemmatizer = dbLemmas.Count > 0
            ? new Lemmatizer(dbLemmas.ToFrozenDictionary(StringComparer.Ordinal))
            : Lemmatizer.Empty;
        FhirResourceNames = FhirVocabulary.CreateMergedResourceNames(dbFhirPaths);
        FhirOperations = FhirVocabulary.CreateMergedOperations(dbFhirOps);

        logger.LogInformation(
            "Auxiliary data loaded: {StopWords} stop words, {Lemmas} lemmas, {FhirPaths} FHIR resource names, {FhirOps} FHIR operations",
            StopWords.Count, Lemmatizer.Count, FhirResourceNames.Count, FhirOperations.Count);
    }

    /// <summary>
    /// Creates an AuxiliaryDatabase with no external data — uses only hardcoded defaults.
    /// </summary>
    public static AuxiliaryDatabase CreateDefault(ILogger logger)
    {
        return new AuxiliaryDatabase(new AuxiliaryDatabaseOptions(), logger);
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        SqliteConnection connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static List<string> LoadStopWords(SqliteConnection connection, ILogger logger)
    {
        List<string> words = [];

        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT word FROM stop_words WHERE word IS NOT NULL;";
            using SqliteDataReader reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                string raw = reader.GetString(0).Trim().ToLowerInvariant();
                if (raw.Length > 0)
                {
                    words.Add(raw);
                }
            }

            logger.LogDebug("Loaded {Count} stop words from auxiliary database", words.Count);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Failed to load stop words from auxiliary database");
        }

        return words;
    }

    private static Dictionary<string, string> LoadLemmas(SqliteConnection connection, ILogger logger)
    {
        Dictionary<string, string> lemmas = new(StringComparer.Ordinal);

        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Inflection, Lemma FROM lemmas WHERE Inflection IS NOT NULL AND Lemma IS NOT NULL;";
            using SqliteDataReader reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                string inflection = reader.GetString(0).Trim().ToLowerInvariant();
                string lemma = reader.GetString(1).Trim().ToLowerInvariant();

                if (inflection.Length >= MinTokenLength && lemma.Length > 0 && !lemmas.ContainsKey(inflection))
                {
                    lemmas[inflection] = lemma;
                }
            }

            logger.LogDebug("Loaded {Count} lemma entries from auxiliary database", lemmas.Count);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Failed to load lemmas from auxiliary database");
        }

        return lemmas;
    }

    private static List<string> LoadFhirElementPaths(SqliteConnection connection, ILogger logger)
    {
        List<string> paths = [];

        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT Path FROM elements WHERE Path IS NOT NULL;";
            using SqliteDataReader reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                string path = reader.GetString(0).Trim();

                // Extract the resource name (first segment before the dot)
                int dotIndex = path.IndexOf('.');
                string resourceName = dotIndex >= 0 ? path[..dotIndex] : path;

                if (resourceName.Length > 0)
                {
                    paths.Add(resourceName);
                }
            }

            logger.LogDebug("Loaded {Count} FHIR element paths from spec database", paths.Count);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Failed to load FHIR element paths from spec database");
        }

        return paths;
    }

    private static List<string> LoadFhirOperationNames(SqliteConnection connection, ILogger logger)
    {
        List<string> operations = [];

        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT Code FROM operations WHERE Code IS NOT NULL;";
            using SqliteDataReader reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                string code = reader.GetString(0).Trim().ToLowerInvariant();
                if (code.Length > 0)
                {
                    // Ensure $-prefix for consistency
                    if (!code.StartsWith('$'))
                    {
                        code = $"${code}";
                    }

                    operations.Add(code);
                }
            }

            logger.LogDebug("Loaded {Count} FHIR operations from spec database", operations.Count);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Failed to load FHIR operations from spec database");
        }

        return operations;
    }
}
