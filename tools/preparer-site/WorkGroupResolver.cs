using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite;

internal static class WorkGroupResolver
{
    // TODO: Reference PreparerJiraProcessingDefaults.JiraSourceAddress directly
    // once the preparer-site tool takes a project reference to
    // FhirAugury.Processor.Jira.Fhir.Preparer. Today it does not, so we
    // duplicate the default here.
    private const string DefaultJiraSourceAddress = "http://localhost:5160";

    internal sealed record WorkGroupDto(string? Name, string? WorkGroupCode, string? WorkGroupNameClean);

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
                List<WorkGroupDto> groups = await ReadWorkGroupsAsync(response, ct).ConfigureAwait(false);
                foreach (WorkGroupDto group in groups)
                {
                    if (CaseInsensitiveEquals(group.WorkGroupCode, raw) ||
                        CaseInsensitiveEquals(group.WorkGroupNameClean, raw) ||
                        CaseInsensitiveEquals(group.Name, raw))
                    {
                        return group.Name;
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

    // Tolerates both the legacy bare-array shape and the new
    // JiraWorkGroupListResponse envelope ({ catalogJoinDegraded, items: [...] }).
    // Exposed internal-static for direct unit-test coverage.
    internal static async Task<List<WorkGroupDto>> ReadWorkGroupsAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        JsonElement element = await response.Content
            .ReadFromJsonAsync<JsonElement>(cancellationToken: ct)
            .ConfigureAwait(false);
        return ParseWorkGroups(element);
    }

    internal static List<WorkGroupDto> ParseWorkGroups(JsonElement element)
    {
        JsonElement array;
        if (element.ValueKind == JsonValueKind.Array)
        {
            array = element;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (!element.TryGetProperty("items", out array) &&
                !element.TryGetProperty("Items", out array))
            {
                return [];
            }
            if (array.ValueKind != JsonValueKind.Array) return [];
        }
        else
        {
            return [];
        }

        List<WorkGroupDto> result = [];
        foreach (JsonElement row in array.EnumerateArray())
        {
            result.Add(new WorkGroupDto(
                Name: GetStringIgnoreCase(row, "name"),
                WorkGroupCode: GetStringIgnoreCase(row, "workGroupCode"),
                WorkGroupNameClean: GetStringIgnoreCase(row, "workGroupNameClean")));
        }
        return result;
    }

    private static string? GetStringIgnoreCase(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
            }
        }
        return null;
    }

    private static bool CaseInsensitiveEquals(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
