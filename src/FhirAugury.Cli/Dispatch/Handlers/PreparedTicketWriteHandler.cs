using FhirAugury.Cli.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class PreparedTicketWriteHandler
{
    public static async Task<object> HandleAsync(PreparedTicketWriteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DbPath))
        {
            throw new ArgumentException("DbPath is required.");
        }

        if (request.Payload is null)
        {
            throw new ArgumentException("Payload is required.");
        }

        PreparedTicketPayloadValidator.ThrowIfInvalid(request.Payload);
        string dbPath = Path.GetFullPath(request.DbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        if (File.Exists(dbPath))
        {
            await EnsureLegacyModeAsync(dbPath, ct);
        }

        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ConnectionString);
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await EnsureLegacyModeAsync(connection, ct);
            await EnsureSchemaAsync(connection, ct);
            await ReplacePreparedTicketAsync(connection, request.Payload, ct);
            await ExecuteAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }

        return new
        {
            key = request.Payload.Key,
            preparedTicketRows = 1,
            repoRows = request.Payload.Repos.Count,
            relatedJiraRows = request.Payload.RelatedJiraTickets.Count,
            relatedZulipRows = request.Payload.RelatedZulipThreads.Count,
            relatedGitHubRows = request.Payload.RelatedGitHubItems.Count,
        };
    }

    private static async Task EnsureLegacyModeAsync(
        string dbPath,
        CancellationToken ct)
    {
        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString);
        await connection.OpenAsync(ct);
        await EnsureLegacyModeAsync(connection, ct);
    }

    private static async Task EnsureLegacyModeAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(
            connection,
            "authoring_processor_modes",
            ct))
        {
            return;
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Mode
            FROM authoring_processor_modes
            WHERE Mode IN ('run-backed', 'cutting-over')
            LIMIT 1
            """;
        string? mode = Convert.ToString(await command.ExecuteScalarAsync(ct));
        if (!string.IsNullOrWhiteSpace(mode))
        {
            throw new ArgumentException(
                $"prepared-ticket-write is unavailable while the processor database mode is '{mode}'.");
        }
    }

    private static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS prepared_tickets(
                RowId INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL UNIQUE,
                Key TEXT NOT NULL UNIQUE,
                RequestSummary TEXT NOT NULL,
                CommentSummary TEXT NOT NULL,
                LinkedTicketSummary TEXT NOT NULL,
                RelatedTicketSummary TEXT NOT NULL,
                RelatedZulipSummary TEXT NOT NULL,
                RelatedGitHubSummary TEXT NOT NULL,
                ExistingProposed TEXT NOT NULL,
                ProposalA TEXT NOT NULL,
                ProposalAJustification TEXT NOT NULL,
                ProposalAImpact TEXT NOT NULL,
                ProposalB TEXT NOT NULL,
                ProposalBJustification TEXT NOT NULL,
                ProposalBImpact TEXT NOT NULL,
                ProposalC TEXT NOT NULL,
                ProposalCJustification TEXT NOT NULL,
                Recommendation TEXT NOT NULL,
                RecommendationJustification TEXT NOT NULL,
                SavedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS prepared_ticket_repos(
                RowId INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL UNIQUE,
                TicketKey TEXT NOT NULL,
                Repo TEXT NOT NULL,
                RepoCategory TEXT NOT NULL,
                Justification TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS prepared_ticket_related_jira(
                RowId INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL UNIQUE,
                TicketKey TEXT NOT NULL,
                AssociatedTicketKey TEXT NOT NULL,
                LinkType TEXT NOT NULL,
                Justification TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS prepared_ticket_related_zulip(
                RowId INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL UNIQUE,
                TicketKey TEXT NOT NULL,
                ZulipThreadId TEXT NOT NULL,
                Justification TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS prepared_ticket_related_github(
                RowId INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL UNIQUE,
                TicketKey TEXT NOT NULL,
                GitHubItemId TEXT NOT NULL,
                Justification TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS prepared_ticket_authoring_state(
                TicketKey TEXT NOT NULL PRIMARY KEY,
                Classification TEXT NOT NULL,
                GraphHash TEXT NOT NULL,
                RunId TEXT NULL,
                RunItemId TEXT NULL,
                OperationId TEXT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """,
            ct);
    }

    private static async Task ReplacePreparedTicketAsync(
        SqliteConnection connection,
        PreparedTicketPayload payload,
        CancellationToken ct)
    {
        string savedAt = (payload.SavedAt ?? DateTimeOffset.UtcNow)
            .ToString("O");
        foreach (string table in new[]
        {
            "prepared_ticket_repos",
            "prepared_ticket_related_jira",
            "prepared_ticket_related_zulip",
            "prepared_ticket_related_github",
            "prepared_tickets",
        })
        {
            await ExecuteAsync(
                connection,
                $"DELETE FROM {table} WHERE {(table == "prepared_tickets" ? "Key" : "TicketKey")} = @key",
                ct,
                ("@key", payload.Key));
        }

        if (await TableExistsAsync(
                connection,
                "prepared_ticket_topic_members",
                ct))
        {
            await ExecuteAsync(
                connection,
                "DELETE FROM prepared_ticket_topic_members WHERE TicketKey = @key",
                ct,
                ("@key", payload.Key));
        }

        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_tickets(
                Id, Key, RequestSummary, CommentSummary, LinkedTicketSummary,
                RelatedTicketSummary, RelatedZulipSummary, RelatedGitHubSummary,
                ExistingProposed, ProposalA, ProposalAJustification, ProposalAImpact,
                ProposalB, ProposalBJustification, ProposalBImpact, ProposalC,
                ProposalCJustification, Recommendation, RecommendationJustification,
                SavedAt)
            VALUES(
                @id, @key, @requestSummary, @commentSummary, @linkedTicketSummary,
                @relatedTicketSummary, @relatedZulipSummary, @relatedGitHubSummary,
                @existingProposed, @proposalA, @proposalAJustification, @proposalAImpact,
                @proposalB, @proposalBJustification, @proposalBImpact, @proposalC,
                @proposalCJustification, @recommendation, @recommendationJustification,
                @savedAt)
            """,
            ct,
            ("@id", Guid.NewGuid().ToString("N")),
            ("@key", payload.Key),
            ("@requestSummary", payload.RequestSummary),
            ("@commentSummary", payload.CommentSummary),
            ("@linkedTicketSummary", payload.LinkedTicketSummary),
            ("@relatedTicketSummary", payload.RelatedTicketSummary),
            ("@relatedZulipSummary", payload.RelatedZulipSummary),
            ("@relatedGitHubSummary", payload.RelatedGitHubSummary),
            ("@existingProposed", payload.ExistingProposed),
            ("@proposalA", payload.ProposalA),
            ("@proposalAJustification", payload.ProposalAJustification),
            ("@proposalAImpact", payload.ProposalAImpact),
            ("@proposalB", payload.ProposalB),
            ("@proposalBJustification", payload.ProposalBJustification),
            ("@proposalBImpact", payload.ProposalBImpact),
            ("@proposalC", payload.ProposalC),
            ("@proposalCJustification", payload.ProposalCJustification),
            ("@recommendation", payload.Recommendation),
            ("@recommendationJustification", payload.RecommendationJustification),
            ("@savedAt", savedAt));

        foreach (PreparedTicketRepoPayload row in payload.Repos)
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO prepared_ticket_repos(
                    Id, TicketKey, Repo, RepoCategory, Justification)
                VALUES(@id, @key, @repo, @category, @justification)
                """,
                ct,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@key", payload.Key),
                ("@repo", row.Repo),
                ("@category", row.RepoCategory),
                ("@justification", row.Justification));
        }

        foreach (PreparedTicketRelatedJiraPayload row in payload.RelatedJiraTickets)
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO prepared_ticket_related_jira(
                    Id, TicketKey, AssociatedTicketKey, LinkType, Justification)
                VALUES(@id, @key, @associated, @linkType, @justification)
                """,
                ct,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@key", payload.Key),
                ("@associated", row.AssociatedTicketKey),
                ("@linkType", row.LinkType),
                ("@justification", row.Justification));
        }
        foreach (PreparedTicketRelatedZulipPayload row in payload.RelatedZulipThreads)
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO prepared_ticket_related_zulip(
                    Id, TicketKey, ZulipThreadId, Justification)
                VALUES(@id, @key, @thread, @justification)
                """,
                ct,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@key", payload.Key),
                ("@thread", row.ZulipThreadId),
                ("@justification", row.Justification));
        }
        foreach (PreparedTicketRelatedGitHubPayload row in payload.RelatedGitHubItems)
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO prepared_ticket_related_github(
                    Id, TicketKey, GitHubItemId, Justification)
                VALUES(@id, @key, @item, @justification)
                """,
                ct,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@key", payload.Key),
                ("@item", row.GitHubItemId),
                ("@justification", row.Justification));
        }

        await ExecuteAsync(
            connection,
            """
            INSERT INTO prepared_ticket_authoring_state(
                TicketKey, Classification, GraphHash, RunId, RunItemId,
                OperationId, UpdatedAt)
            VALUES(
                @key, 'legacy-unverified', @hash, NULL, NULL, NULL, @savedAt)
            ON CONFLICT(TicketKey) DO UPDATE SET
                Classification = excluded.Classification,
                GraphHash = excluded.GraphHash,
                RunId = NULL,
                RunItemId = NULL,
                OperationId = NULL,
                UpdatedAt = excluded.UpdatedAt
            """,
            ct,
            ("@key", payload.Key),
            ("@hash", AuthoringHttpClient.HashWebJson(payload)),
            ("@savedAt", savedAt));
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @table";
        command.Parameters.AddWithValue("@table", table);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return await command.ExecuteNonQueryAsync(ct);
    }
}
