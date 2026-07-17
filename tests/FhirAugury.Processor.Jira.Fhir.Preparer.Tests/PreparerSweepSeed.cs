using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

internal static class PreparerSweepSeed
{
    public static async Task SeedPreparedTicketAsync(PreparerDatabase database, string ticketKey)
    {
        PreparedTicketPayload payload = new()
        {
            Key = ticketKey,
            RequestSummary = "rs",
            CommentSummary = "cs",
            LinkedTicketSummary = "ls",
            RelatedTicketSummary = "rts",
            RelatedZulipSummary = "rzs",
            RelatedGitHubSummary = "rgs",
            ExistingProposed = "ep",
            ProposalA = "a",
            ProposalAJustification = "aj",
            ProposalAImpact = "Non-substantive",
            ProposalB = "b",
            ProposalBJustification = "bj",
            ProposalBImpact = "Compatible, substantive",
            ProposalC = "c",
            ProposalCJustification = "cj",
            Recommendation = "A",
            RecommendationJustification = "rj",
            SavedAt = DateTimeOffset.UtcNow,
        };
        await database.SavePreparedTicketAsync(payload);
    }

    public static async Task InsertHydrationRowAsync(string dbPath, string ticketKey, string status)
    {
        await using SqliteConnection connection = new($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO prepared_ticket_hydration " +
            "(Id, TicketKey, HydratedAt, HydrationStatus) " +
            "VALUES (@id, @key, @hat, @status)";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@key", ticketKey);
        cmd.Parameters.AddWithValue("@hat", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@status", status);
        await cmd.ExecuteNonQueryAsync();
    }
}
