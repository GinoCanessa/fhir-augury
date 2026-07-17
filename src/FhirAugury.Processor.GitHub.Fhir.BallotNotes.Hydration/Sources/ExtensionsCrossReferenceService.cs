using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

/// <summary>A referenced extension that the CI build replaces with a core element.</summary>
public sealed record ExtensionCrossRef(
    string ExtensionUrl,
    string ExtensionName,
    string ReplacementCoreElement,
    string Rationale);

/// <summary>
/// Resolves referenced extension canonicals against the read-only GitHub source
/// DB (<c>github_structure_definitions</c> filtered to <c>HL7/fhir-extensions</c>)
/// and surfaces those the CI build maps to a replacing core element, with the
/// extension's description as rationale. Extension-only churn with no core
/// counterpart is suppressed (not returned). Best-effort: a missing DB yields an
/// empty result.
/// </summary>
public static partial class ExtensionsCrossReferenceService
{
    private const string ExtensionsRepo = "HL7/fhir-extensions";

    // Matches a "replaced by <Resource.element>" rationale and captures the
    // dotted core element path (e.g. "Patient.gender").
    [GeneratedRegex(@"replaced by (?:the )?([A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9\[\]]+)+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReplacedByPattern();

    /// <summary>
    /// Returns the cross-references for the supplied extension URLs that resolve to
    /// a replacing core element in the extensions pack; suppresses the rest.
    /// </summary>
    public static IReadOnlyList<ExtensionCrossRef> Resolve(
        string githubDbPath,
        IReadOnlyCollection<string> extensionUrls,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(githubDbPath) || !File.Exists(githubDbPath) || extensionUrls.Count == 0)
        {
            return [];
        }

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = githubDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;

        List<ExtensionCrossRef> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using SqliteConnection connection = new(connectionString);
            connection.Open();

            foreach (string url in extensionUrls)
            {
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url)) continue;

                using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT Name, Description FROM github_structure_definitions " +
                    "WHERE RepoFullName = $repo AND Url = $url LIMIT 1";
                cmd.Parameters.AddWithValue("$repo", ExtensionsRepo);
                cmd.Parameters.AddWithValue("$url", url);

                using SqliteDataReader reader = cmd.ExecuteReader();
                if (!reader.Read()) continue; // not in the CI build → not surfaced

                string name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                string description = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

                Match match = ReplacedByPattern().Match(description);
                if (!match.Success) continue; // exists but no core counterpart → suppress

                results.Add(new ExtensionCrossRef(url, name, match.Groups[1].Value, description));
            }
        }
        catch (SqliteException ex)
        {
            logger?.LogDebug(ex, "Extensions cross-reference query failed against {Db}", githubDbPath);
            return results;
        }

        return results;
    }
}
