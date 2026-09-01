using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

/// <summary>
/// Resolves the owning work group of an artifact or page from the read-only
/// HL7 JIRA-Spec-Artifacts registry (<c>jira_specs</c> / <c>jira_spec_artifacts</c>
/// / <c>jira_spec_pages</c> / <c>jira_workgroups</c> in <c>github.db</c>).
/// </summary>
/// <remarks>
/// The hydrated repo (<c>owner/name</c>) is matched to its registry
/// specification(s) via <c>jira_specs.GitUrl</c> (case-insensitive substring),
/// the artifact/page is looked up within those specs, and the returned
/// <c>Workgroup</c> — a JIRA-Spec <em>WorkgroupKey</em>, not a canonical code —
/// is mapped through <c>jira_workgroups.WorkGroupCode</c>. The lookup is
/// deliberately conservative: it returns a code only when the registry yields a
/// <em>single</em> distinct owner, otherwise <c>null</c> so the resolver chain
/// falls through (common names like <c>Observation</c> must not mis-resolve once
/// the registry spans multiple repos). Best-effort: a missing table or schema
/// drift yields <c>null</c>.
/// </remarks>
public static class SpecArtifactWorkGroupResolver
{
    /// <summary>
    /// Returns the canonical owning work group code for <paramref name="unitName"/>
    /// (an <paramref name="unitType"/> of <c>Artifact</c> or <c>Page</c>), or
    /// <c>null</c> when unresolved or ambiguous.
    /// </summary>
    public static string? Resolve(
        SqliteConnection connection,
        string owner,
        string name,
        string unitType,
        string unitName,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(unitName)) return null;

        try
        {
            IReadOnlyList<(string RepoFullName, string SpecKey)> specs = MatchSpecs(connection, owner, name);
            if (specs.Count == 0) return null;

            bool isPage = string.Equals(unitType, "Page", StringComparison.OrdinalIgnoreCase);

            // Distinct owner keys (registry-repo-scoped) across the matched specs.
            HashSet<string> distinctCodes = new(StringComparer.OrdinalIgnoreCase);
            string? resolvedCode = null;

            foreach ((string repoFullName, string specKey) in specs)
            {
                foreach (string workgroupKey in LookupOwnerKeys(connection, repoFullName, specKey, unitName, isPage))
                {
                    string code = ResolveCode(connection, repoFullName, workgroupKey);
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    if (distinctCodes.Add(code)) resolvedCode = code;
                }
            }

            // Unambiguous single owner only; otherwise fall through.
            return distinctCodes.Count == 1 ? resolvedCode : null;
        }
        catch (SqliteException ex)
        {
            logger?.LogDebug(ex, "Registry owning-WG lookup failed for {Type} {Name}", unitType, unitName);
            return null;
        }
    }

    private static IReadOnlyList<(string RepoFullName, string SpecKey)> MatchSpecs(
        SqliteConnection connection, string owner, string name)
    {
        List<(string, string)> specs = [];
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT RepoFullName, SpecKey FROM jira_specs " +
            "WHERE GitUrl IS NOT NULL AND GitUrl LIKE $pat COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$pat", $"%{owner}/{name}%");
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string repoFullName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            string specKey = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (!string.IsNullOrEmpty(specKey)) specs.Add((repoFullName, specKey));
        }
        return specs;
    }

    private static IReadOnlyList<string> LookupOwnerKeys(
        SqliteConnection connection, string repoFullName, string specKey, string unitName, bool isPage)
    {
        List<string> keys = [];
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = isPage
            ? "SELECT Workgroup FROM jira_spec_pages " +
              "WHERE RepoFullName = $repo AND SpecKey = $spec AND Deprecated = 0 " +
              "AND (Name = $n COLLATE NOCASE OR PageKey = $n COLLATE NOCASE)"
            : "SELECT Workgroup FROM jira_spec_artifacts " +
              "WHERE RepoFullName = $repo AND SpecKey = $spec AND Deprecated = 0 " +
              "AND (Name = $n COLLATE NOCASE OR ArtifactId = $n COLLATE NOCASE OR ResourceType = $n COLLATE NOCASE)";
        cmd.Parameters.AddWithValue("$repo", repoFullName);
        cmd.Parameters.AddWithValue("$spec", specKey);
        cmd.Parameters.AddWithValue("$n", unitName);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            string key = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(key)) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// Maps a JIRA-Spec <c>WorkgroupKey</c> to its canonical HL7 code via
    /// <c>jira_workgroups</c>, falling back to the key itself when unmapped.
    /// </summary>
    private static string ResolveCode(SqliteConnection connection, string repoFullName, string workgroupKey)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT WorkGroupCode FROM jira_workgroups " +
            "WHERE RepoFullName = $repo AND WorkgroupKey = $key COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$repo", repoFullName);
        cmd.Parameters.AddWithValue("$key", workgroupKey);
        object? result = cmd.ExecuteScalar();
        return result is string code && !string.IsNullOrWhiteSpace(code) ? code : workgroupKey;
    }
}
