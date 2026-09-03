using System.Globalization;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketMdToDb.Import;

public sealed record PersistedRowIdentity(
    string Table,
    long RowId,
    string Id,
    string TicketKey,
    string NaturalKey);

public sealed record PersistedPreparedTicket(
    PersistedRowIdentity Identity,
    PreparedTicketPayload Payload);

public sealed record PersistedHydrationParent(
    PersistedRowIdentity Identity,
    HydrationTicketRow Row);

public sealed record PersistedHydrationJira(
    PersistedRowIdentity Identity,
    HydrationJiraRow Row,
    string? WorkGroupClean);

public sealed record PersistedHydrationZulip(
    PersistedRowIdentity Identity,
    HydrationZulipRow Row);

public sealed record PersistedHydrationGitHub(
    PersistedRowIdentity Identity,
    HydrationGitHubRow Row);

public sealed record PersistedHydrationRepo(
    PersistedRowIdentity Identity,
    HydrationRepoRow Row);

public sealed record PersistedHydrationJiraXref(
    PersistedRowIdentity Identity,
    HydrationJiraXrefRow Row);

public sealed record PersistedDatabaseState(
    IReadOnlyDictionary<string, PersistedPreparedTicket> PreparedTickets,
    IReadOnlyList<PersistedHydrationParent> HydrationParents,
    IReadOnlyList<PersistedHydrationJira> HydrationJiraRows,
    IReadOnlyList<PersistedHydrationZulip> HydrationZulipRows,
    IReadOnlyList<PersistedHydrationGitHub> HydrationGitHubRows,
    IReadOnlyList<PersistedHydrationRepo> HydrationRepoRows,
    IReadOnlyList<PersistedHydrationJiraXref> HydrationJiraXrefRows,
    IReadOnlyList<JiraProcessingSourceTicketRecord> SourceTickets,
    IReadOnlyList<PersistedRowIdentity> Identities,
    IReadOnlyList<string> OrphanPreparedChildren);

public static class PersistedPreparedTicketReader
{
    public static async Task<PersistedDatabaseState> ReadAsync(
        string databasePath,
        CancellationToken ct = default)
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(ct);

        Dictionary<string, PersistedPreparedTicket> tickets =
            await ReadPreparedParentsAsync(connection, ct);
        List<PersistedRowIdentity> identities =
            tickets.Values.Select(ticket => ticket.Identity).ToList();
        List<string> orphanChildren = [];
        await ReadPreparedChildrenAsync(
            connection,
            tickets,
            identities,
            orphanChildren,
            ct);

        List<PersistedHydrationParent> parents =
            await ReadHydrationParentsAsync(connection, identities, ct);
        List<PersistedHydrationJira> jira =
            await ReadHydrationJiraAsync(connection, identities, ct);
        List<PersistedHydrationZulip> zulip =
            await ReadHydrationZulipAsync(connection, identities, ct);
        List<PersistedHydrationGitHub> github =
            await ReadHydrationGitHubAsync(connection, identities, ct);
        List<PersistedHydrationRepo> repos =
            await ReadHydrationReposAsync(connection, identities, ct);
        List<PersistedHydrationJiraXref> xrefs =
            await ReadHydrationXrefsAsync(connection, identities, ct);
        List<JiraProcessingSourceTicketRecord> sourceTickets =
            await ReadSourceTicketsAsync(connection, identities, ct);

        return new PersistedDatabaseState(
            tickets,
            parents,
            jira,
            zulip,
            github,
            repos,
            xrefs,
            sourceTickets,
            identities,
            orphanChildren);
    }

    private static async Task<Dictionary<string, PersistedPreparedTicket>> ReadPreparedParentsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT RowId, Id, Key, RequestSummary, CommentSummary, LinkedTicketSummary,
                   RelatedTicketSummary, RelatedZulipSummary, RelatedGitHubSummary,
                   ExistingProposed, ProposalA, ProposalAJustification, ProposalAImpact,
                   ProposalB, ProposalBJustification, ProposalBImpact,
                   ProposalC, ProposalCJustification, Recommendation,
                   RecommendationJustification, SavedAt
            FROM prepared_tickets
            ORDER BY Key
            """;
        Dictionary<string, PersistedPreparedTicket> rows = new(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string key = reader.GetString(2);
            PreparedTicketPayload payload = new()
            {
                Key = key,
                RequestSummary = reader.GetString(3),
                CommentSummary = reader.GetString(4),
                LinkedTicketSummary = reader.GetString(5),
                RelatedTicketSummary = reader.GetString(6),
                RelatedZulipSummary = reader.GetString(7),
                RelatedGitHubSummary = reader.GetString(8),
                ExistingProposed = reader.GetString(9),
                ProposalA = reader.GetString(10),
                ProposalAJustification = reader.GetString(11),
                ProposalAImpact = reader.GetString(12),
                ProposalB = reader.GetString(13),
                ProposalBJustification = reader.GetString(14),
                ProposalBImpact = reader.GetString(15),
                ProposalC = reader.GetString(16),
                ProposalCJustification = reader.GetString(17),
                Recommendation = reader.GetString(18),
                RecommendationJustification = reader.GetString(19),
                SavedAt = ParseRequiredDate(reader.GetString(20)),
            };
            PersistedRowIdentity identity = Identity(
                "prepared_tickets",
                reader,
                key,
                key);
            rows.Add(key, new PersistedPreparedTicket(identity, payload));
        }

        return rows;
    }

    private static async Task ReadPreparedChildrenAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, PersistedPreparedTicket> tickets,
        ICollection<PersistedRowIdentity> identities,
        ICollection<string> orphanChildren,
        CancellationToken ct)
    {
        await ReadChildTableAsync(
            connection,
            """
            SELECT RowId, Id, TicketKey, Repo, RepoCategory, Justification
            FROM prepared_ticket_repos
            ORDER BY TicketKey, Repo, RepoCategory, Justification
            """,
            "prepared_ticket_repos",
            (reader, payload) => payload.Repos.Add(new PreparedTicketRepoPayload
            {
                Repo = reader.GetString(3),
                RepoCategory = reader.GetString(4),
                Justification = reader.GetString(5),
            }),
            reader => $"{reader.GetString(3)}\u001f{reader.GetString(4)}",
            tickets,
            identities,
            orphanChildren,
            ct);
        await ReadChildTableAsync(
            connection,
            """
            SELECT RowId, Id, TicketKey, AssociatedTicketKey, LinkType, Justification
            FROM prepared_ticket_related_jira
            ORDER BY TicketKey, AssociatedTicketKey, LinkType, Justification
            """,
            "prepared_ticket_related_jira",
            (reader, payload) => payload.RelatedJiraTickets.Add(
                new PreparedTicketRelatedJiraPayload
                {
                    AssociatedTicketKey = reader.GetString(3),
                    LinkType = reader.GetString(4),
                    Justification = reader.GetString(5),
                }),
            reader => $"{reader.GetString(3)}\u001f{reader.GetString(4)}",
            tickets,
            identities,
            orphanChildren,
            ct);
        await ReadChildTableAsync(
            connection,
            """
            SELECT RowId, Id, TicketKey, ZulipThreadId, Justification
            FROM prepared_ticket_related_zulip
            ORDER BY TicketKey, ZulipThreadId, Justification
            """,
            "prepared_ticket_related_zulip",
            (reader, payload) => payload.RelatedZulipThreads.Add(
                new PreparedTicketRelatedZulipPayload
                {
                    ZulipThreadId = reader.GetString(3),
                    Justification = reader.GetString(4),
                }),
            reader => reader.GetString(3),
            tickets,
            identities,
            orphanChildren,
            ct);
        await ReadChildTableAsync(
            connection,
            """
            SELECT RowId, Id, TicketKey, GitHubItemId, Justification
            FROM prepared_ticket_related_github
            ORDER BY TicketKey, GitHubItemId, Justification
            """,
            "prepared_ticket_related_github",
            (reader, payload) => payload.RelatedGitHubItems.Add(
                new PreparedTicketRelatedGitHubPayload
                {
                    GitHubItemId = reader.GetString(3),
                    Justification = reader.GetString(4),
                }),
            reader => reader.GetString(3),
            tickets,
            identities,
            orphanChildren,
            ct);
    }

    private static async Task ReadChildTableAsync(
        SqliteConnection connection,
        string sql,
        string table,
        Action<SqliteDataReader, PreparedTicketPayload> add,
        Func<SqliteDataReader, string> naturalKey,
        IReadOnlyDictionary<string, PersistedPreparedTicket> tickets,
        ICollection<PersistedRowIdentity> identities,
        ICollection<string> orphanChildren,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string ticketKey = reader.GetString(2);
            identities.Add(Identity(table, reader, ticketKey, naturalKey(reader)));
            if (tickets.TryGetValue(ticketKey, out PersistedPreparedTicket? ticket))
            {
                add(reader, ticket.Payload);
            }
            else
            {
                orphanChildren.Add($"{table}:{ticketKey}:{naturalKey(reader)}");
            }
        }
    }

    private static async Task<List<PersistedHydrationParent>> ReadHydrationParentsAsync(
        SqliteConnection connection,
        ICollection<PersistedRowIdentity> identities,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT RowId, Id, TicketKey, Priority, Resolution, ResolutionDescriptionPlain,
                   Specification, RaisedInVersion, SelectedBallot, ChangeCategory, Impact,
                   Labels, CommentCount, DescriptionPlain, HydratedAt, HydrationStatus,
                   HydrationReason
            FROM prepared_ticket_hydration
            ORDER BY TicketKey
            """;
        List<PersistedHydrationParent> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string key = reader.GetString(2);
            PersistedRowIdentity identity = Identity(
                "prepared_ticket_hydration",
                reader,
                key,
                key);
            identities.Add(identity);
            rows.Add(new PersistedHydrationParent(
                identity,
                new HydrationTicketRow(
                    key,
                    GetNullableString(reader, 3),
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    GetNullableString(reader, 7),
                    GetNullableString(reader, 8),
                    GetNullableString(reader, 9),
                    GetNullableString(reader, 10),
                    GetNullableString(reader, 11),
                    GetNullableInt(reader, 12),
                    GetNullableString(reader, 13),
                    ParseRequiredDate(reader.GetString(14)),
                    reader.GetString(15),
                    GetNullableString(reader, 16))));
        }

        return rows;
    }

    private static async Task<List<PersistedHydrationJira>> ReadHydrationJiraAsync(
        SqliteConnection connection,
        ICollection<PersistedRowIdentity> identities,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT RowId, Id, TicketKey, JiraKey, Title, Status, Type, Priority,
                   Resolution, ResolutionDescriptionPlain, WorkGroup, WorkGroupClean,
                   Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_jira_hydration
            ORDER BY TicketKey, JiraKey
            """;
        List<PersistedHydrationJira> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string ticketKey = reader.GetString(2);
            string jiraKey = reader.GetString(3);
            PersistedRowIdentity identity = Identity(
                "prepared_jira_hydration",
                reader,
                ticketKey,
                jiraKey);
            identities.Add(identity);
            rows.Add(new PersistedHydrationJira(
                identity,
                new HydrationJiraRow(
                    ticketKey,
                    jiraKey,
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    GetNullableString(reader, 7),
                    GetNullableString(reader, 8),
                    GetNullableString(reader, 9),
                    GetNullableString(reader, 10),
                    GetNullableString(reader, 12),
                    GetNullableDate(reader, 13),
                    GetNullableString(reader, 14),
                    ParseRequiredDate(reader.GetString(15)),
                    reader.GetString(16),
                    GetNullableString(reader, 17)),
                GetNullableString(reader, 11)));
        }

        return rows;
    }

    private static async Task<List<PersistedHydrationZulip>> ReadHydrationZulipAsync(
        SqliteConnection connection,
        ICollection<PersistedRowIdentity> identities,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT RowId, Id, TicketKey, ZulipThreadId, StreamId, StreamName, Topic,
                   MessageCount, FirstMessageAt, LastMessageAt, FirstMessageExcerpt,
                   Url, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_zulip_hydration
            ORDER BY TicketKey, ZulipThreadId
            """;
        List<PersistedHydrationZulip> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string ticketKey = reader.GetString(2);
            string thread = reader.GetString(3);
            PersistedRowIdentity identity = Identity(
                "prepared_zulip_hydration",
                reader,
                ticketKey,
                thread);
            identities.Add(identity);
            rows.Add(new PersistedHydrationZulip(
                identity,
                new HydrationZulipRow(
                    ticketKey,
                    thread,
                    GetNullableInt(reader, 4),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    GetNullableInt(reader, 7),
                    GetNullableDate(reader, 8),
                    GetNullableDate(reader, 9),
                    GetNullableString(reader, 10),
                    GetNullableString(reader, 11),
                    ParseRequiredDate(reader.GetString(12)),
                    reader.GetString(13),
                    GetNullableString(reader, 14))));
        }

        return rows;
    }

    private static async Task<List<PersistedHydrationGitHub>> ReadHydrationGitHubAsync(
        SqliteConnection connection,
        ICollection<PersistedRowIdentity> identities,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT RowId, Id, TicketKey, GitHubItemId, Owner, Repo, Number, Path, Title,
                   State, IsPullRequest, Labels, UpdatedAt, Url, HydratedAt,
                   HydrationStatus, HydrationReason
            FROM prepared_github_hydration
            ORDER BY TicketKey, GitHubItemId
            """;
        List<PersistedHydrationGitHub> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string ticketKey = reader.GetString(2);
            string item = reader.GetString(3);
            PersistedRowIdentity identity = Identity(
                "prepared_github_hydration",
                reader,
                ticketKey,
                item);
            identities.Add(identity);
            rows.Add(new PersistedHydrationGitHub(
                identity,
                new HydrationGitHubRow(
                    ticketKey,
                    item,
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    GetNullableInt(reader, 6),
                    GetNullableString(reader, 7),
                    GetNullableString(reader, 8),
                    GetNullableString(reader, 9),
                    GetNullableBool(reader, 10),
                    GetNullableString(reader, 11),
                    GetNullableDate(reader, 12),
                    GetNullableString(reader, 13),
                    ParseRequiredDate(reader.GetString(14)),
                    reader.GetString(15),
                    GetNullableString(reader, 16))));
        }

        return rows;
    }

    private static async Task<List<PersistedHydrationRepo>> ReadHydrationReposAsync(
        SqliteConnection connection,
        ICollection<PersistedRowIdentity> identities,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT RowId, Id, TicketKey, Repo, Description, WorkGroup, Specification,
                   CategoryDetail, Url, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_repo_hydration
            ORDER BY TicketKey, Repo
            """;
        List<PersistedHydrationRepo> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string ticketKey = reader.GetString(2);
            string repo = reader.GetString(3);
            PersistedRowIdentity identity = Identity(
                "prepared_repo_hydration",
                reader,
                ticketKey,
                repo);
            identities.Add(identity);
            rows.Add(new PersistedHydrationRepo(
                identity,
                new HydrationRepoRow(
                    ticketKey,
                    repo,
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    GetNullableString(reader, 7),
                    GetNullableString(reader, 8),
                    ParseRequiredDate(reader.GetString(9)),
                    reader.GetString(10),
                    GetNullableString(reader, 11))));
        }

        return rows;
    }

    private static async Task<List<PersistedHydrationJiraXref>> ReadHydrationXrefsAsync(
        SqliteConnection connection,
        ICollection<PersistedRowIdentity> identities,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT RowId, Id, TicketKey, JiraKey, Source
            FROM prepared_ticket_jira_xref
            ORDER BY TicketKey, JiraKey, Source
            """;
        List<PersistedHydrationJiraXref> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string ticketKey = reader.GetString(2);
            string jiraKey = reader.GetString(3);
            string source = reader.GetString(4);
            PersistedRowIdentity identity = Identity(
                "prepared_ticket_jira_xref",
                reader,
                ticketKey,
                $"{jiraKey}\u001f{source}");
            identities.Add(identity);
            rows.Add(new PersistedHydrationJiraXref(
                identity,
                new HydrationJiraXrefRow(ticketKey, jiraKey, source)));
        }

        return rows;
    }

    private static async Task<List<JiraProcessingSourceTicketRecord>> ReadSourceTicketsAsync(
        SqliteConnection connection,
        ICollection<PersistedRowIdentity> identities,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT RowId, Id, Key, Title, Description, Project, Status, WorkGroup, Type,
                   Specification, SourceTicketShape, LastSyncedAt, LastUpdated,
                   StartedProcessingAt, CompletedProcessingAt, LastProcessingAttemptAt,
                   ProcessingStatus, ProcessingError, ProcessingAttemptCount, CompletionId,
                   ErrorMessage, AgentExitCode, ErrorOccurredAt
            FROM jira_processing_source_tickets
            ORDER BY Key, SourceTicketShape
            """;
        List<JiraProcessingSourceTicketRecord> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string key = reader.GetString(2);
            string shape = reader.GetString(10);
            identities.Add(Identity(
                "jira_processing_source_tickets",
                reader,
                key,
                $"{key}\u001f{shape}"));
            rows.Add(new JiraProcessingSourceTicketRecord
            {
                RowId = reader.GetInt32(0),
                Id = reader.GetString(1),
                Key = key,
                Title = reader.GetString(3),
                Description = GetNullableString(reader, 4),
                Project = reader.GetString(5),
                Status = reader.GetString(6),
                WorkGroup = reader.GetString(7),
                Type = reader.GetString(8),
                Specification = reader.GetString(9),
                SourceTicketShape = shape,
                LastSyncedAt = ParseRequiredDate(reader.GetString(11)),
                LastUpdated = GetNullableDate(reader, 12),
                StartedProcessingAt = GetNullableDate(reader, 13),
                CompletedProcessingAt = GetNullableDate(reader, 14),
                LastProcessingAttemptAt = GetNullableDate(reader, 15),
                ProcessingStatus = GetNullableString(reader, 16),
                ProcessingError = GetNullableString(reader, 17),
                ProcessingAttemptCount = reader.GetInt32(18),
                CompletionId = GetNullableString(reader, 19),
                ErrorMessage = GetNullableString(reader, 20),
                AgentExitCode = GetNullableInt(reader, 21),
                ErrorOccurredAt = GetNullableDate(reader, 22),
            });
        }

        return rows;
    }

    private static PersistedRowIdentity Identity(
        string table,
        SqliteDataReader reader,
        string ticketKey,
        string naturalKey) =>
        new(table, reader.GetInt64(0), reader.GetString(1), ticketKey, naturalKey);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static bool? GetNullableBool(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture) != 0;

    private static DateTimeOffset? GetNullableDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : ParseRequiredDate(reader.GetString(ordinal));

    private static DateTimeOffset ParseRequiredDate(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
