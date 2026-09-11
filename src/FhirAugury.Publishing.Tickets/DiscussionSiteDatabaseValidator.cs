using System.Globalization;
using System.Text;
using FhirAugury.Common.Text;
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
    public const int Version = 1;
    public const string UnknownValueKey = "__unknown__";
    public const string UnknownDisplayValue = "(unknown)";
    public const string NamedValueKeyPrefix = "value:";

    public static IReadOnlyList<DiscussionRendererTable> Tables { get; } =
    [
        new(
            "site_metadata",
            """
            CREATE TABLE site_metadata(
                RendererSchemaVersion INTEGER PRIMARY KEY CHECK(RendererSchemaVersion = 1),
                BaseTitle TEXT NOT NULL,
                SiteName TEXT NOT NULL,
                JiraSourceLastSuccessfulRefreshAt TEXT NULL,
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
                "FilterSpecification",
                "FilterProject",
                "FilterWorkGroup",
            ],
            ["RendererSchemaVersion"]),
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
                SortKey TEXT NOT NULL,
                OrderInRole INTEGER NOT NULL,
                PRIMARY KEY(TicketKey, Role, OrderInRole)
            )
            """,
            ["TicketKey", "Role", "DisplayName", "SortKey", "OrderInRole"],
            ["TicketKey", "Role", "OrderInRole"]),
        new(
            "ticket_facets",
            """
            CREATE TABLE ticket_facets(
                TicketKey TEXT NOT NULL COLLATE NOCASE,
                Dimension TEXT NOT NULL CHECK(Dimension IN ('project','wg','type','artifact','page','impact','spec')),
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

    public static bool IsValidFhirJiraKey(string value)
    {
        const string prefix = "FHIR-";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            value.Length == prefix.Length)
        {
            return false;
        }
        return value.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

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

internal static class DiscussionSiteDatabaseValidator
{
    private static readonly HashSet<string> AllowedPeopleRoles =
        new(StringComparer.Ordinal)
        {
            "reporter",
            "assignee",
            "in-person-requester",
        };

    private static readonly HashSet<string> AllowedFacetDimensions =
        new(StringComparer.Ordinal)
        {
            "project",
            "wg",
            "type",
            "artifact",
            "page",
            "impact",
            "spec",
        };

    private static readonly HashSet<string> AllowedSummaryKinds =
        new(StringComparer.Ordinal)
        {
            "linked-jira",
            "related-jira",
            "related-zulip",
        };

    private static readonly HashSet<string> AllowedRelatedKinds =
        new(StringComparer.Ordinal)
        {
            "repo",
            "jira",
            "zulip",
            "github",
            "jira-xref",
        };

    private static readonly string[] ForbiddenIdentityColumnFragments =
    [
        "username",
        "email",
        "accountid",
        "accountkey",
        "userid",
        "userkey",
        "reporterid",
        "assigneeid",
        "credential",
        "password",
        "secret",
        "bearer",
        "token",
    ];

    public sealed record ValidationResult(
        TicketSitePresentation Presentation,
        IReadOnlyDictionary<string, long> TableCounts);

    public static async Task<ValidationResult> ValidateAsync(
        string databasePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        await using SqliteConnection connection = OpenReadOnly(databasePath);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await ValidateSchemaAsync(connection, ct).ConfigureAwait(false);
        await ValidateIntegrityAsync(connection, ct).ConfigureAwait(false);
        TicketSitePresentation presentation =
            await ValidateMetadataAsync(connection, ct).ConfigureAwait(false);
        await ValidateTicketsAsync(connection, ct).ConfigureAwait(false);
        await ValidatePeopleAsync(connection, ct).ConfigureAwait(false);
        await ValidateFacetsAsync(connection, presentation.Filters, ct)
            .ConfigureAwait(false);
        await ValidateRelatedItemsAsync(connection, ct).ConfigureAwait(false);
        await ValidateSummarySourcesAsync(connection, ct).ConfigureAwait(false);
        await ValidateTopicsAsync(connection, ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, long> counts =
            await ReadTableCountsAsync(connection, ct).ConfigureAwait(false);
        return new ValidationResult(presentation, counts);
    }

    public static async Task<ValidationResult> ValidateAsync(
        string databasePath,
        string sourceDatabasePath,
        int sourceSchemaVersion,
        string baseTitle,
        ResolvedFilters filters,
        CancellationToken ct = default)
    {
        ValidationResult result =
            await ValidateAsync(databasePath, ct).ConfigureAwait(false);
        DiscussionSiteProjection expected =
            await DiscussionSiteDatabaseBuilder.CreateProjectionAsync(
                sourceDatabasePath,
                sourceSchemaVersion,
                baseTitle,
                filters,
                ct).ConfigureAwait(false);

        await using SqliteConnection connection = OpenReadOnly(databasePath);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        foreach (DiscussionRendererTable table in DiscussionRendererSchema.Tables)
        {
            IReadOnlyList<string> expectedRows =
                expected.Rows[table.Name]
                    .Select(CanonicalizeRow)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            IReadOnlyList<string> actualRows =
                (await ReadRowsAsync(connection, table, ct).ConfigureAwait(false))
                    .Select(CanonicalizeRow)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            if (!expectedRows.SequenceEqual(actualRows, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer table '{table.Name}' does not match the immutable source projection.");
            }
        }

        return result;
    }

    public static async Task<IReadOnlyDictionary<string, long>>
        ReadTableCountsAsync(
            string databasePath,
            CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenReadOnly(databasePath);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return await ReadTableCountsAsync(connection, ct).ConfigureAwait(false);
    }

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        Dictionary<(string Type, string Name), string> actual =
            new();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT type, name, sql
                FROM sqlite_master
                WHERE name NOT LIKE 'sqlite_%'
                  AND type IN ('table','index','view','trigger')
                ORDER BY type, name
                """;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                actual[(reader.GetString(0), reader.GetString(1))] =
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            }
        }

        Dictionary<(string Type, string Name), string> expected =
            new();
        foreach (DiscussionRendererTable table in DiscussionRendererSchema.Tables)
        {
            expected[("table", table.Name)] = table.CreateSql;
        }
        foreach (DiscussionRendererIndex index in DiscussionRendererSchema.Indexes)
        {
            expected[("index", index.Name)] = index.CreateSql;
        }

        string[] missing = expected.Keys
            .Where(key => !actual.ContainsKey(key))
            .Select(key => $"{key.Type} {key.Name}")
            .ToArray();
        string[] unexpected = actual.Keys
            .Where(key => !expected.ContainsKey(key))
            .Select(key => $"{key.Type} {key.Name}")
            .ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
        {
            throw new InvalidOperationException(
                $"Renderer schema object mismatch. Missing: [{string.Join(", ", missing)}]; " +
                $"unexpected: [{string.Join(", ", unexpected)}].");
        }

        foreach (((string type, string name), string expectedSql) in expected)
        {
            string actualSql = actual[(type, name)];
            if (!string.Equals(
                NormalizeSql(expectedSql),
                NormalizeSql(actualSql),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer {type} '{name}' does not match schema v{DiscussionRendererSchema.Version}.");
            }
        }

        foreach (DiscussionRendererTable table in DiscussionRendererSchema.Tables)
        {
            IReadOnlyList<string> actualColumns =
                await ReadColumnNamesAsync(connection, table.Name, ct)
                    .ConfigureAwait(false);
            if (!table.Columns.SequenceEqual(
                actualColumns,
                StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer table '{table.Name}' has unexpected columns.");
            }

            string? forbidden = actualColumns.FirstOrDefault(column =>
                ForbiddenIdentityColumnFragments.Any(fragment =>
                    column.Contains(
                        fragment,
                        StringComparison.OrdinalIgnoreCase)));
            if (forbidden is not null)
            {
                throw new InvalidOperationException(
                    $"Renderer contains forbidden identity column '{table.Name}.{forbidden}'.");
            }
        }
    }

    private static async Task<TicketSitePresentation> ValidateMetadataAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        IReadOnlyList<IReadOnlyList<object?>> rows = await QueryAsync(
            connection,
            """
            SELECT RendererSchemaVersion, BaseTitle, SiteName,
                   JiraSourceLastSuccessfulRefreshAt,
                   FilterSpecification, FilterProject, FilterWorkGroup
            FROM site_metadata
            """,
            ct).ConfigureAwait(false);
        if (rows.Count != 1)
        {
            throw new InvalidOperationException(
                "Renderer database must contain exactly one site_metadata row.");
        }

        IReadOnlyList<object?> row = rows[0];
        long version = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
        if (version != DiscussionRendererSchema.Version)
        {
            throw new InvalidOperationException(
                $"Unsupported renderer schema version {version}.");
        }

        string baseTitle = RequireString(row[1], "site_metadata.BaseTitle");
        string siteName = RequireString(row[2], "site_metadata.SiteName");
        DateTimeOffset? refresh = null;
        if (row[3] is string refreshText)
        {
            refresh = ParseUtcTimestamp(
                refreshText,
                "site_metadata.JiraSourceLastSuccessfulRefreshAt");
            if (!string.Equals(
                refreshText,
                refresh.Value.ToString("O", CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Renderer Jira source refresh timestamp is not canonical UTC.");
            }
        }

        ResolvedFilters filters = new(
            OptionalNonWhiteSpace(row[4], "site_metadata.FilterSpecification"),
            OptionalNonWhiteSpace(row[5], "site_metadata.FilterProject"),
            OptionalNonWhiteSpace(row[6], "site_metadata.FilterWorkGroup"));
        TicketSitePresentation expected =
            TicketSitePresentation.CreateDiscussion(baseTitle, refresh, filters);
        if (!string.Equals(siteName, expected.SiteName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Renderer site name does not match its provenance and filter metadata.");
        }
        return expected;
    }

    private static async Task ValidateTicketsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        IReadOnlyList<IReadOnlyList<object?>> rows = await QueryAsync(
            connection,
            "SELECT Key, Project, SavedAt FROM tickets",
            ct).ConfigureAwait(false);
        foreach (IReadOnlyList<object?> row in rows)
        {
            string key = RequireString(row[0], "tickets.Key");
            string project = RequireString(row[1], $"tickets[{key}].Project");
            string expectedProject =
                DiscussionSiteDatabaseBuilder.ProjectFromTicketKey(key);
            if (!string.Equals(
                project,
                expectedProject,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Renderer ticket '{key}' has an inconsistent project.");
            }
            if (row[2] is string savedAt)
            {
                ParseTimestamp(savedAt, $"tickets[{key}].SavedAt");
            }
        }
    }

    private static async Task ValidatePeopleAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        List<Dictionary<string, object?>> tickets =
            await QueryNamedAsync(connection, "SELECT Key FROM tickets", ct)
                .ConfigureAwait(false);
        List<Dictionary<string, object?>> people =
            await QueryNamedAsync(
                connection,
                """
                SELECT TicketKey, Role, DisplayName, SortKey, OrderInRole
                FROM ticket_people
                ORDER BY TicketKey COLLATE NOCASE, Role, OrderInRole
                """,
                ct).ConfigureAwait(false);
        HashSet<string> ticketKeys = tickets
            .Select(row => ReadRequiredString(row, "Key"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, object?> row in people)
        {
            string ticketKey = ReadRequiredString(row, "TicketKey");
            string role = ReadRequiredString(row, "Role");
            if (!ticketKeys.Contains(ticketKey))
            {
                throw new InvalidOperationException(
                    $"Renderer person row references missing ticket '{ticketKey}'.");
            }
            if (!AllowedPeopleRoles.Contains(role))
            {
                throw new InvalidOperationException(
                    $"Renderer person role '{role}' is invalid.");
            }

            long order = ReadInt64(row, "OrderInRole");
            string? displayName = ReadNullableString(row, "DisplayName");
            string sortKey = ReadRequiredString(row, "SortKey", allowEmpty: true);
            if (role is "reporter" or "assignee")
            {
                if (order != 0)
                {
                    throw new InvalidOperationException(
                        $"Renderer {role} row for '{ticketKey}' must have order zero.");
                }
            }
            else if (displayName is null || string.IsNullOrWhiteSpace(displayName))
            {
                throw new InvalidOperationException(
                    $"Renderer requester row for '{ticketKey}' requires a display name.");
            }

            if (displayName is not null &&
                !string.Equals(
                    displayName,
                    PublicDisplayNamePolicy.Normalize(displayName),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer person display name for '{ticketKey}' is unsafe or not normalized.");
            }
            if (!string.Equals(
                sortKey,
                DiscussionRendererSchema.NormalizeSortKey(displayName),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer person sort key for '{ticketKey}' is invalid.");
            }
        }

        foreach (string ticketKey in ticketKeys)
        {
            Dictionary<string, object?>[] ticketPeople = people
                .Where(row => string.Equals(
                    ReadRequiredString(row, "TicketKey"),
                    ticketKey,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (string role in new[] { "reporter", "assignee" })
            {
                if (ticketPeople.Count(row =>
                    string.Equals(
                        ReadRequiredString(row, "Role"),
                        role,
                        StringComparison.Ordinal)) != 1)
                {
                    throw new InvalidOperationException(
                        $"Renderer ticket '{ticketKey}' must contain one {role} row.");
                }
            }

            Dictionary<string, object?>[] requesters = ticketPeople
                .Where(row => string.Equals(
                    ReadRequiredString(row, "Role"),
                    "in-person-requester",
                    StringComparison.Ordinal))
                .OrderBy(row => ReadInt64(row, "OrderInRole"))
                .ToArray();
            string[] names = requesters
                .Select(row => ReadRequiredString(row, "DisplayName"))
                .ToArray();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
                !names.SequenceEqual(
                    names.Order(StringComparer.OrdinalIgnoreCase)
                        .ThenBy(value => value, StringComparer.Ordinal),
                    StringComparer.Ordinal) ||
                !requesters.Select(row => ReadInt64(row, "OrderInRole"))
                    .SequenceEqual(Enumerable.Range(0, requesters.Length)
                        .Select(value => (long)value)))
            {
                throw new InvalidOperationException(
                    $"Renderer requester rows for '{ticketKey}' are not unique, alphabetical, and contiguous.");
            }
        }
    }

    private static async Task ValidateFacetsAsync(
        SqliteConnection connection,
        ResolvedFilters filters,
        CancellationToken ct)
    {
        List<Dictionary<string, object?>> tickets =
            await QueryNamedAsync(
                connection,
                """
                SELECT Key, Project, WorkGroup, Type, Specification,
                       ProposalAImpact, ProposalBImpact
                FROM tickets
                """,
                ct).ConfigureAwait(false);
        List<Dictionary<string, object?>> facets =
            await QueryNamedAsync(
                connection,
                """
                SELECT TicketKey, Dimension, ValueKey, DisplayValue, SortKey, IsUnknown
                FROM ticket_facets
                """,
                ct).ConfigureAwait(false);
        HashSet<string> ticketKeys = tickets
            .Select(row => ReadRequiredString(row, "Key"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, object?> row in facets)
        {
            string ticketKey = ReadRequiredString(row, "TicketKey");
            string dimension = ReadRequiredString(row, "Dimension");
            if (!ticketKeys.Contains(ticketKey))
            {
                throw new InvalidOperationException(
                    $"Renderer facet references missing ticket '{ticketKey}'.");
            }
            if (!AllowedFacetDimensions.Contains(dimension))
            {
                throw new InvalidOperationException(
                    $"Renderer facet dimension '{dimension}' is invalid.");
            }

            string valueKey = ReadRequiredString(row, "ValueKey");
            string displayValue = ReadRequiredString(row, "DisplayValue");
            string sortKey = ReadRequiredString(row, "SortKey");
            long isUnknown = ReadInt64(row, "IsUnknown");
            if (isUnknown is not 0 and not 1)
            {
                throw new InvalidOperationException(
                    $"Renderer facet '{ticketKey}/{dimension}' has invalid unknown state.");
            }
            if (isUnknown == 1)
            {
                if (!string.Equals(
                        valueKey,
                        DiscussionRendererSchema.UnknownValueKey,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        displayValue,
                        DiscussionRendererSchema.UnknownDisplayValue,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Renderer unknown facet '{ticketKey}/{dimension}' does not use the sentinel.");
                }
            }
            else
            {
                DiscussionFacetValue expected =
                    DiscussionRendererSchema.NormalizeFacetValue(displayValue);
                if (string.Equals(
                        valueKey,
                        DiscussionRendererSchema.UnknownValueKey,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        valueKey,
                        expected.ValueKey,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        displayValue,
                        displayValue.Trim(),
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Renderer named facet '{ticketKey}/{dimension}' has an invalid value key.");
                }
            }
            if (!string.Equals(
                sortKey,
                DiscussionRendererSchema.NormalizeSortKey(displayValue),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer facet '{ticketKey}/{dimension}/{valueKey}' has an invalid sort key.");
            }
        }

        foreach (Dictionary<string, object?> ticket in tickets)
        {
            string key = ReadRequiredString(ticket, "Key");
            Dictionary<string, object?>[] ticketFacets = facets
                .Where(row => string.Equals(
                    ReadRequiredString(row, "TicketKey"),
                    key,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ValidateFacetValues(
                key,
                "project",
                [ReadNullableString(ticket, "Project")],
                ticketFacets);
            ValidateFacetValues(
                key,
                "wg",
                [ReadNullableString(ticket, "WorkGroup")],
                ticketFacets);
            ValidateFacetValues(
                key,
                "type",
                [ReadNullableString(ticket, "Type")],
                ticketFacets);
            ValidateFacetValues(
                key,
                "spec",
                [ReadNullableString(ticket, "Specification")],
                ticketFacets);
            ValidateFacetValues(
                key,
                "impact",
                [
                    ReadNullableString(ticket, "ProposalAImpact"),
                    ReadNullableString(ticket, "ProposalBImpact"),
                ],
                ticketFacets);
            ValidateFilterFacet(
                key,
                "spec",
                filters.Specification,
                ticketFacets);
            ValidateFilterFacet(
                key,
                "project",
                filters.Project,
                ticketFacets);
            ValidateFilterFacet(
                key,
                "wg",
                filters.WorkGroup,
                ticketFacets);
        }
    }

    private static async Task ValidateRelatedItemsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        List<Dictionary<string, object?>> rows =
            await QueryNamedAsync(
                connection,
                """
                SELECT r.TicketKey, r.Kind, r.ItemKey, r.LinkType,
                       r.LinkTypeKey, r.Label, r.Url, r.HydrationStatus,
                       r.HydrationReason, r.SortKey, t.Key AS ExistingTicketKey
                FROM related_items r
                LEFT JOIN tickets t ON t.Key = r.TicketKey COLLATE NOCASE
                """,
                ct).ConfigureAwait(false);
        foreach (Dictionary<string, object?> row in rows)
        {
            string ticketKey = ReadRequiredString(row, "TicketKey");
            string kind = ReadRequiredString(row, "Kind");
            if (ReadNullableString(row, "ExistingTicketKey") is null)
            {
                throw new InvalidOperationException(
                    $"Renderer related item references missing ticket '{ticketKey}'.");
            }
            if (!AllowedRelatedKinds.Contains(kind))
            {
                throw new InvalidOperationException(
                    $"Renderer related item kind '{kind}' is invalid.");
            }

            string itemKey = ReadRequiredString(row, "ItemKey");
            string? linkType = ReadNullableString(row, "LinkType");
            if (!string.Equals(
                    itemKey,
                    itemKey.Trim(),
                    StringComparison.Ordinal) ||
                linkType is not null &&
                (string.IsNullOrWhiteSpace(linkType) ||
                 !string.Equals(
                     linkType,
                     linkType.Trim(),
                     StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Renderer related item '{ticketKey}/{kind}/{itemKey}' is not normalized.");
            }
            string linkTypeKey = ReadRequiredString(
                row,
                "LinkTypeKey",
                allowEmpty: true);
            if (!string.Equals(
                linkTypeKey,
                DiscussionRendererSchema.NormalizeLinkTypeKey(linkType),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer related item '{ticketKey}/{kind}/{itemKey}' has an invalid link-type key.");
            }
            string label = ReadRequiredString(row, "Label");
            if (!string.Equals(label, label.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer related item '{ticketKey}/{kind}/{itemKey}' has an unnormalized label.");
            }
            if (!string.Equals(
                ReadRequiredString(row, "SortKey", allowEmpty: true),
                DiscussionRendererSchema.NormalizeSortKey(label),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer related item '{ticketKey}/{kind}/{itemKey}' has an invalid sort key.");
            }

            string? url = ReadNullableString(row, "Url");
            if (url is not null &&
                (!string.Equals(url, url.Trim(), StringComparison.Ordinal) ||
                 !DiscussionRendererSchema.IsSafeExternalUrl(url)))
            {
                throw new InvalidOperationException(
                    $"Renderer related item '{ticketKey}/{kind}/{itemKey}' has an unsafe URL.");
            }
            if (kind == "github" && url is not null)
            {
                throw new InvalidOperationException(
                    "Renderer GitHub related items must not expose URLs.");
            }

            string? hydrationStatus = ReadNullableString(row, "HydrationStatus");
            string? hydrationReason = ReadNullableString(row, "HydrationReason");
            if (hydrationStatus is not null &&
                string.IsNullOrWhiteSpace(hydrationStatus))
            {
                throw new InvalidOperationException(
                    $"Renderer related item '{ticketKey}/{kind}/{itemKey}' has an empty hydration status.");
            }
            if (hydrationReason is not null &&
                string.IsNullOrWhiteSpace(hydrationReason))
            {
                throw new InvalidOperationException(
                    $"Renderer related item '{ticketKey}/{kind}/{itemKey}' has an empty hydration reason.");
            }
        }
    }

    private static async Task ValidateSummarySourcesAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        List<Dictionary<string, object?>> rows =
            await QueryNamedAsync(
                connection,
                """
                SELECT s.TicketKey, s.SummaryKind, s.SourceKey, s.Label,
                       s.Url, s.SortKey, t.LinkedTicketSummary,
                       t.RelatedTicketSummary, t.RelatedZulipSummary
                FROM summary_sources s
                LEFT JOIN tickets t ON t.Key = s.TicketKey COLLATE NOCASE
                """,
                ct).ConfigureAwait(false);
        foreach (Dictionary<string, object?> row in rows)
        {
            string ticketKey = ReadRequiredString(row, "TicketKey");
            string summaryKind = ReadRequiredString(row, "SummaryKind");
            if (!AllowedSummaryKinds.Contains(summaryKind))
            {
                throw new InvalidOperationException(
                    $"Renderer summary source kind '{summaryKind}' is invalid.");
            }
            string sourceKey = ReadRequiredString(row, "SourceKey");
            string label = ReadRequiredString(row, "Label");
            if (!string.Equals(
                    sourceKey,
                    sourceKey.Trim(),
                    StringComparison.Ordinal) ||
                !string.Equals(label, label.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer summary source '{ticketKey}/{summaryKind}/{sourceKey}' is not normalized.");
            }
            if (!string.Equals(
                ReadRequiredString(row, "SortKey"),
                DiscussionRendererSchema.NormalizeSortKey(label),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Renderer summary source '{ticketKey}/{summaryKind}/{sourceKey}' has an invalid sort key.");
            }

            string summaryColumn = summaryKind switch
            {
                "linked-jira" => "LinkedTicketSummary",
                "related-jira" => "RelatedTicketSummary",
                "related-zulip" => "RelatedZulipSummary",
                _ => throw new InvalidOperationException(),
            };
            if (string.IsNullOrWhiteSpace(
                ReadNullableString(row, summaryColumn)))
            {
                throw new InvalidOperationException(
                    $"Renderer summary source '{ticketKey}/{summaryKind}/{sourceKey}' has no represented summary.");
            }

            string? url = ReadNullableString(row, "Url");
            if (url is not null &&
                (!string.Equals(url, url.Trim(), StringComparison.Ordinal) ||
                 !DiscussionRendererSchema.IsSafeExternalUrl(url)))
            {
                throw new InvalidOperationException(
                    $"Renderer summary source '{ticketKey}/{summaryKind}/{sourceKey}' has an unsafe URL.");
            }
            if (summaryKind is "linked-jira" or "related-jira")
            {
                if (DiscussionRendererSchema.IsValidFhirJiraKey(sourceKey) &&
                    url is null)
                {
                    throw new InvalidOperationException(
                        $"Renderer FHIR Jira summary source '{sourceKey}' requires a URL.");
                }
                if (!DiscussionRendererSchema.IsValidFhirJiraKey(sourceKey) &&
                    url is not null)
                {
                    throw new InvalidOperationException(
                        $"Renderer non-FHIR Jira summary source '{sourceKey}' must remain plain text.");
                }
            }
        }

        IReadOnlyList<string> expectedRows =
            (await QueryAsync(
                connection,
                """
                SELECT r.TicketKey,
                       CASE
                           WHEN r.Kind = 'jira' AND r.LinkTypeKey = 'LINKED'
                               THEN 'linked-jira'
                           WHEN r.Kind = 'jira' AND r.LinkTypeKey = 'RELATED'
                               THEN 'related-jira'
                           ELSE 'related-zulip'
                       END,
                       r.ItemKey, r.Label, r.Url, r.SortKey
                FROM related_items r
                INNER JOIN tickets t
                    ON t.Key = r.TicketKey COLLATE NOCASE
                WHERE (
                        r.Kind = 'jira'
                        AND r.LinkTypeKey = 'LINKED'
                        AND NULLIF(TRIM(t.LinkedTicketSummary), '') IS NOT NULL
                      )
                   OR (
                        r.Kind = 'jira'
                        AND r.LinkTypeKey = 'RELATED'
                        AND NULLIF(TRIM(t.RelatedTicketSummary), '') IS NOT NULL
                      )
                   OR (
                        r.Kind = 'zulip'
                        AND r.LinkTypeKey = ''
                        AND NULLIF(TRIM(t.RelatedZulipSummary), '') IS NOT NULL
                      )
                """,
                ct).ConfigureAwait(false))
            .Select(CanonicalizeRow)
            .Order(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<string> actualRows =
            (await QueryAsync(
                connection,
                """
                SELECT TicketKey, SummaryKind, SourceKey, Label, Url, SortKey
                FROM summary_sources
                """,
                ct).ConfigureAwait(false))
            .Select(CanonicalizeRow)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!expectedRows.SequenceEqual(actualRows, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Renderer summary sources are not a complete and exact projection of eligible related items.");
        }
    }

    private static async Task ValidateTopicsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        List<Dictionary<string, object?>> topics =
            await QueryNamedAsync(connection, "SELECT * FROM topics", ct)
                .ConfigureAwait(false);
        List<Dictionary<string, object?>> groups =
            await QueryNamedAsync(connection, "SELECT * FROM topic_groups", ct)
                .ConfigureAwait(false);
        List<Dictionary<string, object?>> members =
            await QueryNamedAsync(connection, "SELECT * FROM topic_members", ct)
                .ConfigureAwait(false);
        List<Dictionary<string, object?>> tickets =
            await QueryNamedAsync(
                connection,
                "SELECT Key, Title, Status, Type FROM tickets",
                ct).ConfigureAwait(false);

        Dictionary<long, Dictionary<string, object?>> topicById =
            topics.ToDictionary(row => ReadInt64(row, "RowId"));
        Dictionary<long, Dictionary<string, object?>> groupById =
            groups.ToDictionary(row => ReadInt64(row, "RowId"));
        Dictionary<string, Dictionary<string, object?>> ticketByKey =
            tickets.ToDictionary(
                row => ReadRequiredString(row, "Key"),
                StringComparer.OrdinalIgnoreCase);

        foreach (Dictionary<string, object?> group in groups)
        {
            long groupId = ReadInt64(group, "RowId");
            long topicId = ReadInt64(group, "TopicRowId");
            if (!topicById.ContainsKey(topicId))
            {
                throw new InvalidOperationException(
                    $"Renderer topic group {groupId} references a missing topic.");
            }
            if (ReadInt64(group, "OrderInTopic") < 0)
            {
                throw new InvalidOperationException(
                    $"Renderer topic group {groupId} has a negative order.");
            }
            Dictionary<string, object?>[] groupMembers = members
                .Where(row =>
                    ReadNullableInt64(row, "TopicGroupRowId") == groupId)
                .ToArray();
            string firstTicketKey =
                ReadRequiredString(group, "FirstTicketKey");
            if (groupMembers.Length < 2 ||
                !groupMembers.Any(row => string.Equals(
                    ReadRequiredString(row, "TicketKey"),
                    firstTicketKey,
                    StringComparison.OrdinalIgnoreCase)) ||
                groupMembers.Any(row =>
                    ReadInt64(row, "TopicRowId") != topicId))
            {
                throw new InvalidOperationException(
                    $"Renderer topic group {groupId} has invalid membership.");
            }
            long[] groupMemberOrders = groupMembers
                .Select(row => ReadInt64(row, "OrderInContainer"))
                .ToArray();
            if (!HasUniqueContiguousOrder(groupMemberOrders))
            {
                throw new InvalidOperationException(
                    $"Renderer topic group {groupId} has invalid member order.");
            }
        }

        foreach ((long topicId, _) in topicById)
        {
            Dictionary<string, object?>[] topicMembers = members
                .Where(row => ReadInt64(row, "TopicRowId") == topicId)
                .ToArray();
            if (topicMembers.Length < 2)
            {
                throw new InvalidOperationException(
                    $"Renderer topic {topicId} must contain at least two tickets.");
            }
            long[] ungroupedMemberOrders = topicMembers
                .Where(row =>
                    ReadNullableInt64(row, "TopicGroupRowId") is null)
                .Select(row => ReadInt64(row, "OrderInContainer"))
                .ToArray();
            if (!HasUniqueContiguousOrder(ungroupedMemberOrders))
            {
                throw new InvalidOperationException(
                    $"Renderer topic {topicId} has invalid ungrouped member order.");
            }
            long[] topicGroupOrders = groups
                .Where(row => ReadInt64(row, "TopicRowId") == topicId)
                .Select(row => ReadInt64(row, "OrderInTopic"))
                .ToArray();
            if (topicGroupOrders.Distinct().Count() != topicGroupOrders.Length)
            {
                throw new InvalidOperationException(
                    $"Renderer topic {topicId} has duplicate group order.");
            }
        }

        foreach (Dictionary<string, object?> member in members)
        {
            long topicId = ReadInt64(member, "TopicRowId");
            long? groupId = ReadNullableInt64(member, "TopicGroupRowId");
            string ticketKey = ReadRequiredString(member, "TicketKey");
            if (!topicById.ContainsKey(topicId) ||
                !ticketByKey.TryGetValue(
                    ticketKey,
                    out Dictionary<string, object?>? ticket))
            {
                throw new InvalidOperationException(
                    $"Renderer topic member '{topicId}/{ticketKey}' references missing data.");
            }
            if (groupId is not null &&
                (!groupById.TryGetValue(
                    groupId.Value,
                    out Dictionary<string, object?>? group) ||
                 ReadInt64(group, "TopicRowId") != topicId))
            {
                throw new InvalidOperationException(
                    $"Renderer topic member '{topicId}/{ticketKey}' has an invalid group.");
            }
            if (ReadInt64(member, "OrderInContainer") < 0)
            {
                throw new InvalidOperationException(
                    $"Renderer topic member '{topicId}/{ticketKey}' has a negative order.");
            }
            foreach (string column in new[] { "Title", "Status", "Type" })
            {
                if (!Equals(
                    ReadNullableString(member, column),
                    ReadNullableString(ticket, column)))
                {
                    throw new InvalidOperationException(
                        $"Renderer topic member '{topicId}/{ticketKey}' has stale {column} data.");
                }
            }
        }
    }

    private static void ValidateFacetValues(
        string ticketKey,
        string dimension,
        IEnumerable<string?> values,
        IEnumerable<Dictionary<string, object?>> ticketFacets)
    {
        DiscussionFacetValue[] expected = values
            .Select(DiscussionRendererSchema.NormalizeFacetValue)
            .OrderBy(value => value.ValueKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.ValueKey, StringComparer.Ordinal)
            .DistinctBy(
                value => value.ValueKey,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DiscussionFacetValue[] actual = ticketFacets
            .Where(row => string.Equals(
                ReadRequiredString(row, "Dimension"),
                dimension,
                StringComparison.Ordinal))
            .Select(row => new DiscussionFacetValue(
                ReadRequiredString(row, "ValueKey"),
                ReadRequiredString(row, "DisplayValue"),
                ReadRequiredString(row, "SortKey"),
                ReadInt64(row, "IsUnknown")))
            .OrderBy(value => value.ValueKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.ValueKey, StringComparer.Ordinal)
            .ToArray();
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"Renderer facet membership for '{ticketKey}/{dimension}' is inconsistent.");
        }
    }

    private static void ValidateFilterFacet(
        string ticketKey,
        string dimension,
        string? filterValue,
        IEnumerable<Dictionary<string, object?>> ticketFacets)
    {
        if (filterValue is null)
        {
            return;
        }

        DiscussionFacetValue expected =
            DiscussionRendererSchema.NormalizeFacetValue(filterValue);
        bool matches = ticketFacets.Any(row =>
            string.Equals(
                ReadRequiredString(row, "Dimension"),
                dimension,
                StringComparison.Ordinal) &&
            ReadInt64(row, "IsUnknown") == 0 &&
            string.Equals(
                ReadRequiredString(row, "ValueKey"),
                expected.ValueKey,
                StringComparison.OrdinalIgnoreCase));
        if (!matches)
        {
            throw new InvalidOperationException(
                $"Renderer ticket '{ticketKey}' does not match the '{dimension}' filter.");
        }
    }

    private static bool HasUniqueContiguousOrder(IEnumerable<long> values)
    {
        long[] ordered = values.Order().ToArray();
        return ordered.SequenceEqual(
            Enumerable.Range(0, ordered.Length).Select(value => (long)value));
    }

    private static async Task ValidateIntegrityAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        string result = Convert.ToString(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture) ?? "unknown";
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Renderer integrity check failed: {result}.");
        }
    }

    private static async Task<IReadOnlyDictionary<string, long>>
        ReadTableCountsAsync(
            SqliteConnection connection,
            CancellationToken ct)
    {
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach (DiscussionRendererTable table in DiscussionRendererSchema.Tables)
        {
            counts[table.Name] = await ScalarInt64Async(
                connection,
                $"SELECT COUNT(*) FROM \"{table.Name}\"",
                ct).ConfigureAwait(false);
        }
        return counts;
    }

    private static async Task<IReadOnlyList<IReadOnlyList<object?>>>
        ReadRowsAsync(
            SqliteConnection connection,
            DiscussionRendererTable table,
            CancellationToken ct)
        => await QueryAsync(
            connection,
            $"SELECT {string.Join(", ", table.Columns.Select(QuoteIdentifier))} " +
            $"FROM {QuoteIdentifier(table.Name)}",
            ct).ConfigureAwait(false);

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        List<string> names = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(reader.GetString(1));
        }
        return names;
    }

    private static async Task<List<Dictionary<string, object?>>> QueryNamedAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct)
    {
        List<Dictionary<string, object?>> rows = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            Dictionary<string, object?> row =
                new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] =
                    reader.IsDBNull(index) ? null : reader.GetValue(index);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<IReadOnlyList<IReadOnlyList<object?>>> QueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct)
    {
        List<IReadOnlyList<object?>> rows = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            object?[] row = new object?[reader.FieldCount];
            for (int index = 0; index < reader.FieldCount; index++)
            {
                row[index] = reader.IsDBNull(index)
                    ? null
                    : reader.GetValue(index);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<long> ScalarInt64Async(
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
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static string NormalizeSql(string sql)
    {
        StringBuilder builder = new(sql.Length);
        bool inStringLiteral = false;
        for (int index = 0; index < sql.Length; index++)
        {
            char character = sql[index];
            if (inStringLiteral)
            {
                builder.Append(character);
                if (character != '\'')
                {
                    continue;
                }
                if (index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    builder.Append(sql[++index]);
                    continue;
                }
                inStringLiteral = false;
                continue;
            }

            if (character == '\'')
            {
                inStringLiteral = true;
                builder.Append(character);
            }
            else if (!char.IsWhiteSpace(character) && character != ';')
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }
        return builder.ToString();
    }

    private static string CanonicalizeRow(IReadOnlyList<object?> row)
        => string.Join(
            "|",
            row.Select(value => value switch
            {
                null => "N",
                byte[] bytes => $"B:{Convert.ToBase64String(bytes)}",
                string text =>
                    $"S:{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}",
                _ => $"V:{Convert.ToString(value, CultureInfo.InvariantCulture)}",
            }));

    private static string RequireString(
        object? value,
        string coordinate,
        bool allowEmpty = false)
    {
        if (value is not string text ||
            (!allowEmpty && string.IsNullOrWhiteSpace(text)))
        {
            throw new InvalidOperationException(
                $"Renderer value '{coordinate}' is required.");
        }
        return text;
    }

    private static string ReadRequiredString(
        IReadOnlyDictionary<string, object?> row,
        string column,
        bool allowEmpty = false)
        => RequireString(row[column], column, allowEmpty);

    private static string? ReadNullableString(
        IReadOnlyDictionary<string, object?> row,
        string column)
        => row[column] switch
        {
            null => null,
            string value => value,
            object value => Convert.ToString(
                value,
                CultureInfo.InvariantCulture),
        };

    private static long ReadInt64(
        IReadOnlyDictionary<string, object?> row,
        string column)
        => Convert.ToInt64(row[column], CultureInfo.InvariantCulture);

    private static long? ReadNullableInt64(
        IReadOnlyDictionary<string, object?> row,
        string column)
        => row[column] is null
            ? null
            : Convert.ToInt64(row[column], CultureInfo.InvariantCulture);

    private static string? OptionalNonWhiteSpace(
        object? value,
        string coordinate)
    {
        if (value is null)
        {
            return null;
        }
        string text = RequireString(value, coordinate);
        if (!string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Renderer value '{coordinate}' is not normalized.");
        }
        return text;
    }

    private static DateTimeOffset ParseUtcTimestamp(
        string value,
        string coordinate)
    {
        DateTimeOffset timestamp = ParseTimestamp(value, coordinate);
        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Renderer timestamp '{coordinate}' must be UTC.");
        }
        return timestamp;
    }

    private static DateTimeOffset ParseTimestamp(
        string value,
        string coordinate)
    {
        if (!DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset timestamp))
        {
            throw new InvalidOperationException(
                $"Renderer timestamp '{coordinate}' is invalid.");
        }
        return timestamp;
    }

    private static string QuoteIdentifier(string value)
        => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static SqliteConnection OpenReadOnly(string databasePath)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
}
