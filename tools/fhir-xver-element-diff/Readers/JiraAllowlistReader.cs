using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.FhirXverElementDiff.Readers;

/// <summary>
/// The set of real <c>FHIR-N</c> ticket numbers, used to validate every ticket reference
/// extracted from a commit message. HL7/fhir commit messages carry bogus <c>FHIR-</c>
/// tokens (e.g. build numbers, branch fragments) that are not tickets; membership in this
/// allowlist is what separates a real citation from noise.
/// </summary>
internal sealed record FhirKeyAllowlist(HashSet<int> Numbers)
{
    public bool IsEmpty => Numbers.Count == 0;
}

/// <summary>
/// Loads the FHIR-project ticket-number allowlist from the Jira cache DB
/// (<c>jira_issues WHERE ProjectKey='FHIR'</c>). Read-only.
/// </summary>
internal static class JiraAllowlistReader
{
    public static FhirKeyAllowlist Load(string jiraDbPath)
    {
        HashSet<int> numbers = [];

        using SqliteConnection conn = new(new SqliteConnectionStringBuilder
        {
            DataSource = jiraDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key FROM jira_issues WHERE ProjectKey = 'FHIR' AND Key LIKE 'FHIR-%'";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }
            string key = reader.GetString(0);
            int dash = key.LastIndexOf('-');
            if (dash >= 0 && int.TryParse(key.AsSpan(dash + 1), out int number))
            {
                numbers.Add(number);
            }
        }

        return new FhirKeyAllowlist(numbers);
    }
}
