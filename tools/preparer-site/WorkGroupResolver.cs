using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite;

internal static class WorkGroupResolver
{
    // TODO: Reference PreparerJiraProcessingDefaults.JiraSourceAddress directly
    // once the preparer-site tool takes a project reference to
    // FhirAugury.Processor.Jira.Fhir.Preparer. Today it does not, so we
    // duplicate the default here.
    private const string DefaultJiraSourceAddress = "http://localhost:5160";

    private sealed record WorkGroupDto(string? Name, string? WorkGroupCode, string? WorkGroupNameClean);

    public static async Task<string?> TryResolveAsync(
        string raw,
        string? httpUrl,
        string? jiraSourceDbPath,
        TextWriter stderr,
        CancellationToken ct)
    {
        string baseUrl = httpUrl ?? DefaultJiraSourceAddress;
        string? httpFailureReason = null;

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(3) };
            string requestUrl = baseUrl.TrimEnd('/') + "/api/v1/work-groups";
            HttpResponseMessage response = await client.GetAsync(requestUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                httpFailureReason = $"HTTP {(int)response.StatusCode}";
            }
            else
            {
                List<WorkGroupDto>? groups = await response.Content
                    .ReadFromJsonAsync<List<WorkGroupDto>>(cancellationToken: ct)
                    .ConfigureAwait(false);
                if (groups is not null)
                {
                    foreach (WorkGroupDto group in groups)
                    {
                        if (CaseInsensitiveEquals(group.WorkGroupCode, raw) ||
                            CaseInsensitiveEquals(group.WorkGroupNameClean, raw) ||
                            CaseInsensitiveEquals(group.Name, raw))
                        {
                            return group.Name;
                        }
                    }
                }
                // HTTP succeeded but no match — do not fall through to DB; the
                // service is authoritative on what workgroups exist.
                return null;
            }
        }
        catch (HttpRequestException ex)
        {
            httpFailureReason = ex.Message;
        }
        catch (TaskCanceledException)
        {
            httpFailureReason = "timeout";
        }
        catch (SocketException ex)
        {
            httpFailureReason = ex.Message;
        }

        if (httpFailureReason is not null)
        {
            if (string.IsNullOrEmpty(jiraSourceDbPath))
            {
                return null;
            }

            await stderr.WriteLineAsync(
                $"Jira source HTTP failed ({httpFailureReason}); falling back to --jira-source-db.")
                .ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(jiraSourceDbPath) || !File.Exists(jiraSourceDbPath))
        {
            return null;
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = jiraSourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT iw.Name FROM jira_index_workgroups iw " +
            "JOIN hl7_workgroups hwg ON hwg.Id = iw.WorkGroupId " +
            "WHERE hwg.Code = @g COLLATE NOCASE " +
            "   OR hwg.NameClean = @g COLLATE NOCASE " +
            "   OR iw.Name = @g COLLATE NOCASE " +
            "LIMIT 1";
        cmd.Parameters.AddWithValue("@g", raw);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    private static bool CaseInsensitiveEquals(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
