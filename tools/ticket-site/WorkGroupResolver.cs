using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using FhirAugury.Common.WorkGroups;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite;

internal static class WorkGroupResolver
{
    // TODO: Reference PreparerJiraProcessingDefaults.JiraSourceAddress directly
    // once the preparer-site tool takes a project reference to
    // FhirAugury.Processor.Jira.Fhir.Preparer. Today it does not, so we
    // duplicate the default here.
    private const string DefaultJiraSourceAddress = "http://localhost:5160";

    internal sealed record WorkGroupDto(string? Name, string? WorkGroupCode, string? WorkGroupNameClean);

    /// <summary>
    /// Resolves the user-supplied selector (any of <c>code</c>,
    /// <c>nameClean</c>, or <c>name</c>) against the jira-source catalog.
    /// Prefers the live HTTP endpoint; on connection failure (or when
    /// <paramref name="jiraSourceDbPath"/> is supplied), falls back to a
    /// read-only SQLite query against the jira-source DB. Returns the
    /// canonical workgroup <c>Name</c> (the value persisted on
    /// <c>jira_issues.WorkGroup</c>) or <c>null</c> when no match exists.
    /// </summary>
    /// <remarks>
    /// Uses the shared <see cref="FhirAugury.Common.WorkGroups.WorkGroupResolver"/>
    /// for the in-process matching pass so behaviour stays consistent
    /// with the jira-source endpoint and CLI. On
    /// <see cref="WorkGroupResolveOutcome.Ambiguous"/> the resolver
    /// refuses to silently pick — this method returns <c>null</c> in
    /// that case, mirroring the previous "no-match" behaviour.
    /// </remarks>
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
                return ResolveUsingSharedResolver(raw, groups);
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

        List<WorkGroupDto> snapshot = await LoadSnapshotFromDbAsync(connection, ct).ConfigureAwait(false);
        return ResolveUsingSharedResolver(raw, snapshot);
    }

    private static string? ResolveUsingSharedResolver(string raw, IReadOnlyList<WorkGroupDto> groups)
    {
        List<Hl7WorkGroupDto> snapshot = new(groups.Count);
        foreach (WorkGroupDto g in groups)
        {
            if (string.IsNullOrEmpty(g.Name)) continue;
            snapshot.Add(new Hl7WorkGroupDto(
                Code: g.WorkGroupCode ?? string.Empty,
                Name: g.Name,
                Definition: null,
                Retired: false,
                NameClean: g.WorkGroupNameClean ?? Hl7WorkGroupNameCleaner.Clean(g.Name)));
        }
        FhirAugury.Common.WorkGroups.WorkGroupResolver resolver =
            new FhirAugury.Common.WorkGroups.WorkGroupResolver(snapshot);
        WorkGroupResolveResult result = resolver.Resolve(raw);
        return result.Outcome == WorkGroupResolveOutcome.Found ? result.Match!.Name : null;
    }

    private static async Task<List<WorkGroupDto>> LoadSnapshotFromDbAsync(SqliteConnection connection, CancellationToken ct)
    {
        List<WorkGroupDto> result = [];
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT iw.Name, hwg.Code, hwg.NameClean FROM jira_index_workgroups iw " +
            "LEFT JOIN hl7_workgroups hwg ON hwg.Id = iw.WorkGroupId";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string? name = reader.IsDBNull(0) ? null : reader.GetString(0);
            string? code = reader.IsDBNull(1) ? null : reader.GetString(1);
            string? nameClean = reader.IsDBNull(2) ? null : reader.GetString(2);
            result.Add(new WorkGroupDto(name, code, nameClean));
        }
        return result;
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
}
