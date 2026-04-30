using System.Globalization;
using FhirAugury.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database;

/// <summary>
/// Read-only window onto the Planner database. Throws <see cref="InvalidOperationException"/>
/// with a clear message if the configured planner database file does not exist on first
/// poll. Intentionally narrow surface — the applier only needs to enumerate ticket
/// completion state and read per-(ticket, repo) plan changes.
/// </summary>
public sealed class PlannerReadOnlyDatabase : SourceDatabase
{
    public string DatabasePath { get; }

    public PlannerReadOnlyDatabase(string dbPath, ILogger<PlannerReadOnlyDatabase> logger)
        : base(dbPath, logger, readOnly: true)
    {
        DatabasePath = dbPath;
    }

    /// <summary>
    /// Concrete planner database has no schema to initialise from this side; planner owns it.
    /// </summary>
    protected override void InitializeSchema(SqliteConnection connection)
    {
    }

    /// <summary>
    /// Lists tickets that:
    /// - have a row in <c>jira_processing_source_tickets</c> with <c>ProcessingStatus = 'complete'</c>,
    /// - have a corresponding <c>planned_tickets</c> row (i.e. plan output exists),
    /// - and whose ticket type matches the supplied filter (when supplied).
    /// </summary>
    public IReadOnlyList<PlannerCompletedTicketView> ListCompletedPlannedTickets(IReadOnlyCollection<string>? ticketTypes)
    {
        EnsureFileExists();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.Key, j.Type, j.CompletionId, j.CompletedProcessingAt
            FROM jira_processing_source_tickets j
            INNER JOIN planned_tickets p ON p.Key = j.Key
            WHERE j.ProcessingStatus = 'complete'
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<PlannerCompletedTicketView> rows = [];
        HashSet<string>? typeFilter = ticketTypes is { Count: > 0 }
            ? new HashSet<string>(ticketTypes, StringComparer.OrdinalIgnoreCase)
            : null;
        while (reader.Read())
        {
            string key = reader.GetString(0);
            string type = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            string? completionId = reader.IsDBNull(2) ? null : reader.GetString(2);
            DateTimeOffset? completedAt = ParseDate(reader, 3);
            if (typeFilter is not null && !typeFilter.Contains(type))
            {
                continue;
            }

            rows.Add(new PlannerCompletedTicketView(key, type, completionId, completedAt));
        }
        return rows;
    }

    /// <summary>
    /// Returns the source ticket id for a given ticket key, used to populate the
    /// agent-CLI <c>SourceTicketId</c> environment variable.
    /// </summary>
    public string? GetSourceTicketId(string ticketKey)
    {
        EnsureFileExists();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM jira_processing_source_tickets WHERE Key = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", ticketKey);
        object? result = command.ExecuteScalar();
        return result is string s ? s : null;
    }

    /// <summary>Per-(ticket) planned repos.</summary>
    public IReadOnlyList<PlannedRepoView> ListPlannedRepos(string ticketKey)
    {
        EnsureFileExists();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, IssueKey, RepoKey, RepoRevision, Justification
            FROM planned_ticket_repos
            WHERE IssueKey = @key
            ORDER BY RepoKey ASC
            """;
        command.Parameters.AddWithValue("@key", ticketKey);
        using SqliteDataReader reader = command.ExecuteReader();
        List<PlannedRepoView> rows = [];
        while (reader.Read())
        {
            rows.Add(new PlannedRepoView(
                Id: reader.GetString(0),
                IssueKey: reader.GetString(1),
                RepoKey: reader.GetString(2),
                RepoRevision: reader.IsDBNull(3) ? null : reader.GetString(3),
                Justification: reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
        }
        return rows;
    }

    /// <summary>Per-(ticket, repo) planned repo changes, ordered by ChangeSequence.</summary>
    public IReadOnlyList<PlannedRepoChangeView> ListPlannedRepoChanges(string ticketKey, string repoKey)
    {
        EnsureFileExists();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, IssueKey, TicketRepoId, RepoKey, ChangeSequence, FilePath, ChangeTitle
            FROM planned_ticket_repo_changes
            WHERE IssueKey = @key AND RepoKey = @repo
            ORDER BY ChangeSequence ASC
            """;
        command.Parameters.AddWithValue("@key", ticketKey);
        command.Parameters.AddWithValue("@repo", repoKey);
        using SqliteDataReader reader = command.ExecuteReader();
        List<PlannedRepoChangeView> rows = [];
        while (reader.Read())
        {
            rows.Add(new PlannedRepoChangeView(
                Id: reader.GetString(0),
                IssueKey: reader.GetString(1),
                TicketRepoId: reader.GetString(2),
                RepoKey: reader.GetString(3),
                ChangeSequence: reader.GetInt32(4),
                FilePath: reader.GetString(5),
                ChangeTitle: reader.IsDBNull(6) ? string.Empty : reader.GetString(6)));
        }
        return rows;
    }

    private void EnsureFileExists()
    {
        if (!File.Exists(DatabasePath))
        {
            throw new InvalidOperationException(
                $"Planner database not found at '{DatabasePath}'. Configure Processing:Applier:PlannerDatabasePath to point at the planner output, or run the planner first.");
        }
    }

    private static DateTimeOffset? ParseDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        string value = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;
    }
}

/// <summary>Projection of a single completed planned ticket row.</summary>
public readonly record struct PlannerCompletedTicketView(
    string Key,
    string Type,
    string? CompletionId,
    DateTimeOffset? CompletedAt);

/// <summary>Projection of a single planned-ticket repo row.</summary>
public sealed record PlannedRepoView(
    string Id,
    string IssueKey,
    string RepoKey,
    string? RepoRevision,
    string Justification);

/// <summary>Projection of a single planned-ticket repo-change row.</summary>
public sealed record PlannedRepoChangeView(
    string Id,
    string IssueKey,
    string TicketRepoId,
    string RepoKey,
    int ChangeSequence,
    string FilePath,
    string ChangeTitle);
