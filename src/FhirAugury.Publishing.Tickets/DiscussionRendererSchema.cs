using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets;

internal sealed record DiscussionRendererTable(
    string Name,
    string CreateSql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> KeyColumns);

internal sealed record DiscussionRendererIndex(
    string Name,
    string CreateSql);

internal readonly record struct DiscussionFacetValue(
    string ValueKey,
    string DisplayValue,
    string SortKey,
    long IsUnknown);

internal static class DiscussionRendererSchema
{
    public const int Version = 2;
    public const string UnknownValueKey = "__unknown__";
    public const string UnknownDisplayValue = "(unknown)";
    public const string NamedValueKeyPrefix = "value:";
    public const string PersonAvailable = "available";
    public const string PersonUnavailable = "unavailable";

    public static IReadOnlyList<DiscussionRendererTable> Tables { get; } =
    [
        new(
            "site_metadata",
            """
            CREATE TABLE site_metadata(
                RendererSchemaVersion INTEGER PRIMARY KEY CHECK(RendererSchemaVersion = 2),
                BaseTitle TEXT NOT NULL,
                SiteName TEXT NOT NULL,
                JiraSourceLastSuccessfulRefreshAt TEXT NULL,
                ReadinessJson TEXT NOT NULL,
                FilterSpecification TEXT NULL,
                FilterProject TEXT NULL,
                FilterWorkGroup TEXT NULL
            )
            """,
            [
                "RendererSchemaVersion",
                "BaseTitle",
                "SiteName",
                "JiraSourceLastSuccessfulRefreshAt",
                "ReadinessJson",
                "FilterSpecification",
                "FilterProject",
                "FilterWorkGroup",
            ],
            ["RendererSchemaVersion"]),
        new(
            "facet_dimensions",
            """
            CREATE TABLE facet_dimensions(
                Dimension TEXT PRIMARY KEY,
                Route TEXT NOT NULL UNIQUE,
                Label TEXT NOT NULL,
                SortOrder INTEGER NOT NULL UNIQUE,
                ShowInList INTEGER NOT NULL CHECK(ShowInList IN (0,1))
            )
            """,
            ["Dimension", "Route", "Label", "SortOrder", "ShowInList"],
            ["Dimension"]),
        new(
            "tickets",
            """
            CREATE TABLE tickets(
                Key TEXT PRIMARY KEY COLLATE NOCASE,
                Title TEXT NOT NULL,
                Project TEXT NOT NULL,
                WorkGroup TEXT NULL,
                Status TEXT NULL,
                Type TEXT NULL,
                Specification TEXT NULL,
                Priority TEXT NULL,
                Resolution TEXT NULL,
                RaisedInVersion TEXT NULL,
                SelectedBallot TEXT NULL,
                ChangeCategory TEXT NULL,
                Impact TEXT NULL,
                CommentCount INTEGER NULL,
                Recommendation TEXT NULL,
                RecommendationJustification TEXT NULL,
                SavedAt TEXT NULL,
                RequestHtml TEXT NULL,
                RequestPlain TEXT NULL,
                ResolutionHtml TEXT NULL,
                ResolutionPlain TEXT NULL,
                RequestSummary TEXT NULL,
                CommentSummary TEXT NULL,
                LinkedTicketSummary TEXT NULL,
                RelatedTicketSummary TEXT NULL,
                RelatedZulipSummary TEXT NULL,
                RelatedGitHubSummary TEXT NULL,
                ExistingProposed TEXT NULL,
                ProposalA TEXT NULL,
                ProposalAJustification TEXT NULL,
                ProposalAImpact TEXT NULL,
                ProposalB TEXT NULL,
                ProposalBJustification TEXT NULL,
                ProposalBImpact TEXT NULL,
                ProposalC TEXT NULL,
                ProposalCJustification TEXT NULL
            )
            """,
            [
                "Key",
                "Title",
                "Project",
                "WorkGroup",
                "Status",
                "Type",
                "Specification",
                "Priority",
                "Resolution",
                "RaisedInVersion",
                "SelectedBallot",
                "ChangeCategory",
                "Impact",
                "CommentCount",
                "Recommendation",
                "RecommendationJustification",
                "SavedAt",
                "RequestHtml",
                "RequestPlain",
                "ResolutionHtml",
                "ResolutionPlain",
                "RequestSummary",
                "CommentSummary",
                "LinkedTicketSummary",
                "RelatedTicketSummary",
                "RelatedZulipSummary",
                "RelatedGitHubSummary",
                "ExistingProposed",
                "ProposalA",
                "ProposalAJustification",
                "ProposalAImpact",
                "ProposalB",
                "ProposalBJustification",
                "ProposalBImpact",
                "ProposalC",
                "ProposalCJustification",
            ],
            ["Key"]),
        new(
            "ticket_people",
            """
            CREATE TABLE ticket_people(
                TicketKey TEXT NOT NULL COLLATE NOCASE,
                Role TEXT NOT NULL CHECK(Role IN ('reporter','assignee','in-person-requester')),
                DisplayName TEXT NULL,
                Availability TEXT NOT NULL CHECK(Availability IN ('available','unavailable')),
                UnavailableReason TEXT NULL,
                SortKey TEXT NOT NULL,
                OrderInRole INTEGER NOT NULL,
                PRIMARY KEY(TicketKey, Role, OrderInRole)
            )
            """,
            [
                "TicketKey",
                "Role",
                "DisplayName",
                "Availability",
                "UnavailableReason",
                "SortKey",
                "OrderInRole",
            ],
            ["TicketKey", "Role", "OrderInRole"]),
        new(
            "ticket_facets",
            """
            CREATE TABLE ticket_facets(
                TicketKey TEXT NOT NULL COLLATE NOCASE,
                Dimension TEXT NOT NULL,
                ValueKey TEXT NOT NULL COLLATE NOCASE,
                DisplayValue TEXT NOT NULL,
                SortKey TEXT NOT NULL,
                IsUnknown INTEGER NOT NULL CHECK(IsUnknown IN (0,1)),
                PRIMARY KEY(TicketKey, Dimension, ValueKey)
            )
            """,
            [
                "TicketKey",
                "Dimension",
                "ValueKey",
                "DisplayValue",
                "SortKey",
                "IsUnknown",
            ],
            ["TicketKey", "Dimension", "ValueKey"]),
        new(
            "summary_sources",
            """
            CREATE TABLE summary_sources(
                TicketKey TEXT NOT NULL COLLATE NOCASE,
                SummaryKind TEXT NOT NULL CHECK(SummaryKind IN ('linked-jira','related-jira','related-zulip')),
                SourceKey TEXT NOT NULL COLLATE NOCASE,
                Label TEXT NOT NULL,
                Url TEXT NULL,
                SortKey TEXT NOT NULL,
                PRIMARY KEY(TicketKey, SummaryKind, SourceKey)
            )
            """,
            ["TicketKey", "SummaryKind", "SourceKey", "Label", "Url", "SortKey"],
            ["TicketKey", "SummaryKind", "SourceKey"]),
        new(
            "related_items",
            """
            CREATE TABLE related_items(
                TicketKey TEXT NOT NULL COLLATE NOCASE,
                Kind TEXT NOT NULL CHECK(Kind IN ('repo','jira','zulip','github','jira-xref')),
                ItemKey TEXT NOT NULL COLLATE NOCASE,
                LinkType TEXT NULL,
                LinkTypeKey TEXT NOT NULL,
                Label TEXT NOT NULL,
                Url TEXT NULL,
                Detail TEXT NULL,
                Justification TEXT NULL,
                HydrationStatus TEXT NULL,
                HydrationReason TEXT NULL,
                SortKey TEXT NOT NULL,
                PRIMARY KEY(TicketKey, Kind, ItemKey, LinkTypeKey)
            )
            """,
            [
                "TicketKey",
                "Kind",
                "ItemKey",
                "LinkType",
                "LinkTypeKey",
                "Label",
                "Url",
                "Detail",
                "Justification",
                "HydrationStatus",
                "HydrationReason",
                "SortKey",
            ],
            ["TicketKey", "Kind", "ItemKey", "LinkTypeKey"]),
        new(
            "topics",
            """
            CREATE TABLE topics(
                RowId INTEGER PRIMARY KEY,
                Id TEXT NOT NULL UNIQUE,
                WorkGroupClean TEXT NOT NULL,
                WorkGroupDisplay TEXT NOT NULL,
                Specification TEXT NOT NULL,
                Type TEXT NOT NULL,
                ShortDescription TEXT NOT NULL,
                LongerDescription TEXT NULL,
                RenderOrderHint INTEGER NULL
            )
            """,
            [
                "RowId",
                "Id",
                "WorkGroupClean",
                "WorkGroupDisplay",
                "Specification",
                "Type",
                "ShortDescription",
                "LongerDescription",
                "RenderOrderHint",
            ],
            ["RowId"]),
        new(
            "topic_groups",
            """
            CREATE TABLE topic_groups(
                RowId INTEGER PRIMARY KEY,
                Id TEXT NOT NULL UNIQUE,
                TopicRowId INTEGER NOT NULL,
                FirstTicketKey TEXT NOT NULL COLLATE NOCASE,
                Rationale TEXT NULL,
                OrderInTopic INTEGER NOT NULL
            )
            """,
            [
                "RowId",
                "Id",
                "TopicRowId",
                "FirstTicketKey",
                "Rationale",
                "OrderInTopic",
            ],
            ["RowId"]),
        new(
            "topic_members",
            """
            CREATE TABLE topic_members(
                TopicRowId INTEGER NOT NULL,
                TopicGroupRowId INTEGER NULL,
                TicketKey TEXT NOT NULL COLLATE NOCASE,
                Title TEXT NOT NULL,
                Status TEXT NULL,
                Type TEXT NULL,
                OrderInContainer INTEGER NOT NULL,
                PRIMARY KEY(TopicRowId, TicketKey)
            )
            """,
            [
                "TopicRowId",
                "TopicGroupRowId",
                "TicketKey",
                "Title",
                "Status",
                "Type",
                "OrderInContainer",
            ],
            ["TopicRowId", "TicketKey"]),
    ];

    public static IReadOnlyList<DiscussionRendererIndex> Indexes { get; } =
    [
        new(
            "ix_ticket_facets_sort",
            """
            CREATE INDEX ix_ticket_facets_sort
            ON ticket_facets(Dimension, IsUnknown, SortKey, DisplayValue)
            """),
        new(
            "ix_ticket_facets_lookup",
            """
            CREATE INDEX ix_ticket_facets_lookup
            ON ticket_facets(Dimension, ValueKey, TicketKey)
            """),
        new(
            "ix_topic_members_group_order",
            """
            CREATE INDEX ix_topic_members_group_order
            ON topic_members(TopicGroupRowId, OrderInContainer)
            """),
        new(
            "ix_topic_members_ticket",
            """
            CREATE INDEX ix_topic_members_ticket
            ON topic_members(TicketKey, TopicRowId)
            """),
    ];

    public static async Task CreateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        foreach (DiscussionRendererTable table in Tables)
        {
            await ExecuteAsync(connection, transaction, table.CreateSql, ct)
                .ConfigureAwait(false);
        }
        foreach (DiscussionRendererIndex index in Indexes)
        {
            await ExecuteAsync(connection, transaction, index.CreateSql, ct)
                .ConfigureAwait(false);
        }
    }

    public static DiscussionFacetValue NormalizeFacetValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new DiscussionFacetValue(
                UnknownValueKey,
                UnknownDisplayValue,
                NormalizeSortKey(UnknownDisplayValue),
                1);
        }

        string display = value.Trim();
        return new DiscussionFacetValue(
            $"{NamedValueKeyPrefix}{display}",
            display,
            NormalizeSortKey(display),
            0);
    }

    public static string NormalizeSortKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();

    public static string NormalizeLinkTypeKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();

    public static bool IsSafeExternalUrl(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) &&
           (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
