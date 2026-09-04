using System.Text.Json;
using FhirAugury.Cli.Dispatch;
using FhirAugury.Cli.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Cli.Tests;

public sealed class PreparedTicketWriteHandlerTests
{
    [Fact]
    public async Task WritesValidPayloadAndReturnsRowCounts()
    {
        string dbPath = CreateDbPath();
        PreparedTicketWriteRequest request = new() { Command = "prepared-ticket-write", DbPath = dbPath, Payload = SamplePayload("FHIR-123") };

        OutputEnvelope envelope = await CommandDispatcher.ExecuteAsync(JsonSerializer.Serialize(request));

        Assert.True(envelope.Success);
        Assert.Equal(1, Count(dbPath, "prepared_tickets"));
        Assert.Equal(1, Count(dbPath, "prepared_ticket_repos"));
        string data = JsonSerializer.Serialize(envelope.Data);
        Assert.Contains("FHIR-123", data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidPayloadDoesNotMutateDatabase()
    {
        string dbPath = CreateDbPath();
        PreparedTicketWriteRequest valid = new() { Command = "prepared-ticket-write", DbPath = dbPath, Payload = SamplePayload("FHIR-123") };
        Assert.True((await CommandDispatcher.ExecuteAsync(JsonSerializer.Serialize(valid))).Success);
        PreparedTicketPayload invalidPayload = SamplePayload("FHIR-123");
        invalidPayload.Recommendation = "Z";
        PreparedTicketWriteRequest invalid = new() { Command = "prepared-ticket-write", DbPath = dbPath, Payload = invalidPayload };

        OutputEnvelope envelope = await CommandDispatcher.ExecuteAsync(JsonSerializer.Serialize(invalid));

        Assert.False(envelope.Success);
        Assert.Equal(1, Count(dbPath, "prepared_tickets"));
        Assert.Equal(1, Count(dbPath, "prepared_ticket_repos"));
    }

    [Fact]
    public async Task ReplacingTicketRemovesStaleGroupingMembership()
    {
        string dbPath = CreateDbPath();
        PreparedTicketWriteRequest request = new()
        {
            Command = "prepared-ticket-write",
            DbPath = dbPath,
            Payload = SamplePayload("FHIR-123"),
        };
        Assert.True(
            (await CommandDispatcher.ExecuteAsync(
                JsonSerializer.Serialize(request))).Success);
        using (SqliteConnection connection =
            new($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE prepared_ticket_topic_members(
                    TicketKey TEXT NOT NULL
                );
                INSERT INTO prepared_ticket_topic_members(TicketKey)
                VALUES('FHIR-123');
                """;
            command.ExecuteNonQuery();
        }

        OutputEnvelope envelope = await CommandDispatcher.ExecuteAsync(
            JsonSerializer.Serialize(request));

        Assert.True(envelope.Success);
        Assert.Equal(0, Count(dbPath, "prepared_ticket_topic_members"));
    }

    [Fact]
    public async Task MissingDbPathReturnsFailureEnvelope()
    {
        PreparedTicketWriteRequest request = new() { Command = "prepared-ticket-write", Payload = SamplePayload("FHIR-123") };

        OutputEnvelope envelope = await CommandDispatcher.ExecuteAsync(JsonSerializer.Serialize(request));

        Assert.False(envelope.Success);
        Assert.Equal("INVALID_ARGUMENT", envelope.Error?.Code);
    }

    [Theory]
    [InlineData("run-backed")]
    [InlineData("cutting-over")]
    public async Task DurableModeRefusesTransitionalWrite(string mode)
    {
        string dbPath = CreateDbPath();
        using (SqliteConnection connection = new($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE authoring_processor_modes(Mode TEXT NOT NULL);
                INSERT INTO authoring_processor_modes(Mode) VALUES(@mode);
                """;
            command.Parameters.AddWithValue("@mode", mode);
            command.ExecuteNonQuery();
        }

        OutputEnvelope envelope = await CommandDispatcher.ExecuteAsync(
            JsonSerializer.Serialize(new PreparedTicketWriteRequest
            {
                Command = "prepared-ticket-write",
                DbPath = dbPath,
                Payload = SamplePayload("FHIR-123"),
            }));

        Assert.False(envelope.Success);
        Assert.Equal("INVALID_ARGUMENT", envelope.Error?.Code);
        Assert.Contains(mode, envelope.Error?.Message);
        using SqliteConnection verify = new($"Data Source={dbPath};Pooling=False");
        verify.Open();
        using SqliteCommand exists = verify.CreateCommand();
        exists.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='prepared_tickets'";
        Assert.Equal(0L, Convert.ToInt64(exists.ExecuteScalar()));
    }

    private static string CreateDbPath()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "prepared-ticket-write", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "preparer.db");
    }

    private static PreparedTicketPayload SamplePayload(string key) => new()
    {
        Key = key,
        RequestSummary = "request",
        CommentSummary = "comments",
        LinkedTicketSummary = "linked",
        RelatedTicketSummary = "related",
        RelatedZulipSummary = "zulip",
        RelatedGitHubSummary = "github",
        ExistingProposed = "existing",
        ProposalA = "proposal a",
        ProposalAJustification = "why a",
        ProposalAImpact = "Non-substantive",
        ProposalB = "proposal b",
        ProposalBJustification = "why b",
        ProposalBImpact = "Compatible, substantive",
        ProposalC = "proposal c",
        ProposalCJustification = "why c",
        Recommendation = "A",
        RecommendationJustification = "because",
        Repos = [new PreparedTicketRepoPayload { Repo = "HL7/fhir", RepoCategory = "FHIR Core", Justification = "repo" }],
    };

    private static int Count(string dbPath, string table)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
