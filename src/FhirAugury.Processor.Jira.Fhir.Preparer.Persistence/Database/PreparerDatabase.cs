using System.Globalization;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;

public sealed class PreparerDatabase(string dbPath, ILogger<PreparerDatabase> logger, bool readOnly = false)
    : FhirAugury.Processing.Common.Database.ProcessingDatabase(dbPath, logger, readOnly)
{
    public string DatabasePath { get; } = dbPath;

    /// <summary>
    /// Idempotent. Creates every preparer table via <c>CREATE TABLE IF NOT EXISTS</c>
    /// and follows up with the <c>CREATE UNIQUE INDEX IF NOT EXISTS</c> passes required
    /// by CsLightDbGen's lack of composite-unique support. Safe to call against a
    /// connection the preparer does not own (e.g., <c>preparer-site</c>'s trim-step
    /// temp copy).
    /// </summary>
    public static void EnsureSchema(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        JiraProcessingSourceTicketStore.EnsureSchema(connection);
        PreparedTicketRecord.CreateTable(connection);
        PreparedTicketRepoRecord.CreateTable(connection);
        PreparedTicketRelatedJiraRecord.CreateTable(connection);
        PreparedTicketRelatedZulipRecord.CreateTable(connection);
        PreparedTicketRelatedGitHubRecord.CreateTable(connection);
        PreparedTicketHydrationRecord.CreateTable(connection);
        PreparedJiraHydrationRecord.CreateTable(connection);
        PreparedZulipHydrationRecord.CreateTable(connection);
        PreparedGitHubHydrationRecord.CreateTable(connection);
        PreparedRepoHydrationRecord.CreateTable(connection);
        PreparedTicketJiraXrefRecord.CreateTable(connection);
        EnsureHydrationCompositeUniqueIndexes(connection);
    }

    protected override void InitializeSchema(SqliteConnection connection)
        => EnsureSchema(connection);

    /// <summary>
    /// CsLightDbGen does not currently expose a way to declare a composite UNIQUE index
    /// (the <c>[LdgSQLiteIndex]</c> attribute has no Unique property), so the per-ticket
    /// uniqueness contract for each hydration table is enforced via follow-on
    /// <c>CREATE UNIQUE INDEX IF NOT EXISTS</c> statements, mirroring the
    /// <see cref="JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex"/> pattern.
    /// </summary>
    private static void EnsureHydrationCompositeUniqueIndexes(SqliteConnection connection)
    {
        string[] statements =
        [
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_jira_hydration_ticket_jira ON prepared_jira_hydration(TicketKey, JiraKey);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_zulip_hydration_ticket_thread ON prepared_zulip_hydration(TicketKey, ZulipThreadId);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_github_hydration_ticket_item ON prepared_github_hydration(TicketKey, GitHubItemId);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_repo_hydration_ticket_repo ON prepared_repo_hydration(TicketKey, Repo);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_jira_xref_ticket_jira_source ON prepared_ticket_jira_xref(TicketKey, JiraKey, Source);",
        ];
        foreach (string sql in statements)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    public async Task<PreparedTicketSaveResult> SavePreparedTicketAsync(PreparedTicketPayload payload, CancellationToken ct = default)
    {
        PreparedTicketPayloadValidator.ThrowIfInvalid(payload);
        DateTimeOffset savedAt = payload.SavedAt ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        await begin.ExecuteNonQueryAsync(ct);
        try
        {
            await DeleteRowsAsync(connection, payload.Key, ct);
            await InsertParentAsync(connection, payload, savedAt, ct);
            foreach (PreparedTicketRepoPayload repo in payload.Repos)
            {
                await ExecuteAsync(connection, "INSERT INTO prepared_ticket_repos (Id, TicketKey, Repo, RepoCategory, Justification) VALUES (@id, @key, @repo, @category, @justification)", ct,
                    ("@id", Guid.NewGuid().ToString("N")), ("@key", payload.Key), ("@repo", repo.Repo), ("@category", repo.RepoCategory), ("@justification", repo.Justification));
            }

            foreach (PreparedTicketRelatedJiraPayload related in payload.RelatedJiraTickets)
            {
                await ExecuteAsync(connection, "INSERT INTO prepared_ticket_related_jira (Id, TicketKey, AssociatedTicketKey, LinkType, Justification) VALUES (@id, @key, @associated, @linkType, @justification)", ct,
                    ("@id", Guid.NewGuid().ToString("N")), ("@key", payload.Key), ("@associated", related.AssociatedTicketKey), ("@linkType", related.LinkType), ("@justification", related.Justification));
            }

            foreach (PreparedTicketRelatedZulipPayload related in payload.RelatedZulipThreads)
            {
                await ExecuteAsync(connection, "INSERT INTO prepared_ticket_related_zulip (Id, TicketKey, ZulipThreadId, Justification) VALUES (@id, @key, @thread, @justification)", ct,
                    ("@id", Guid.NewGuid().ToString("N")), ("@key", payload.Key), ("@thread", related.ZulipThreadId), ("@justification", related.Justification));
            }

            foreach (PreparedTicketRelatedGitHubPayload related in payload.RelatedGitHubItems)
            {
                await ExecuteAsync(connection, "INSERT INTO prepared_ticket_related_github (Id, TicketKey, GitHubItemId, Justification) VALUES (@id, @key, @item, @justification)", ct,
                    ("@id", Guid.NewGuid().ToString("N")), ("@key", payload.Key), ("@item", related.GitHubItemId), ("@justification", related.Justification));
            }

            await ExecuteRawAsync(connection, "COMMIT", ct);
            return new PreparedTicketSaveResult(payload.Key, 1, payload.Repos.Count, payload.RelatedJiraTickets.Count, payload.RelatedZulipThreads.Count, payload.RelatedGitHubItems.Count);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> PreparedTicketExistsAsync(string key, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM prepared_tickets WHERE Key = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", key);
        object? value = await command.ExecuteScalarAsync(ct);
        return value is not null;
    }

    public async Task SaveHydrationAsync(PreparedTicketHydrationBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        await begin.ExecuteNonQueryAsync(ct);
        try
        {
            await DeleteHydrationRowsAsync(connection, batch.TicketKey, ct);
            await InsertHydrationParentAsync(connection, batch.Parent, ct);
            foreach (PreparedJiraHydrationRow row in batch.JiraRows)
            {
                await InsertJiraHydrationAsync(connection, row, ct);
            }

            foreach (PreparedZulipHydrationRow row in batch.ZulipRows)
            {
                await InsertZulipHydrationAsync(connection, row, ct);
            }

            foreach (PreparedGitHubHydrationRow row in batch.GitHubRows)
            {
                await InsertGitHubHydrationAsync(connection, row, ct);
            }

            foreach (PreparedRepoHydrationRow row in batch.RepoRows)
            {
                await InsertRepoHydrationAsync(connection, row, ct);
            }

            foreach (PreparedTicketJiraXrefRow row in batch.JiraXrefRows)
            {
                await InsertJiraXrefAsync(connection, row, ct);
            }

            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<PreparedTicketHydrationReadModel?> GetHydrationAsync(string key, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        PreparedTicketHydrationRow? parent = await ReadHydrationParentAsync(connection, key, ct);
        IReadOnlyList<PreparedJiraHydrationRow> jira = await ReadJiraHydrationAsync(connection, key, ct);
        IReadOnlyList<PreparedZulipHydrationRow> zulip = await ReadZulipHydrationAsync(connection, key, ct);
        IReadOnlyList<PreparedGitHubHydrationRow> github = await ReadGitHubHydrationAsync(connection, key, ct);
        IReadOnlyList<PreparedRepoHydrationRow> repos = await ReadRepoHydrationAsync(connection, key, ct);
        IReadOnlyList<PreparedTicketJiraXrefRow> xref = await ReadJiraXrefAsync(connection, key, ct);
        if (parent is null && jira.Count == 0 && zulip.Count == 0 && github.Count == 0 && repos.Count == 0 && xref.Count == 0)
        {
            return null;
        }

        return new PreparedTicketHydrationReadModel(parent, jira, zulip, github, repos, xref);
    }

    /// <summary>
    /// Returns every <c>Key</c> in <c>prepared_tickets</c> in ascending order.
    /// Used by the hydration sweeper as the inventory pass.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListPreparedTicketKeysAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Key FROM prepared_tickets ORDER BY Key";
        List<string> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>
    /// Returns the set of <c>prepared_tickets.Key</c> values whose
    /// <c>prepared_ticket_hydration</c> row is either missing or has
    /// <c>HydrationStatus = 'unresolved'</c>. These are the keys the
    /// hydration sweeper should re-hydrate.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListUnresolvedOrMissingHydrationKeysAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Key FROM prepared_tickets t
            LEFT JOIN prepared_ticket_hydration h ON h.TicketKey = t.Key
            WHERE h.TicketKey IS NULL OR h.HydrationStatus = 'unresolved'
            ORDER BY t.Key
            """;
        List<string> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    public async Task<IReadOnlyList<string>> ListRelatedJiraKeysForTicketAsync(string key, CancellationToken ct = default)
        => await ReadStringColumnAsync(
            "SELECT AssociatedTicketKey FROM prepared_ticket_related_jira WHERE TicketKey = @key ORDER BY AssociatedTicketKey",
            key, ct);

    public async Task<IReadOnlyList<string>> ListRelatedZulipThreadIdsForTicketAsync(string key, CancellationToken ct = default)
        => await ReadStringColumnAsync(
            "SELECT ZulipThreadId FROM prepared_ticket_related_zulip WHERE TicketKey = @key ORDER BY ZulipThreadId",
            key, ct);

    public async Task<IReadOnlyList<string>> ListRelatedGitHubItemIdsForTicketAsync(string key, CancellationToken ct = default)
        => await ReadStringColumnAsync(
            "SELECT GitHubItemId FROM prepared_ticket_related_github WHERE TicketKey = @key ORDER BY GitHubItemId",
            key, ct);

    public async Task<IReadOnlyList<string>> ListReposForTicketAsync(string key, CancellationToken ct = default)
        => await ReadStringColumnAsync(
            "SELECT Repo FROM prepared_ticket_repos WHERE TicketKey = @key ORDER BY Repo",
            key, ct);

    private async Task<IReadOnlyList<string>> ReadStringColumnAsync(string sql, string key, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@key", key);
        List<string> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task DeleteHydrationRowsAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        foreach (string table in new[]
        {
            "prepared_ticket_hydration",
            "prepared_jira_hydration",
            "prepared_zulip_hydration",
            "prepared_github_hydration",
            "prepared_repo_hydration",
            "prepared_ticket_jira_xref",
        })
        {
            await ExecuteAsync(connection, $"DELETE FROM {table} WHERE TicketKey = @key", ct, ("@key", key));
        }
    }

    private static async Task InsertHydrationParentAsync(SqliteConnection connection, PreparedTicketHydrationRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_ticket_hydration
            (Id, TicketKey, Priority, Resolution, ResolutionDescriptionPlain, Specification, RaisedInVersion, SelectedBallot,
             ChangeCategory, Impact, Labels, CommentCount, DescriptionPlain, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@Id, @TicketKey, @Priority, @Resolution, @ResolutionDescriptionPlain, @Specification, @RaisedInVersion, @SelectedBallot,
             @ChangeCategory, @Impact, @Labels, @CommentCount, @DescriptionPlain, @HydratedAt, @HydrationStatus, @HydrationReason)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        AddNullable(command, "@Priority", row.Priority);
        AddNullable(command, "@Resolution", row.Resolution);
        AddNullable(command, "@ResolutionDescriptionPlain", row.ResolutionDescriptionPlain);
        AddNullable(command, "@Specification", row.Specification);
        AddNullable(command, "@RaisedInVersion", row.RaisedInVersion);
        AddNullable(command, "@SelectedBallot", row.SelectedBallot);
        AddNullable(command, "@ChangeCategory", row.ChangeCategory);
        AddNullable(command, "@Impact", row.Impact);
        AddNullable(command, "@Labels", row.Labels);
        AddNullable(command, "@CommentCount", row.CommentCount);
        AddNullable(command, "@DescriptionPlain", row.DescriptionPlain);
        command.Parameters.AddWithValue("@HydratedAt", Format(row.HydratedAt));
        command.Parameters.AddWithValue("@HydrationStatus", row.HydrationStatus);
        AddNullable(command, "@HydrationReason", row.HydrationReason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertJiraHydrationAsync(SqliteConnection connection, PreparedJiraHydrationRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_jira_hydration
            (Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain,
             WorkGroup, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@Id, @TicketKey, @JiraKey, @Title, @Status, @Type, @Priority, @Resolution, @ResolutionDescriptionPlain,
             @WorkGroup, @Specification, @UpdatedAt, @Url, @HydratedAt, @HydrationStatus, @HydrationReason)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        command.Parameters.AddWithValue("@JiraKey", row.JiraKey);
        AddNullable(command, "@Title", row.Title);
        AddNullable(command, "@Status", row.Status);
        AddNullable(command, "@Type", row.Type);
        AddNullable(command, "@Priority", row.Priority);
        AddNullable(command, "@Resolution", row.Resolution);
        AddNullable(command, "@ResolutionDescriptionPlain", row.ResolutionDescriptionPlain);
        AddNullable(command, "@WorkGroup", row.WorkGroup);
        AddNullable(command, "@Specification", row.Specification);
        AddNullable(command, "@UpdatedAt", row.UpdatedAt.HasValue ? Format(row.UpdatedAt.Value) : null);
        AddNullable(command, "@Url", row.Url);
        command.Parameters.AddWithValue("@HydratedAt", Format(row.HydratedAt));
        command.Parameters.AddWithValue("@HydrationStatus", row.HydrationStatus);
        AddNullable(command, "@HydrationReason", row.HydrationReason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertZulipHydrationAsync(SqliteConnection connection, PreparedZulipHydrationRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_zulip_hydration
            (Id, TicketKey, ZulipThreadId, StreamId, StreamName, Topic, MessageCount, FirstMessageAt, LastMessageAt,
             FirstMessageExcerpt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@Id, @TicketKey, @ZulipThreadId, @StreamId, @StreamName, @Topic, @MessageCount, @FirstMessageAt, @LastMessageAt,
             @FirstMessageExcerpt, @Url, @HydratedAt, @HydrationStatus, @HydrationReason)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        command.Parameters.AddWithValue("@ZulipThreadId", row.ZulipThreadId);
        AddNullable(command, "@StreamId", row.StreamId);
        AddNullable(command, "@StreamName", row.StreamName);
        AddNullable(command, "@Topic", row.Topic);
        AddNullable(command, "@MessageCount", row.MessageCount);
        AddNullable(command, "@FirstMessageAt", row.FirstMessageAt.HasValue ? Format(row.FirstMessageAt.Value) : null);
        AddNullable(command, "@LastMessageAt", row.LastMessageAt.HasValue ? Format(row.LastMessageAt.Value) : null);
        AddNullable(command, "@FirstMessageExcerpt", row.FirstMessageExcerpt);
        AddNullable(command, "@Url", row.Url);
        command.Parameters.AddWithValue("@HydratedAt", Format(row.HydratedAt));
        command.Parameters.AddWithValue("@HydrationStatus", row.HydrationStatus);
        AddNullable(command, "@HydrationReason", row.HydrationReason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertGitHubHydrationAsync(SqliteConnection connection, PreparedGitHubHydrationRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_github_hydration
            (Id, TicketKey, GitHubItemId, Owner, Repo, Number, Path, Title, State, IsPullRequest, Labels, UpdatedAt, Url,
             HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@Id, @TicketKey, @GitHubItemId, @Owner, @Repo, @Number, @Path, @Title, @State, @IsPullRequest, @Labels, @UpdatedAt, @Url,
             @HydratedAt, @HydrationStatus, @HydrationReason)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        command.Parameters.AddWithValue("@GitHubItemId", row.GitHubItemId);
        AddNullable(command, "@Owner", row.Owner);
        AddNullable(command, "@Repo", row.Repo);
        AddNullable(command, "@Number", row.Number);
        AddNullable(command, "@Path", row.Path);
        AddNullable(command, "@Title", row.Title);
        AddNullable(command, "@State", row.State);
        AddNullable(command, "@IsPullRequest", row.IsPullRequest.HasValue ? (row.IsPullRequest.Value ? 1 : 0) : (object?)null);
        AddNullable(command, "@Labels", row.Labels);
        AddNullable(command, "@UpdatedAt", row.UpdatedAt.HasValue ? Format(row.UpdatedAt.Value) : null);
        AddNullable(command, "@Url", row.Url);
        command.Parameters.AddWithValue("@HydratedAt", Format(row.HydratedAt));
        command.Parameters.AddWithValue("@HydrationStatus", row.HydrationStatus);
        AddNullable(command, "@HydrationReason", row.HydrationReason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertRepoHydrationAsync(SqliteConnection connection, PreparedRepoHydrationRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_repo_hydration
            (Id, TicketKey, Repo, Description, WorkGroup, Specification, CategoryDetail, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@Id, @TicketKey, @Repo, @Description, @WorkGroup, @Specification, @CategoryDetail, @Url, @HydratedAt, @HydrationStatus, @HydrationReason)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        command.Parameters.AddWithValue("@Repo", row.Repo);
        AddNullable(command, "@Description", row.Description);
        AddNullable(command, "@WorkGroup", row.WorkGroup);
        AddNullable(command, "@Specification", row.Specification);
        AddNullable(command, "@CategoryDetail", row.CategoryDetail);
        AddNullable(command, "@Url", row.Url);
        command.Parameters.AddWithValue("@HydratedAt", Format(row.HydratedAt));
        command.Parameters.AddWithValue("@HydrationStatus", row.HydrationStatus);
        AddNullable(command, "@HydrationReason", row.HydrationReason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertJiraXrefAsync(SqliteConnection connection, PreparedTicketJiraXrefRow row, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_ticket_jira_xref (Id, TicketKey, JiraKey, Source)
            VALUES (@Id, @TicketKey, @JiraKey, @Source)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TicketKey", row.TicketKey);
        command.Parameters.AddWithValue("@JiraKey", row.JiraKey);
        command.Parameters.AddWithValue("@Source", row.Source);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PreparedTicketHydrationRow?> ReadHydrationParentAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, Priority, Resolution, ResolutionDescriptionPlain, Specification, RaisedInVersion, SelectedBallot,
                   ChangeCategory, Impact, Labels, CommentCount, DescriptionPlain, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_ticket_hydration WHERE TicketKey = @key
            """;
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new PreparedTicketHydrationRow(
            TicketKey: reader.GetString(0),
            Priority: ReadNullableString(reader, 1),
            Resolution: ReadNullableString(reader, 2),
            ResolutionDescriptionPlain: ReadNullableString(reader, 3),
            Specification: ReadNullableString(reader, 4),
            RaisedInVersion: ReadNullableString(reader, 5),
            SelectedBallot: ReadNullableString(reader, 6),
            ChangeCategory: ReadNullableString(reader, 7),
            Impact: ReadNullableString(reader, 8),
            Labels: ReadNullableString(reader, 9),
            CommentCount: reader.IsDBNull(10) ? null : reader.GetInt32(10),
            DescriptionPlain: ReadNullableString(reader, 11),
            HydratedAt: ParseDate(reader.GetString(12)),
            HydrationStatus: reader.GetString(13),
            HydrationReason: ReadNullableString(reader, 14));
    }

    private static async Task<IReadOnlyList<PreparedJiraHydrationRow>> ReadJiraHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain,
                   WorkGroup, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_jira_hydration WHERE TicketKey = @key ORDER BY JiraKey
            """;
        command.Parameters.AddWithValue("@key", key);
        List<PreparedJiraHydrationRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PreparedJiraHydrationRow(
                TicketKey: reader.GetString(0),
                JiraKey: reader.GetString(1),
                Title: ReadNullableString(reader, 2),
                Status: ReadNullableString(reader, 3),
                Type: ReadNullableString(reader, 4),
                Priority: ReadNullableString(reader, 5),
                Resolution: ReadNullableString(reader, 6),
                ResolutionDescriptionPlain: ReadNullableString(reader, 7),
                WorkGroup: ReadNullableString(reader, 8),
                Specification: ReadNullableString(reader, 9),
                UpdatedAt: reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
                Url: ReadNullableString(reader, 11),
                HydratedAt: ParseDate(reader.GetString(12)),
                HydrationStatus: reader.GetString(13),
                HydrationReason: ReadNullableString(reader, 14)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PreparedZulipHydrationRow>> ReadZulipHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, ZulipThreadId, StreamId, StreamName, Topic, MessageCount, FirstMessageAt, LastMessageAt,
                   FirstMessageExcerpt, Url, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_zulip_hydration WHERE TicketKey = @key ORDER BY ZulipThreadId
            """;
        command.Parameters.AddWithValue("@key", key);
        List<PreparedZulipHydrationRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PreparedZulipHydrationRow(
                TicketKey: reader.GetString(0),
                ZulipThreadId: reader.GetString(1),
                StreamId: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                StreamName: ReadNullableString(reader, 3),
                Topic: ReadNullableString(reader, 4),
                MessageCount: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                FirstMessageAt: reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
                LastMessageAt: reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
                FirstMessageExcerpt: ReadNullableString(reader, 8),
                Url: ReadNullableString(reader, 9),
                HydratedAt: ParseDate(reader.GetString(10)),
                HydrationStatus: reader.GetString(11),
                HydrationReason: ReadNullableString(reader, 12)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PreparedGitHubHydrationRow>> ReadGitHubHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, GitHubItemId, Owner, Repo, Number, Path, Title, State, IsPullRequest, Labels, UpdatedAt, Url,
                   HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_github_hydration WHERE TicketKey = @key ORDER BY GitHubItemId
            """;
        command.Parameters.AddWithValue("@key", key);
        List<PreparedGitHubHydrationRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PreparedGitHubHydrationRow(
                TicketKey: reader.GetString(0),
                GitHubItemId: reader.GetString(1),
                Owner: ReadNullableString(reader, 2),
                Repo: ReadNullableString(reader, 3),
                Number: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Path: ReadNullableString(reader, 5),
                Title: ReadNullableString(reader, 6),
                State: ReadNullableString(reader, 7),
                IsPullRequest: reader.IsDBNull(8) ? null : reader.GetInt32(8) != 0,
                Labels: ReadNullableString(reader, 9),
                UpdatedAt: reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
                Url: ReadNullableString(reader, 11),
                HydratedAt: ParseDate(reader.GetString(12)),
                HydrationStatus: reader.GetString(13),
                HydrationReason: ReadNullableString(reader, 14)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PreparedRepoHydrationRow>> ReadRepoHydrationAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, Repo, Description, WorkGroup, Specification, CategoryDetail, Url, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_repo_hydration WHERE TicketKey = @key ORDER BY Repo
            """;
        command.Parameters.AddWithValue("@key", key);
        List<PreparedRepoHydrationRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PreparedRepoHydrationRow(
                TicketKey: reader.GetString(0),
                Repo: reader.GetString(1),
                Description: ReadNullableString(reader, 2),
                WorkGroup: ReadNullableString(reader, 3),
                Specification: ReadNullableString(reader, 4),
                CategoryDetail: ReadNullableString(reader, 5),
                Url: ReadNullableString(reader, 6),
                HydratedAt: ParseDate(reader.GetString(7)),
                HydrationStatus: reader.GetString(8),
                HydrationReason: ReadNullableString(reader, 9)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PreparedTicketJiraXrefRow>> ReadJiraXrefAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT TicketKey, JiraKey, Source FROM prepared_ticket_jira_xref WHERE TicketKey = @key ORDER BY Source, JiraKey";
        command.Parameters.AddWithValue("@key", key);
        List<PreparedTicketJiraXrefRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PreparedTicketJiraXrefRow(
                TicketKey: reader.GetString(0),
                JiraKey: reader.GetString(1),
                Source: reader.GetString(2)));
        }

        return rows;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void AddNullable(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    public async Task<PreparedTicketDetail?> GetPreparedTicketAsync(string key, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        PreparedTicketSummary? summary = await GetSummaryAsync(connection, key, ct);
        if (summary is null)
        {
            return null;
        }

        PreparedTicketRelatedItems relatedItems = await GetRelatedItemsAsync(connection, key, ct);
        return new PreparedTicketDetail(summary, relatedItems);
    }

    public async Task<PreparedTicketRelatedItems> GetPreparedTicketRelatedItemsAsync(string key, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await GetRelatedItemsAsync(connection, key, ct);
    }

    public async Task<IReadOnlyList<PreparedTicketSummary>> ListPreparedTicketsAsync(PreparedTicketQueryFilter filter, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        List<string> where = [];
        AddOptional(command, where, "Recommendation = @recommendation", "@recommendation", filter.Recommendation);
        if (!string.IsNullOrWhiteSpace(filter.Impact))
        {
            where.Add("(ProposalAImpact = @impact OR ProposalBImpact = @impact)");
            command.Parameters.AddWithValue("@impact", filter.Impact);
        }

        AddExists(command, where, "prepared_ticket_repos", "Repo = @repo", "@repo", filter.Repo);
        AddExists(command, where, "prepared_ticket_repos", "RepoCategory = @repoCategory", "@repoCategory", filter.RepoCategory);
        AddExists(command, where, "prepared_ticket_related_jira", "AssociatedTicketKey = @relatedJiraKey", "@relatedJiraKey", filter.RelatedJiraKey);
        AddExists(command, where, "prepared_ticket_related_github", "GitHubItemId = @githubItemId", "@githubItemId", filter.GitHubItemId);
        AddExists(command, where, "prepared_ticket_related_zulip", "ZulipThreadId = @zulipThreadId", "@zulipThreadId", filter.ZulipThreadId);
        string whereSql = where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);
        command.CommandText = $"""
            SELECT Key, RequestSummary, ProposalAImpact, ProposalBImpact, Recommendation, RecommendationJustification, SavedAt
            FROM prepared_tickets
            {whereSql}
            ORDER BY SavedAt DESC, Key ASC
            LIMIT @limit OFFSET @offset
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(filter.Limit, 1, 500));
        command.Parameters.AddWithValue("@offset", Math.Max(0, filter.Offset));
        List<PreparedTicketSummary> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadSummary(reader));
        }

        return rows;
    }

    private static async Task DeleteRowsAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        foreach (string table in new[] { "prepared_ticket_repos", "prepared_ticket_related_jira", "prepared_ticket_related_zulip", "prepared_ticket_related_github" })
        {
            await ExecuteAsync(connection, $"DELETE FROM {table} WHERE TicketKey = @key", ct, ("@key", key));
        }

        await ExecuteAsync(connection, "DELETE FROM prepared_tickets WHERE Key = @key", ct, ("@key", key));
    }

    private static async Task InsertParentAsync(SqliteConnection connection, PreparedTicketPayload payload, DateTimeOffset savedAt, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_tickets
            (Id, Key, RequestSummary, CommentSummary, LinkedTicketSummary, RelatedTicketSummary, RelatedZulipSummary, RelatedGitHubSummary, ExistingProposed,
             ProposalA, ProposalAJustification, ProposalAImpact, ProposalB, ProposalBJustification, ProposalBImpact, ProposalC, ProposalCJustification,
             Recommendation, RecommendationJustification, SavedAt)
            VALUES
            (@Id, @Key, @RequestSummary, @CommentSummary, @LinkedTicketSummary, @RelatedTicketSummary, @RelatedZulipSummary, @RelatedGitHubSummary, @ExistingProposed,
             @ProposalA, @ProposalAJustification, @ProposalAImpact, @ProposalB, @ProposalBJustification, @ProposalBImpact, @ProposalC, @ProposalCJustification,
             @Recommendation, @RecommendationJustification, @SavedAt)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@Key", payload.Key);
        command.Parameters.AddWithValue("@RequestSummary", payload.RequestSummary);
        command.Parameters.AddWithValue("@CommentSummary", payload.CommentSummary);
        command.Parameters.AddWithValue("@LinkedTicketSummary", payload.LinkedTicketSummary);
        command.Parameters.AddWithValue("@RelatedTicketSummary", payload.RelatedTicketSummary);
        command.Parameters.AddWithValue("@RelatedZulipSummary", payload.RelatedZulipSummary);
        command.Parameters.AddWithValue("@RelatedGitHubSummary", payload.RelatedGitHubSummary);
        command.Parameters.AddWithValue("@ExistingProposed", payload.ExistingProposed);
        command.Parameters.AddWithValue("@ProposalA", payload.ProposalA);
        command.Parameters.AddWithValue("@ProposalAJustification", payload.ProposalAJustification);
        command.Parameters.AddWithValue("@ProposalAImpact", payload.ProposalAImpact);
        command.Parameters.AddWithValue("@ProposalB", payload.ProposalB);
        command.Parameters.AddWithValue("@ProposalBJustification", payload.ProposalBJustification);
        command.Parameters.AddWithValue("@ProposalBImpact", payload.ProposalBImpact);
        command.Parameters.AddWithValue("@ProposalC", payload.ProposalC);
        command.Parameters.AddWithValue("@ProposalCJustification", payload.ProposalCJustification);
        command.Parameters.AddWithValue("@Recommendation", payload.Recommendation);
        command.Parameters.AddWithValue("@RecommendationJustification", payload.RecommendationJustification);
        command.Parameters.AddWithValue("@SavedAt", Format(savedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PreparedTicketSummary?> GetSummaryAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Key, RequestSummary, ProposalAImpact, ProposalBImpact, Recommendation, RecommendationJustification, SavedAt FROM prepared_tickets WHERE Key = @key";
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadSummary(reader);
        }

        return null;
    }

    private static async Task<PreparedTicketRelatedItems> GetRelatedItemsAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        List<PreparedTicketRepoItem> repos = [];
        await using (SqliteCommand command = SelectChildren(connection, "SELECT Repo, RepoCategory, Justification FROM prepared_ticket_repos WHERE TicketKey = @key ORDER BY Repo", key))
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                repos.Add(new PreparedTicketRepoItem(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        List<PreparedTicketRelatedJiraItem> jira = [];
        await using (SqliteCommand command = SelectChildren(connection, "SELECT AssociatedTicketKey, LinkType, Justification FROM prepared_ticket_related_jira WHERE TicketKey = @key ORDER BY AssociatedTicketKey", key))
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                jira.Add(new PreparedTicketRelatedJiraItem(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        List<PreparedTicketRelatedZulipItem> zulip = [];
        await using (SqliteCommand command = SelectChildren(connection, "SELECT ZulipThreadId, Justification FROM prepared_ticket_related_zulip WHERE TicketKey = @key ORDER BY ZulipThreadId", key))
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                zulip.Add(new PreparedTicketRelatedZulipItem(reader.GetString(0), reader.GetString(1)));
            }
        }

        List<PreparedTicketRelatedGitHubItem> github = [];
        await using (SqliteCommand command = SelectChildren(connection, "SELECT GitHubItemId, Justification FROM prepared_ticket_related_github WHERE TicketKey = @key ORDER BY GitHubItemId", key))
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                github.Add(new PreparedTicketRelatedGitHubItem(reader.GetString(0), reader.GetString(1)));
            }
        }

        return new PreparedTicketRelatedItems(repos, jira, zulip, github);
    }

    private static SqliteCommand SelectChildren(SqliteConnection connection, string sql, string key)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@key", key);
        return command;
    }

    private static PreparedTicketSummary ReadSummary(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static void AddOptional(SqliteCommand command, List<string> where, string expression, string parameter, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            where.Add(expression);
            command.Parameters.AddWithValue(parameter, value);
        }
    }

    private static void AddExists(SqliteCommand command, List<string> where, string table, string expression, string parameter, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            where.Add($"EXISTS (SELECT 1 FROM {table} child WHERE child.TicketKey = prepared_tickets.Key AND {expression})");
            command.Parameters.AddWithValue(parameter, value);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteRawAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
