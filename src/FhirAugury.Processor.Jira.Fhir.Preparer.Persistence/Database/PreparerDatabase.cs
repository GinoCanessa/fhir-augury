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
        PreparedTicketTopicRecord.CreateTable(connection);
        PreparedTicketTopicGroupRecord.CreateTable(connection);
        PreparedTicketTopicMemberRecord.CreateTable(connection);
        EnsureHydrationCompositeUniqueIndexes(connection);
        EnsureGroupingCompositeUniqueIndexes(connection);
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

    /// <summary>
    /// Per-partition composite-unique indexes for the grouping tables. As with
    /// <see cref="EnsureHydrationCompositeUniqueIndexes"/>, CsLightDbGen's
    /// <c>[LdgSQLiteIndex]</c> attribute does not support <c>Unique</c>, so the
    /// uniqueness contract is enforced here via follow-on
    /// <c>CREATE UNIQUE INDEX IF NOT EXISTS</c> statements. The
    /// "each ticket appears in at most one Topic within a partition" invariant
    /// cannot be a single SQLite UNIQUE (the partition triple lives on
    /// <c>prepared_ticket_topics</c>, members live on <c>prepared_ticket_topic_members</c>);
    /// it is enforced in C# inside <c>SaveGroupingAsync</c> + the payload validator.
    /// </summary>
    private static void EnsureGroupingCompositeUniqueIndexes(SqliteConnection connection)
    {
        string[] statements =
        [
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_topics_partition_short ON prepared_ticket_topics(WorkGroupClean, Specification, Type, ShortDescription);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_topic_groups_topic_first ON prepared_ticket_topic_groups(TopicRowId, FirstTicketKey);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_prepared_ticket_topic_members_topic_ticket ON prepared_ticket_topic_members(TopicRowId, TicketKey);",
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

    /// <summary>
    /// Atomically replaces the grouping rows for the
    /// <c>(WorkGroupClean, Specification, Type)</c> partition described by
    /// <paramref name="payload"/>. Validates the payload first, then verifies
    /// that every referenced ticket key exists in <c>prepared_tickets</c>; if
    /// any are missing, throws <see cref="ArgumentException"/> before any
    /// mutation. Inside a single <c>BEGIN IMMEDIATE</c> transaction the
    /// partition's existing topic / group / member rows are deleted and the
    /// new ones inserted.
    /// </summary>
    public async Task<PreparedTicketGroupingSaveResult> SaveGroupingAsync(PreparedTicketGroupingPayload payload, CancellationToken ct = default)
    {
        PreparedTicketGroupingPayloadValidator.ThrowIfInvalid(payload);
        DateTimeOffset savedAt = payload.SavedAt ?? DateTimeOffset.UtcNow;

        await using SqliteConnection connection = OpenConnection();

        IReadOnlyList<string> referencedKeys = CollectReferencedTicketKeys(payload);
        if (referencedKeys.Count > 0)
        {
            IReadOnlyList<string> missing = await FindMissingPreparedTicketKeysAsync(connection, referencedKeys, ct);
            if (missing.Count > 0)
            {
                throw new ArgumentException($"Unknown prepared ticket keys: {string.Join(", ", missing)}.", nameof(payload));
            }
        }

        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await ExecuteAsync(
                connection,
                """
                DELETE FROM prepared_ticket_topic_members
                WHERE TopicRowId IN (
                    SELECT RowId FROM prepared_ticket_topics
                    WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
                )
                """,
                ct,
                ("@wg", payload.WorkGroupClean),
                ("@spec", payload.Specification),
                ("@type", payload.Type));

            await ExecuteAsync(
                connection,
                """
                DELETE FROM prepared_ticket_topic_groups
                WHERE TopicRowId IN (
                    SELECT RowId FROM prepared_ticket_topics
                    WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
                )
                """,
                ct,
                ("@wg", payload.WorkGroupClean),
                ("@spec", payload.Specification),
                ("@type", payload.Type));

            await ExecuteAsync(
                connection,
                "DELETE FROM prepared_ticket_topics WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type",
                ct,
                ("@wg", payload.WorkGroupClean),
                ("@spec", payload.Specification),
                ("@type", payload.Type));

            int topicRows = 0;
            int topicGroupRows = 0;
            int memberRows = 0;
            foreach (PreparedTicketTopicPayload topic in payload.Topics)
            {
                int topicRowId = await InsertTopicAsync(connection, payload, topic, savedAt, ct);
                topicRows++;
                for (int groupIndex = 0; groupIndex < topic.LinkedTicketGroups.Count; groupIndex++)
                {
                    PreparedTicketTopicGroupPayload group = topic.LinkedTicketGroups[groupIndex];
                    int groupRowId = await InsertTopicGroupAsync(connection, topicRowId, groupIndex, group, savedAt, ct);
                    topicGroupRows++;
                    foreach (PreparedTicketTopicGroupMemberPayload member in group.Members)
                    {
                        await InsertTopicMemberAsync(connection, topicRowId, groupRowId, member.TicketKey, member.Order, ct);
                        memberRows++;
                    }
                }

                for (int remainingIndex = 0; remainingIndex < topic.RemainingTicketKeys.Count; remainingIndex++)
                {
                    string remainingKey = topic.RemainingTicketKeys[remainingIndex];
                    await InsertTopicMemberAsync(connection, topicRowId, topicGroupRowId: null, remainingKey, remainingIndex, ct);
                    memberRows++;
                }
            }

            await ExecuteRawAsync(connection, "COMMIT", ct);
            return new PreparedTicketGroupingSaveResult(
                payload.WorkGroupClean,
                payload.Specification,
                payload.Type,
                topicRows,
                topicGroupRows,
                memberRows);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Reads the full grouping shape for a single
    /// <c>(WorkGroupClean, Specification, Type)</c> partition. Returns
    /// <c>null</c> only when the partition has no topic rows <em>and</em> no
    /// prep tickets attributable to it via the
    /// <c>prepared_jira_hydration</c> self-join. Topics are sorted in C#:
    /// hinted topics first (ascending by hint); unhinted topics second,
    /// sorted descending by total member count and then ascending by
    /// short description (ordinal-ignore-case).
    /// </summary>
    /// <remarks>
    /// The <c>WorkGroupClean</c> → <c>WorkGroup</c> display-name mapping
    /// is convention-bound: <c>REPLACE(j.WorkGroup, ' ', '') = @wg</c>,
    /// matching the <c>nameClean</c> convention used by
    /// <c>orchestrate-prep</c>. Tickets with no self-row in
    /// <c>prepared_jira_hydration</c> (<c>JiraKey = TicketKey</c>) are
    /// counted in <c>UnattributedTicketCount</c> but are intentionally
    /// excluded from <c>IndividualTicketKeys</c>.
    /// </remarks>
    public async Task<PreparedTicketGroupingPartition?> GetGroupingAsync(string workGroupClean, string specification, string type, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        PreparedTicketGroupingPartition? partition = await BuildPartitionAsync(connection, workGroupClean, workGroupDisplay: null, specification, type, ct);
        if (partition is null)
        {
            return null;
        }

        return partition;
    }

    /// <summary>
    /// Workgroup-wide aggregate. Discovers all <c>(Specification, Type)</c>
    /// partitions that either have topic rows or have at least one
    /// hydrated prep ticket attributable to the workgroup. Returns
    /// <c>null</c> when neither source produces a row.
    /// </summary>
    public async Task<PreparedTicketGroupingWorkGroupView?> GetWorkGroupGroupingsAsync(string workGroupClean, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        IReadOnlyList<(string Specification, string Type)> partitions = await DiscoverWorkGroupPartitionsAsync(connection, workGroupClean, ct);
        string? workGroupDisplay = await ResolveWorkGroupDisplayAsync(connection, workGroupClean, ct);
        if (partitions.Count == 0 && workGroupDisplay is null)
        {
            return null;
        }

        string displayName = workGroupDisplay ?? workGroupClean;
        List<PreparedTicketGroupingPartition> built = [];
        foreach ((string specification, string type) in partitions)
        {
            PreparedTicketGroupingPartition? partition = await BuildPartitionAsync(connection, workGroupClean, displayName, specification, type, ct);
            if (partition is not null)
            {
                built.Add(partition);
            }
        }

        return new PreparedTicketGroupingWorkGroupView(workGroupClean, displayName, built);
    }

    /// <summary>
    /// Returns the workgroup display name resolved from
    /// <c>prepared_ticket_topics.WorkGroupDisplay</c> first, falling
    /// through to the most-recent <c>prepared_jira_hydration.WorkGroup</c>
    /// self-row, mirroring the heading logic used by
    /// <see cref="GetWorkGroupGroupingsAsync"/>. Returns <c>null</c> when
    /// neither source carries a non-empty display string for
    /// <paramref name="workGroupClean"/>. Consumed by
    /// <c>PreparedTicketHydrationController</c>.
    /// </summary>
    public async Task<string?> ResolveWorkGroupDisplayNameAsync(string workGroupClean, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        return await ResolveWorkGroupDisplayAsync(connection, workGroupClean, ct);
    }

    /// <summary>
    /// Returns the per-ticket analytic / clustering projection used by
    /// the <c>topic-groupings</c> skill. For every <c>prepared_jira_hydration</c>
    /// self-row (<c>JiraKey = TicketKey</c>) whose <c>WorkGroup</c>
    /// matches <paramref name="workGroupClean"/> under the
    /// <c>REPLACE(IFNULL(WorkGroup, ''), ' ', '')</c> convention used
    /// by the rest of the preparer, this method:
    /// <list type="bullet">
    ///   <item>Pulls the partition / display fields
    ///   (<c>Title</c>, <c>Status</c>, <c>Specification</c>, <c>Type</c>)
    ///   from the hydration row.</item>
    ///   <item>Left-joins <c>prepared_tickets</c> to pull the
    ///   <c>RequestSummary</c> / <c>CommentSummary</c> /
    ///   <c>LinkedTicketSummary</c> / <c>RelatedTicketSummary</c> /
    ///   <c>RelatedZulipSummary</c> / <c>RelatedGitHubSummary</c>
    ///   analytic text fields. Tickets without a
    ///   <c>prepared_tickets</c> row are still emitted with empty
    ///   summaries and <c>HasPreparedTicket = false</c> so the
    ///   clustering skill can drop them before building any payload.</item>
    ///   <item>Pulls every <c>prepared_ticket_related_jira</c> row for
    ///   those keys to populate the per-ticket <c>Links</c> list.</item>
    /// </list>
    /// Returns <c>null</c> when the workgroup has zero hydration self-rows.
    /// Read-only — does not touch the hydration sweeper or the
    /// grouping tables.
    /// </summary>
    public async Task<PreparedTicketClusteringSignals?> GetClusteringSignalsAsync(string workGroupClean, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();

        List<PreparedTicketClusteringSignal> tickets = [];
        Dictionary<string, List<PreparedTicketClusteringLink>> linksByTicket = new(StringComparer.Ordinal);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT j.TicketKey,
                       j.Title,
                       j.Status,
                       j.Specification,
                       j.Type,
                       t.RequestSummary,
                       t.CommentSummary,
                       t.LinkedTicketSummary,
                       t.RelatedTicketSummary,
                       t.RelatedZulipSummary,
                       t.RelatedGitHubSummary,
                       CASE WHEN t.Key IS NULL THEN 0 ELSE 1 END AS HasPreparedTicket
                FROM prepared_jira_hydration j
                LEFT JOIN prepared_tickets t ON t.Key = j.TicketKey
                WHERE j.JiraKey = j.TicketKey
                  AND REPLACE(IFNULL(j.WorkGroup, ''), ' ', '') = @wg
                ORDER BY j.TicketKey ASC
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string ticketKey = reader.GetString(0);
                List<PreparedTicketClusteringLink> ticketLinks = [];
                linksByTicket[ticketKey] = ticketLinks;
                tickets.Add(new PreparedTicketClusteringSignal(
                    TicketKey: ticketKey,
                    Title: ReadNullableString(reader, 1),
                    Status: ReadNullableString(reader, 2),
                    Specification: ReadNullableString(reader, 3),
                    Type: ReadNullableString(reader, 4),
                    RequestSummary: ReadNullableString(reader, 5) ?? string.Empty,
                    CommentSummary: ReadNullableString(reader, 6) ?? string.Empty,
                    LinkedTicketSummary: ReadNullableString(reader, 7) ?? string.Empty,
                    RelatedTicketSummary: ReadNullableString(reader, 8) ?? string.Empty,
                    RelatedZulipSummary: ReadNullableString(reader, 9) ?? string.Empty,
                    RelatedGitHubSummary: ReadNullableString(reader, 10) ?? string.Empty,
                    HasPreparedTicket: reader.GetInt32(11) == 1,
                    Links: ticketLinks));
            }
        }

        if (tickets.Count == 0)
        {
            return null;
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.TicketKey, r.AssociatedTicketKey, r.LinkType, r.Justification
                FROM prepared_ticket_related_jira r
                INNER JOIN prepared_jira_hydration j
                  ON j.TicketKey = r.TicketKey AND j.JiraKey = j.TicketKey
                WHERE REPLACE(IFNULL(j.WorkGroup, ''), ' ', '') = @wg
                ORDER BY r.TicketKey ASC, r.AssociatedTicketKey ASC
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string ticketKey = reader.GetString(0);
                if (!linksByTicket.TryGetValue(ticketKey, out List<PreparedTicketClusteringLink>? bucket))
                {
                    continue;
                }

                bucket.Add(new PreparedTicketClusteringLink(
                    AssociatedTicketKey: reader.GetString(1),
                    LinkType: reader.GetString(2),
                    Justification: reader.GetString(3)));
            }
        }

        string? workGroupDisplay = await ResolveWorkGroupDisplayAsync(connection, workGroupClean, ct);
        return new PreparedTicketClusteringSignals(workGroupClean, workGroupDisplay, tickets);
    }

    /// <summary>
    /// Lists the per-ticket display projection over
    /// <c>prepared_jira_hydration</c> self-rows
    /// (<c>JiraKey = TicketKey</c>) whose <c>WorkGroup</c> matches
    /// <paramref name="workGroupClean"/> under the
    /// <c>REPLACE(IFNULL(WorkGroup, ''), ' ', '')</c> convention used by
    /// the grouping query. Rows are ordered by <c>TicketKey</c> ascending.
    /// Returns an empty list (never throws) when no rows match.
    /// Consumed by the <c>index-prepared-db</c> skill.
    /// </summary>
    public async Task<IReadOnlyList<PreparedJiraHydrationRow>> ListJiraHydrationDisplayForWorkGroupAsync(
        string workGroupClean,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain,
                   WorkGroup, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_jira_hydration
            WHERE JiraKey = TicketKey
              AND REPLACE(IFNULL(WorkGroup, ''), ' ', '') = @wg
            ORDER BY TicketKey ASC
            """;
        command.Parameters.AddWithValue("@wg", workGroupClean);
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

    /// <summary>
    /// Deletes the partition's topic / group / member rows in a single
    /// transaction. Always succeeds (deleting an empty partition is a
    /// no-op) — matches the source's "regenerate, do not update" stance.
    /// </summary>
    public async Task DeleteGroupingAsync(string workGroupClean, string specification, string type, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            await ExecuteAsync(
                connection,
                """
                DELETE FROM prepared_ticket_topic_members
                WHERE TopicRowId IN (
                    SELECT RowId FROM prepared_ticket_topics
                    WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
                )
                """,
                ct,
                ("@wg", workGroupClean),
                ("@spec", specification),
                ("@type", type));

            await ExecuteAsync(
                connection,
                """
                DELETE FROM prepared_ticket_topic_groups
                WHERE TopicRowId IN (
                    SELECT RowId FROM prepared_ticket_topics
                    WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
                )
                """,
                ct,
                ("@wg", workGroupClean),
                ("@spec", specification),
                ("@type", type));

            await ExecuteAsync(
                connection,
                "DELETE FROM prepared_ticket_topics WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type",
                ct,
                ("@wg", workGroupClean),
                ("@spec", specification),
                ("@type", type));

            await ExecuteRawAsync(connection, "COMMIT", ct);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    private static IReadOnlyList<string> CollectReferencedTicketKeys(PreparedTicketGroupingPayload payload)
    {
        HashSet<string> set = new(StringComparer.Ordinal);
        foreach (PreparedTicketTopicPayload topic in payload.Topics)
        {
            foreach (PreparedTicketTopicGroupPayload group in topic.LinkedTicketGroups)
            {
                foreach (PreparedTicketTopicGroupMemberPayload member in group.Members)
                {
                    set.Add(member.TicketKey);
                }
            }

            foreach (string remaining in topic.RemainingTicketKeys)
            {
                set.Add(remaining);
            }
        }

        return [.. set];
    }

    private static async Task<IReadOnlyList<string>> FindMissingPreparedTicketKeysAsync(SqliteConnection connection, IReadOnlyList<string> keys, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        List<string> parameterNames = new(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            string name = $"@k{i}";
            parameterNames.Add(name);
            command.Parameters.AddWithValue(name, keys[i]);
        }

        command.CommandText = $"SELECT Key FROM prepared_tickets WHERE Key IN ({string.Join(", ", parameterNames)})";
        HashSet<string> found = new(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add(reader.GetString(0));
        }

        List<string> missing = [];
        foreach (string key in keys)
        {
            if (!found.Contains(key))
            {
                missing.Add(key);
            }
        }

        missing.Sort(StringComparer.Ordinal);
        return missing;
    }

    private static async Task<int> InsertTopicAsync(SqliteConnection connection, PreparedTicketGroupingPayload payload, PreparedTicketTopicPayload topic, DateTimeOffset savedAt, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_ticket_topics
            (Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, ShortDescription, LongerDescription, RenderOrderHint, SavedAt)
            VALUES
            (@Id, @WorkGroupClean, @WorkGroupDisplay, @Specification, @Type, @ShortDescription, @LongerDescription, @RenderOrderHint, @SavedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@WorkGroupClean", payload.WorkGroupClean);
        command.Parameters.AddWithValue("@WorkGroupDisplay", payload.WorkGroupDisplay);
        command.Parameters.AddWithValue("@Specification", payload.Specification);
        command.Parameters.AddWithValue("@Type", payload.Type);
        command.Parameters.AddWithValue("@ShortDescription", topic.ShortDescription);
        command.Parameters.AddWithValue("@LongerDescription", topic.LongerDescription);
        command.Parameters.AddWithValue("@RenderOrderHint", (object?)topic.RenderOrderHint ?? DBNull.Value);
        command.Parameters.AddWithValue("@SavedAt", Format(savedAt));
        object? scalar = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<int> InsertTopicGroupAsync(SqliteConnection connection, int topicRowId, int orderInTopic, PreparedTicketTopicGroupPayload group, DateTimeOffset savedAt, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_ticket_topic_groups
            (Id, TopicRowId, FirstTicketKey, Rationale, OrderInTopic, SavedAt)
            VALUES
            (@Id, @TopicRowId, @FirstTicketKey, @Rationale, @OrderInTopic, @SavedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TopicRowId", topicRowId);
        command.Parameters.AddWithValue("@FirstTicketKey", group.FirstTicketKey);
        command.Parameters.AddWithValue("@Rationale", group.Rationale);
        command.Parameters.AddWithValue("@OrderInTopic", orderInTopic);
        command.Parameters.AddWithValue("@SavedAt", Format(savedAt));
        object? scalar = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task InsertTopicMemberAsync(SqliteConnection connection, int topicRowId, int? topicGroupRowId, string ticketKey, int orderInContainer, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_ticket_topic_members
            (Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer)
            VALUES
            (@Id, @TopicRowId, @TopicGroupRowId, @TicketKey, @OrderInContainer)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@TopicRowId", topicRowId);
        command.Parameters.AddWithValue("@TopicGroupRowId", (object?)topicGroupRowId ?? DBNull.Value);
        command.Parameters.AddWithValue("@TicketKey", ticketKey);
        command.Parameters.AddWithValue("@OrderInContainer", orderInContainer);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PreparedTicketGroupingPartition?> BuildPartitionAsync(SqliteConnection connection, string workGroupClean, string? workGroupDisplay, string specification, string type, CancellationToken ct)
    {
        List<TopicRow> topicRows = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT RowId, Id, WorkGroupDisplay, ShortDescription, LongerDescription, RenderOrderHint, SavedAt
                FROM prepared_ticket_topics
                WHERE WorkGroupClean = @wg AND Specification = @spec AND Type = @type
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            command.Parameters.AddWithValue("@spec", specification);
            command.Parameters.AddWithValue("@type", type);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                topicRows.Add(new TopicRow(
                    RowId: reader.GetInt32(0),
                    Id: reader.GetString(1),
                    WorkGroupDisplay: reader.GetString(2),
                    ShortDescription: reader.GetString(3),
                    LongerDescription: reader.GetString(4),
                    RenderOrderHint: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    SavedAt: DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }

        List<string> individualKeys = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.Key
                FROM prepared_tickets t
                INNER JOIN prepared_jira_hydration j
                  ON j.TicketKey = t.Key AND j.JiraKey = t.Key
                WHERE REPLACE(IFNULL(j.WorkGroup, ''), ' ', '') = @wg
                  AND IFNULL(j.Type, '') = @type
                  AND IFNULL(j.Specification, 'Unspecified') = @spec
                  AND NOT EXISTS (
                      SELECT 1
                      FROM prepared_ticket_topic_members m
                      INNER JOIN prepared_ticket_topics topic ON topic.RowId = m.TopicRowId
                      WHERE m.TicketKey = t.Key
                        AND topic.WorkGroupClean = @wg
                        AND topic.Specification = @spec
                        AND topic.Type = @type
                  )
                ORDER BY t.Key
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            command.Parameters.AddWithValue("@spec", specification);
            command.Parameters.AddWithValue("@type", type);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                individualKeys.Add(reader.GetString(0));
            }
        }

        int unattributedCount;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM prepared_tickets t
                WHERE NOT EXISTS (
                    SELECT 1 FROM prepared_jira_hydration j
                    WHERE j.TicketKey = t.Key AND j.JiraKey = t.Key
                )
                """;
            object? scalar = await command.ExecuteScalarAsync(ct);
            unattributedCount = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
        }

        if (topicRows.Count == 0 && individualKeys.Count == 0)
        {
            return null;
        }

        List<PreparedTicketTopic> topics = [];
        DateTimeOffset? lastSavedAt = null;
        foreach (TopicRow topicRow in topicRows)
        {
            (IReadOnlyList<PreparedTicketTopicGroup> groups, IReadOnlyList<string> remaining) = await LoadTopicContentsAsync(connection, topicRow.RowId, ct);
            topics.Add(new PreparedTicketTopic(
                topicRow.Id,
                topicRow.ShortDescription,
                topicRow.LongerDescription,
                topicRow.RenderOrderHint,
                topicRow.SavedAt,
                groups,
                remaining));
            if (lastSavedAt is null || topicRow.SavedAt > lastSavedAt.Value)
            {
                lastSavedAt = topicRow.SavedAt;
            }
        }

        topics.Sort((a, b) =>
        {
            bool aHinted = a.RenderOrderHint.HasValue;
            bool bHinted = b.RenderOrderHint.HasValue;
            if (aHinted && bHinted)
            {
                int byHint = a.RenderOrderHint!.Value.CompareTo(b.RenderOrderHint!.Value);
                if (byHint != 0)
                {
                    return byHint;
                }

                return string.Compare(a.ShortDescription, b.ShortDescription, StringComparison.OrdinalIgnoreCase);
            }

            if (aHinted != bHinted)
            {
                return aHinted ? -1 : 1;
            }

            int aTotal = TopicTotalCount(a);
            int bTotal = TopicTotalCount(b);
            int byCount = bTotal.CompareTo(aTotal);
            if (byCount != 0)
            {
                return byCount;
            }

            return string.Compare(a.ShortDescription, b.ShortDescription, StringComparison.OrdinalIgnoreCase);
        });

        string resolvedDisplay = workGroupDisplay
            ?? (topicRows.Count > 0 ? topicRows[0].WorkGroupDisplay : workGroupClean);

        return new PreparedTicketGroupingPartition(
            workGroupClean,
            resolvedDisplay,
            specification,
            type,
            topics,
            individualKeys,
            unattributedCount,
            lastSavedAt);
    }

    private static int TopicTotalCount(PreparedTicketTopic topic)
    {
        int total = topic.RemainingTicketKeys.Count;
        foreach (PreparedTicketTopicGroup group in topic.LinkedTicketGroups)
        {
            total += group.Members.Count;
        }

        return total;
    }

    private static async Task<(IReadOnlyList<PreparedTicketTopicGroup> Groups, IReadOnlyList<string> Remaining)> LoadTopicContentsAsync(SqliteConnection connection, int topicRowId, CancellationToken ct)
    {
        List<TopicGroupRow> groupRows = [];
        Dictionary<int, List<PreparedTicketTopicGroupMember>> membersByGroupRowId = [];

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT RowId, Id, FirstTicketKey, Rationale, OrderInTopic, SavedAt
                FROM prepared_ticket_topic_groups
                WHERE TopicRowId = @topic
                ORDER BY OrderInTopic
                """;
            command.Parameters.AddWithValue("@topic", topicRowId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                int rowId = reader.GetInt32(0);
                List<PreparedTicketTopicGroupMember> members = [];
                membersByGroupRowId[rowId] = members;
                groupRows.Add(new TopicGroupRow(
                    RowId: rowId,
                    Id: reader.GetString(1),
                    FirstTicketKey: reader.GetString(2),
                    Rationale: reader.GetString(3),
                    OrderInTopic: reader.GetInt32(4),
                    SavedAt: DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Members: members));
            }
        }

        List<string> remaining = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT TopicGroupRowId, TicketKey, OrderInContainer
                FROM prepared_ticket_topic_members
                WHERE TopicRowId = @topic
                ORDER BY (TopicGroupRowId IS NULL), TopicGroupRowId, OrderInContainer
                """;
            command.Parameters.AddWithValue("@topic", topicRowId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string ticketKey = reader.GetString(1);
                int order = reader.GetInt32(2);
                if (reader.IsDBNull(0))
                {
                    remaining.Add(ticketKey);
                    continue;
                }

                int groupRowId = reader.GetInt32(0);
                if (membersByGroupRowId.TryGetValue(groupRowId, out List<PreparedTicketTopicGroupMember>? bucket))
                {
                    bucket.Add(new PreparedTicketTopicGroupMember(ticketKey, order));
                }
            }
        }

        List<PreparedTicketTopicGroup> groups = new(groupRows.Count);
        foreach (TopicGroupRow row in groupRows)
        {
            groups.Add(new PreparedTicketTopicGroup(
                row.Id,
                row.FirstTicketKey,
                row.Rationale,
                row.OrderInTopic,
                row.SavedAt,
                row.Members));
        }

        return (groups, remaining);
    }

    private static async Task<IReadOnlyList<(string Specification, string Type)>> DiscoverWorkGroupPartitionsAsync(SqliteConnection connection, string workGroupClean, CancellationToken ct)
    {
        HashSet<(string, string)> seen = [];
        List<(string, string)> ordered = [];

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT Specification, Type
                FROM prepared_ticket_topics
                WHERE WorkGroupClean = @wg
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                (string, string) tuple = (reader.GetString(0), reader.GetString(1));
                if (seen.Add(tuple))
                {
                    ordered.Add(tuple);
                }
            }
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT IFNULL(j.Specification, 'Unspecified'), IFNULL(j.Type, '')
                FROM prepared_jira_hydration j
                WHERE j.JiraKey = j.TicketKey
                  AND REPLACE(IFNULL(j.WorkGroup, ''), ' ', '') = @wg
                  AND j.Type IS NOT NULL AND j.Type <> ''
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                (string, string) tuple = (reader.GetString(0), reader.GetString(1));
                if (seen.Add(tuple))
                {
                    ordered.Add(tuple);
                }
            }
        }

        ordered.Sort((a, b) =>
        {
            int bySpec = string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase);
            if (bySpec != 0)
            {
                return bySpec;
            }

            return string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        });
        return ordered;
    }

    private static async Task<string?> ResolveWorkGroupDisplayAsync(SqliteConnection connection, string workGroupClean, CancellationToken ct)
    {
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT WorkGroupDisplay FROM prepared_ticket_topics
                WHERE WorkGroupClean = @wg
                ORDER BY SavedAt DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            object? scalar = await command.ExecuteScalarAsync(ct);
            if (scalar is string display && !string.IsNullOrWhiteSpace(display))
            {
                return display;
            }
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT j.WorkGroup FROM prepared_jira_hydration j
                WHERE j.JiraKey = j.TicketKey
                  AND REPLACE(IFNULL(j.WorkGroup, ''), ' ', '') = @wg
                ORDER BY j.HydratedAt DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("@wg", workGroupClean);
            object? scalar = await command.ExecuteScalarAsync(ct);
            if (scalar is string display && !string.IsNullOrWhiteSpace(display))
            {
                return display;
            }
        }

        return null;
    }

    private readonly record struct TopicRow(
        int RowId,
        string Id,
        string WorkGroupDisplay,
        string ShortDescription,
        string LongerDescription,
        int? RenderOrderHint,
        DateTimeOffset SavedAt);

    private sealed record TopicGroupRow(
        int RowId,
        string Id,
        string FirstTicketKey,
        string Rationale,
        int OrderInTopic,
        DateTimeOffset SavedAt,
        List<PreparedTicketTopicGroupMember> Members);


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

        // Grouping cascade: a per-ticket overwrite removes the ticket's
        // grouping-member rows so it cannot remain pinned to a Topic / Linked
        // Ticket Group that no longer represents its current state. Topic and
        // group rows are intentionally left in place — source guidance is
        // "re-runs replace, do not merge"; the next per-partition PUT will
        // overwrite stale topic rows wholesale.
        await ExecuteAsync(connection, "DELETE FROM prepared_ticket_topic_members WHERE TicketKey = @key", ct, ("@key", key));

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
