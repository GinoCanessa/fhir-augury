using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.PreparerSite.Tests;

internal static class PreparerTestDb
{
    public sealed record SourceTicketSeed(
        string Key,
        string Project = "FHIR",
        string WorkGroup = "FHIR Infrastructure",
        string Status = "Open",
        string Type = "Change Request");

    public static async Task SeedAsync(
        string dbPath,
        IReadOnlyList<SourceTicketSeed> tickets,
        IReadOnlyDictionary<string, string?>? specByKey = null,
        bool seedAllChildTables = false)
    {
        using PreparerDatabase preparer = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        preparer.Initialize();

        foreach (SourceTicketSeed ticket in tickets)
        {
            PreparedTicketPayload payload = new()
            {
                Key = ticket.Key,
                RequestSummary = $"Request summary for {ticket.Key}.",
                CommentSummary = $"Comment summary for {ticket.Key}.",
                ProposalA = "Proposal A.",
                ProposalAJustification = "Justification A.",
                ProposalAImpact = "Non-substantive",
                ProposalB = "Proposal B.",
                ProposalBJustification = "Justification B.",
                ProposalBImpact = "Compatible, substantive",
                ProposalC = "Proposal C.",
                Recommendation = "A",
                RecommendationJustification = "Recommendation justification.",
            };
            if (seedAllChildTables)
            {
                payload.Repos.Add(new PreparedTicketRepoPayload
                {
                    Repo = "HL7/fhir",
                    RepoCategory = "core",
                    Justification = "repo justification",
                });
                payload.RelatedJiraTickets.Add(new PreparedTicketRelatedJiraPayload
                {
                    AssociatedTicketKey = "REL-9001",
                    LinkType = "related",
                    Justification = "related justification",
                });
            }
            await preparer.SavePreparedTicketAsync(payload);
        }

        await using SqliteConnection connection = new($"Data Source={dbPath}");
        await connection.OpenAsync();
        foreach (SourceTicketSeed ticket in tickets)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO jira_processing_source_tickets " +
                "(Id, Key, Title, Description, Project, Status, WorkGroup, Type, SourceTicketShape, LastSyncedAt, LastUpdated, ProcessingAttemptCount, ProcessingStatus) " +
                "VALUES (@id, @key, @title, @desc, @project, @status, @wg, @type, @shape, @synced, @updated, @pac, @ps)";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@key", ticket.Key);
            cmd.Parameters.AddWithValue("@title", $"Source ticket title {ticket.Key}");
            cmd.Parameters.AddWithValue("@desc", DBNull.Value);
            cmd.Parameters.AddWithValue("@project", ticket.Project);
            cmd.Parameters.AddWithValue("@status", ticket.Status);
            cmd.Parameters.AddWithValue("@wg", ticket.WorkGroup);
            cmd.Parameters.AddWithValue("@type", ticket.Type);
            cmd.Parameters.AddWithValue("@shape", "default");
            cmd.Parameters.AddWithValue("@synced", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@pac", 0);
            cmd.Parameters.AddWithValue("@ps", "Done");
            await cmd.ExecuteNonQueryAsync();
        }

        if (specByKey is not null)
        {
            string hydratedAt = DateTimeOffset.UtcNow.ToString("O");
            foreach ((string key, string? spec) in specByKey)
            {
                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO prepared_ticket_hydration " +
                    "(Id, TicketKey, Priority, Resolution, Specification, RaisedInVersion, CommentCount, DescriptionPlain, HydratedAt, HydrationStatus) " +
                    "VALUES (@id, @key, 'Major', 'Persuasive', @spec, '5.0.0', 0, NULL, @hat, 'resolved')";
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@spec", (object?)spec ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@hat", hydratedAt);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        if (seedAllChildTables)
        {
            string hydratedAt = DateTimeOffset.UtcNow.ToString("O");
            foreach (SourceTicketSeed ticket in tickets)
            {
                await ExecAsync(connection,
                    "INSERT INTO prepared_jira_hydration (Id, TicketKey, JiraKey, Title, Status, Type, HydratedAt, HydrationStatus) " +
                    "VALUES (@id, @key, @jk, 'related jira', 'Open', 'CR', @hat, 'resolved')",
                    [("@id", Guid.NewGuid().ToString("N")), ("@key", ticket.Key),
                     ("@jk", "REL-9001"), ("@hat", hydratedAt)]);
                await ExecAsync(connection,
                    "INSERT INTO prepared_zulip_hydration (Id, TicketKey, ZulipThreadId, StreamName, Topic, MessageCount, HydratedAt, HydrationStatus) " +
                    "VALUES (@id, @key, @tid, 'implementers', 'topic', 1, @hat, 'resolved')",
                    [("@id", Guid.NewGuid().ToString("N")), ("@key", ticket.Key),
                     ("@tid", $"impl:{ticket.Key}"), ("@hat", hydratedAt)]);
                await ExecAsync(connection,
                    "INSERT INTO prepared_github_hydration (Id, TicketKey, GitHubItemId, Owner, Repo, Number, State, IsPullRequest, HydratedAt, HydrationStatus) " +
                    "VALUES (@id, @key, @itm, 'HL7', 'fhir', 1, 'open', 0, @hat, 'resolved')",
                    [("@id", Guid.NewGuid().ToString("N")), ("@key", ticket.Key),
                     ("@itm", $"HL7/fhir#{ticket.Key}"), ("@hat", hydratedAt)]);
                await ExecAsync(connection,
                    "INSERT INTO prepared_repo_hydration (Id, TicketKey, Repo, Description, CategoryDetail, Url, HydratedAt, HydrationStatus) " +
                    "VALUES (@id, @key, 'HL7/fhir', 'core', 'FhirCore', 'https://x', @hat, 'resolved')",
                    [("@id", Guid.NewGuid().ToString("N")), ("@key", ticket.Key), ("@hat", hydratedAt)]);
                await ExecAsync(connection,
                    "INSERT INTO prepared_ticket_jira_xref (Id, TicketKey, JiraKey, Source) " +
                    "VALUES (@id, @key, @jk, 'DuplicateOf')",
                    [("@id", Guid.NewGuid().ToString("N")), ("@key", ticket.Key), ("@jk", "XREF-9002")]);
            }
        }

        await using SqliteConnection cp = new($"Data Source={dbPath}");
        await cp.OpenAsync();
        await using SqliteCommand checkpoint = cp.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await checkpoint.ExecuteNonQueryAsync();
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
