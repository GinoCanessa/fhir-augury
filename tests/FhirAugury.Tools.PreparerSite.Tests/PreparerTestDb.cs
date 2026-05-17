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
        IReadOnlyDictionary<string, string?>? specByKey = null)
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

        await using SqliteConnection cp = new($"Data Source={dbPath}");
        await cp.OpenAsync();
        await using SqliteCommand checkpoint = cp.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await checkpoint.ExecuteNonQueryAsync();
    }
}
