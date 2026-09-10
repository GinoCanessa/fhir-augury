using FhirAugury.Common.WorkGroups;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets;

internal static class WorkGroupResolver
{
    private sealed record WorkGroupDto(
        string Name,
        string WorkGroupCode,
        string WorkGroupNameClean);

    public static async Task<string?> TryResolveAsync(
        string raw,
        string snapshotDbPath,
        CancellationToken ct)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = snapshotDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        List<WorkGroupDto> groups = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT Name, Code, NameClean FROM jira_review_workgroups ORDER BY Name";
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            groups.Add(new WorkGroupDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        List<Hl7WorkGroupDto> snapshot = new(groups.Count);
        foreach (WorkGroupDto group in groups)
        {
            snapshot.Add(new Hl7WorkGroupDto(
                group.WorkGroupCode,
                group.Name,
                Definition: null,
                Retired: false,
                NameClean: group.WorkGroupNameClean));
        }

        FhirAugury.Common.WorkGroups.WorkGroupResolver resolver = new(snapshot);
        WorkGroupResolveResult result = resolver.Resolve(raw);
        return result.Outcome == WorkGroupResolveOutcome.Found
            ? result.Match!.Name
            : null;
    }
}
