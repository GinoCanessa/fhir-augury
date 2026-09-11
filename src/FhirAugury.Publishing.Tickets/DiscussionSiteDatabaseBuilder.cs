using System.Collections.ObjectModel;
using System.Globalization;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets;

internal sealed record DiscussionSiteProjection(
    TicketSitePresentation Presentation,
    IReadOnlyDictionary<string, IReadOnlyList<object?[]>> Rows);

internal static class DiscussionSiteDatabaseBuilder
{
    public const int RendererSchemaVersion = DiscussionRendererSchema.Version;

    public sealed record BuildResult(
        string TempDbPath,
        long SurvivingTicketCount,
        TicketSitePresentation Presentation,
        bool OwnsTempFile);

    public static Task<BuildResult> BuildAsync(
        string sourceDatabasePath,
        AuthoringSnapshotDescriptor descriptor,
        string baseTitle,
        ResolvedFilters filters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return BuildAsync(
            sourceDatabasePath,
            descriptor.SchemaVersion,
            baseTitle,
            filters,
            ct);
    }

    public static async Task<BuildResult> BuildAsync(
        string sourceDatabasePath,
        int sourceSchemaVersion,
        string baseTitle,
        ResolvedFilters filters,
        CancellationToken ct = default)
    {
        DiscussionSiteProjection projection = await CreateProjectionAsync(
            sourceDatabasePath,
            sourceSchemaVersion,
            baseTitle,
            filters,
            ct).ConfigureAwait(false);

        string tempPath = Path.GetTempFileName();
        try
        {
            File.SetAttributes(tempPath, FileAttributes.Normal);
            await using SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = tempPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(ct)
                    .ConfigureAwait(false);
            await DiscussionRendererSchema.CreateAsync(
                connection,
                transaction,
                ct).ConfigureAwait(false);
            foreach (DiscussionRendererTable table in DiscussionRendererSchema.Tables)
            {
                foreach (object?[] row in projection.Rows[table.Name])
                {
                    await InsertRowAsync(
                        connection,
                        transaction,
                        table,
                        row,
                        ct).ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await connection.CloseAsync().ConfigureAwait(false);

            return new BuildResult(
                tempPath,
                projection.Rows["tickets"].Count,
                projection.Presentation,
                OwnsTempFile: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    internal static async Task<DiscussionSiteProjection> CreateProjectionAsync(
        string sourceDatabasePath,
        int sourceSchemaVersion,
        string baseTitle,
        ResolvedFilters filters,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseTitle);
        ArgumentNullException.ThrowIfNull(filters);
        if (!PreparedTicketSnapshotSchemaResolver.TryResolve(
            sourceSchemaVersion,
            out _))
        {
            throw new InvalidOperationException(
                $"Unsupported discussion source snapshot schema version {sourceSchemaVersion}.");
        }

        await using SqliteConnection source = OpenReadOnly(sourceDatabasePath);
        await source.OpenAsync(ct).ConfigureAwait(false);

        IReadOnlyList<string> selectedKeys =
            await ReadSelectedTicketKeysAsync(source, filters, ct)
                .ConfigureAwait(false);
        List<SourceTicket> tickets = new(selectedKeys.Count);
        foreach (string key in selectedKeys)
        {
            tickets.Add(
                await ReadTicketAsync(
                    source,
                    key,
                    sourceSchemaVersion,
                    ct).ConfigureAwait(false));
        }

        DateTimeOffset? freshness = await ReadCorpusFreshnessAsync(
            source,
            tickets,
            sourceSchemaVersion,
            ct).ConfigureAwait(false);
        TicketSitePresentation presentation =
            TicketSitePresentation.CreateDiscussion(
                baseTitle,
                freshness,
                filters);

        Dictionary<string, List<object?[]>> rows =
            DiscussionRendererSchema.Tables.ToDictionary(
                table => table.Name,
                _ => new List<object?[]>(),
                StringComparer.Ordinal);
        rows["site_metadata"].Add(
        [
            DiscussionRendererSchema.Version,
            presentation.BaseTitle,
            presentation.SiteName,
            presentation.JiraSourceLastSuccessfulRefreshAt?.ToString(
                "O",
                CultureInfo.InvariantCulture),
            filters.Specification,
            filters.Project,
            filters.WorkGroup,
        ]);

        Dictionary<string, SourceTicket> ticketByKey = tickets.ToDictionary(
            ticket => ticket.Key,
            StringComparer.OrdinalIgnoreCase);
        foreach (SourceTicket ticket in tickets)
        {
            rows["tickets"].Add(ticket.ToRendererRow());
        }

        await ProjectPeopleAsync(
            source,
            tickets,
            sourceSchemaVersion,
            rows["ticket_people"],
            ct).ConfigureAwait(false);
        await ProjectFacetsAsync(
            source,
            tickets,
            rows["ticket_facets"],
            ct).ConfigureAwait(false);
        await ProjectRelatedItemsAsync(
            source,
            ticketByKey,
            rows["related_items"],
            rows["summary_sources"],
            ct).ConfigureAwait(false);
        await ProjectTopicsAsync(
            source,
            ticketByKey,
            rows["topics"],
            rows["topic_groups"],
            rows["topic_members"],
            ct).ConfigureAwait(false);

        ReadOnlyDictionary<string, IReadOnlyList<object?[]>> readOnlyRows =
            new(rows.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<object?[]>)pair.Value.AsReadOnly(),
                StringComparer.Ordinal));
        return new DiscussionSiteProjection(presentation, readOnlyRows);
    }

    internal static string ProjectFromTicketKey(string key)
    {
        int separator = key.IndexOf('-', StringComparison.Ordinal);
        return separator > 0 ? key[..separator] : string.Empty;
    }

    private static async Task<IReadOnlyList<string>>
        ReadSelectedTicketKeysAsync(
            SqliteConnection source,
            ResolvedFilters filters,
            CancellationToken ct)
    {
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT pt.Key
            FROM prepared_tickets pt
            WHERE (
                    @project IS NULL
                    OR LOWER(substr(pt.Key, 1, instr(pt.Key, '-') - 1)) =
                       LOWER(@project)
                  )
              AND (
                    @workGroup IS NULL
                    OR EXISTS (
                        SELECT 1
                        FROM prepared_jira_hydration self
                        WHERE self.TicketKey = pt.Key COLLATE NOCASE
                          AND self.JiraKey = self.TicketKey COLLATE NOCASE
                          AND LOWER(self.WorkGroup) = LOWER(@workGroup)
                    )
                  )
              AND (
                    @specification IS NULL
                    OR EXISTS (
                        SELECT 1
                        FROM prepared_ticket_hydration parent
                        WHERE parent.TicketKey = pt.Key COLLATE NOCASE
                          AND LOWER(parent.Specification) =
                              LOWER(@specification)
                    )
                  )
            ORDER BY pt.Key COLLATE NOCASE, pt.Key
            """;
        command.Parameters.AddWithValue(
            "@project",
            (object?)filters.Project ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@workGroup",
            (object?)filters.WorkGroup ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@specification",
            (object?)filters.Specification ?? DBNull.Value);

        List<string> keys = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string key = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                throw new InvalidOperationException(
                    $"Discussion source contains an invalid or duplicate prepared ticket key '{key}'.");
            }
            keys.Add(key);
        }
        return keys;
    }

    private static async Task<SourceTicket> ReadTicketAsync(
        SqliteConnection source,
        string key,
        int sourceSchemaVersion,
        CancellationToken ct)
    {
        string v2Reporter = sourceSchemaVersion ==
            PreparedTicketSnapshotSchemaV2.Version
                ? "parent.Reporter"
                : "NULL";
        string v2Assignee = sourceSchemaVersion ==
            PreparedTicketSnapshotSchemaV2.Version
                ? "parent.Assignee"
                : "NULL";
        string v2SourceProject = sourceSchemaVersion ==
            PreparedTicketSnapshotSchemaV2.Version
                ? "parent.SourceProject"
                : "NULL";
        string v2SourceRefresh = sourceSchemaVersion ==
            PreparedTicketSnapshotSchemaV2.Version
                ? "parent.SourceLastSuccessfulRefreshAt"
                : "NULL";
        string v2SourceRevision = sourceSchemaVersion ==
            PreparedTicketSnapshotSchemaV2.Version
                ? "parent.SourceContentRevision"
                : "NULL";

        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            $"""
            SELECT ticket.Key,
                   self.Title,
                   self.WorkGroup,
                   self.Status,
                   self.Type,
                   COALESCE(parent.Specification, self.Specification)
                       AS Specification,
                   COALESCE(parent.Priority, self.Priority) AS Priority,
                   COALESCE(parent.Resolution, self.Resolution) AS Resolution,
                   parent.RaisedInVersion,
                   parent.SelectedBallot,
                   parent.ChangeCategory,
                   parent.Impact,
                   parent.CommentCount,
                   ticket.Recommendation,
                   ticket.RecommendationJustification,
                   ticket.SavedAt,
                   content.DescriptionHtml AS RequestHtml,
                   parent.DescriptionPlain AS RequestPlain,
                   content.ResolutionDescriptionHtml AS ResolutionHtml,
                   COALESCE(
                       parent.ResolutionDescriptionPlain,
                       self.ResolutionDescriptionPlain) AS ResolutionPlain,
                   ticket.RequestSummary,
                   ticket.CommentSummary,
                   ticket.LinkedTicketSummary,
                   ticket.RelatedTicketSummary,
                   ticket.RelatedZulipSummary,
                   ticket.RelatedGitHubSummary,
                   ticket.ExistingProposed,
                   ticket.ProposalA,
                   ticket.ProposalAJustification,
                   ticket.ProposalAImpact,
                   ticket.ProposalB,
                   ticket.ProposalBJustification,
                   ticket.ProposalBImpact,
                   ticket.ProposalC,
                   ticket.ProposalCJustification,
                   {v2Reporter} AS StructuredReporter,
                   {v2Assignee} AS StructuredAssignee,
                   {v2SourceProject} AS SourceProject,
                   {v2SourceRefresh} AS SourceLastSuccessfulRefreshAt,
                   {v2SourceRevision} AS SourceContentRevision
            FROM prepared_tickets ticket
            INNER JOIN prepared_ticket_hydration parent
                ON parent.TicketKey = ticket.Key COLLATE NOCASE
            INNER JOIN prepared_jira_hydration self
                ON self.TicketKey = ticket.Key COLLATE NOCASE
               AND self.JiraKey = self.TicketKey COLLATE NOCASE
            LEFT JOIN prepared_ticket_jira_content content
                ON content.TicketKey = ticket.Key COLLATE NOCASE
            WHERE ticket.Key = @key COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Discussion source ticket '{key}' lacks canonical parent or self hydration.");
        }

        SourceTicket ticket = new()
        {
            Key = reader.GetString(0),
            Title = ReadNullableString(reader, 1) ?? string.Empty,
            Project = ProjectFromTicketKey(reader.GetString(0)),
            WorkGroup = ReadNullableString(reader, 2),
            Status = ReadNullableString(reader, 3),
            Type = ReadNullableString(reader, 4),
            Specification = ReadNullableString(reader, 5),
            Priority = ReadNullableString(reader, 6),
            Resolution = ReadNullableString(reader, 7),
            RaisedInVersion = ReadNullableString(reader, 8),
            SelectedBallot = ReadNullableString(reader, 9),
            ChangeCategory = ReadNullableString(reader, 10),
            Impact = ReadNullableString(reader, 11),
            CommentCount = ReadNullableInt64(reader, 12),
            Recommendation = ReadNullableString(reader, 13),
            RecommendationJustification = ReadNullableString(reader, 14),
            SavedAt = ReadNullableString(reader, 15),
            RequestHtml = ReadNullableString(reader, 16),
            RequestPlain = ReadNullableString(reader, 17),
            ResolutionHtml = ReadNullableString(reader, 18),
            ResolutionPlain = ReadNullableString(reader, 19),
            RequestSummary = ReadNullableString(reader, 20),
            CommentSummary = ReadNullableString(reader, 21),
            LinkedTicketSummary = ReadNullableString(reader, 22),
            RelatedTicketSummary = ReadNullableString(reader, 23),
            RelatedZulipSummary = ReadNullableString(reader, 24),
            RelatedGitHubSummary = ReadNullableString(reader, 25),
            ExistingProposed = ReadNullableString(reader, 26),
            ProposalA = ReadNullableString(reader, 27),
            ProposalAJustification = ReadNullableString(reader, 28),
            ProposalAImpact = ReadNullableString(reader, 29),
            ProposalB = ReadNullableString(reader, 30),
            ProposalBJustification = ReadNullableString(reader, 31),
            ProposalBImpact = ReadNullableString(reader, 32),
            ProposalC = ReadNullableString(reader, 33),
            ProposalCJustification = ReadNullableString(reader, 34),
            Reporter = NormalizeDisplayName(ReadNullableString(reader, 35)),
            Assignee = NormalizeDisplayName(ReadNullableString(reader, 36)),
            SourceProject = ReadNullableString(reader, 37),
            SourceLastSuccessfulRefreshAt = ReadNullableString(reader, 38),
            SourceContentRevision = ReadNullableInt64(reader, 39),
        };
        if (string.IsNullOrWhiteSpace(ticket.Project))
        {
            throw new InvalidOperationException(
                $"Discussion source ticket key '{ticket.Key}' has no project prefix.");
        }
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Discussion source ticket '{key}' has ambiguous canonical hydration.");
        }
        return ticket;
    }

    private static async Task<DateTimeOffset?> ReadCorpusFreshnessAsync(
        SqliteConnection source,
        IReadOnlyList<SourceTicket> tickets,
        int sourceSchemaVersion,
        CancellationToken ct)
    {
        if (sourceSchemaVersion == PreparedTicketSnapshotSchemaV1.Version ||
            tickets.Count == 0)
        {
            return null;
        }

        bool complete = true;
        DateTimeOffset? maximum = null;
        foreach (SourceTicket ticket in tickets)
        {
            IReadOnlyList<string> runIds =
                await ReadAcceptedRunIdsAsync(source, ticket.Key, ct)
                    .ConfigureAwait(false);
            if (runIds.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Discussion source ticket '{ticket.Key}' must have exactly one accepted authoring coordinate; found {runIds.Count}.");
            }

            List<(string? Refresh, long? Revision)> provenance = [];
            await using (SqliteCommand command = source.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT LatestSuccessfulRefreshAt, ContentRevision
                    FROM authoring_run_input_provenance
                    WHERE RunId = @runId
                      AND LOWER(Source) = 'jira'
                    ORDER BY RowId
                    """;
                command.Parameters.AddWithValue("@runId", runIds[0]);
                await using SqliteDataReader reader =
                    await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    provenance.Add(
                        (ReadNullableString(reader, 0),
                         ReadNullableInt64(reader, 1)));
                }
            }
            if (provenance.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Discussion source run '{runIds[0]}' has multiple Jira provenance rows.");
            }

            (string? Refresh, long? Revision)? runProvenance =
                provenance.Count == 1 ? provenance[0] : null;
            DateTimeOffset? parentRefresh =
                ticket.SourceLastSuccessfulRefreshAt is null
                    ? null
                    : ParseSourceTimestamp(
                        ticket.SourceLastSuccessfulRefreshAt,
                        $"prepared_ticket_hydration[{ticket.Key}].SourceLastSuccessfulRefreshAt");
            DateTimeOffset? authoringRefresh =
                runProvenance?.Refresh is not { } runRefresh
                    ? null
                    : ParseSourceTimestamp(
                        runRefresh,
                        $"authoring_run_input_provenance[{runIds[0]}].LatestSuccessfulRefreshAt");
            if (ticket.SourceContentRevision is null ||
                parentRefresh is not { } stableParentRefresh ||
                string.IsNullOrWhiteSpace(ticket.SourceProject) ||
                !string.Equals(
                    ticket.SourceProject,
                    ticket.Project,
                    StringComparison.OrdinalIgnoreCase) ||
                runProvenance?.Revision is null ||
                authoringRefresh is not { } stableAuthoringRefresh)
            {
                complete = false;
                continue;
            }

            maximum = Max(
                maximum,
                stableParentRefresh.ToUniversalTime());
            maximum = Max(
                maximum,
                stableAuthoringRefresh.ToUniversalTime());
        }

        return complete ? maximum : null;
    }

    private static async Task<IReadOnlyList<string>> ReadAcceptedRunIdsAsync(
        SqliteConnection source,
        string ticketKey,
        CancellationToken ct)
    {
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT item.RunId
            FROM authoring_run_items item
            INNER JOIN authoring_result_receipts receipt
                ON receipt.Id = item.AcceptedReceiptId
               AND receipt.RunId = item.RunId
               AND receipt.RunItemId = item.Id
               AND receipt.BusinessKey = item.BusinessKey COLLATE NOCASE
               AND receipt.ExpectedSourceRevision =
                   item.ExpectedSourceRevision
               AND receipt.ObservedSourceRevision =
                   receipt.ExpectedSourceRevision
            INNER JOIN authoring_runs run
                ON run.Id = item.RunId
               AND run.ProcessorKind = 'jira-fhir'
               AND run.AuthoringEpoch = receipt.AuthoringEpoch
            WHERE item.BusinessKey = @ticketKey COLLATE NOCASE
              AND item.AcceptedReceiptId IS NOT NULL
              AND LOWER(item.Status) IN ('complete','superseded')
            ORDER BY item.RunId, item.Id
            """;
        command.Parameters.AddWithValue("@ticketKey", ticketKey);
        List<string> runIds = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            runIds.Add(reader.GetString(0));
        }
        return runIds;
    }

    private static async Task ProjectPeopleAsync(
        SqliteConnection source,
        IReadOnlyList<SourceTicket> tickets,
        int sourceSchemaVersion,
        ICollection<object?[]> destination,
        CancellationToken ct)
    {
        Dictionary<string, List<string>> requesters =
            new(StringComparer.OrdinalIgnoreCase);
        if (sourceSchemaVersion == PreparedTicketSnapshotSchemaV2.Version)
        {
            HashSet<string> selected = tickets
                .Select(ticket => ticket.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await using SqliteCommand command = source.CreateCommand();
            command.CommandText =
                """
                SELECT TicketKey, DisplayName
                FROM prepared_ticket_in_person_requesters
                ORDER BY TicketKey COLLATE NOCASE, DisplayName COLLATE NOCASE,
                         DisplayName, RowId
                """;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string ticketKey = reader.GetString(0);
                if (!selected.Contains(ticketKey))
                {
                    continue;
                }
                string? displayName =
                    NormalizeDisplayName(ReadNullableString(reader, 1));
                if (displayName is null)
                {
                    continue;
                }
                if (!requesters.TryGetValue(
                    ticketKey,
                    out List<string>? values))
                {
                    values = [];
                    requesters[ticketKey] = values;
                }
                values.Add(displayName);
            }
        }

        foreach (SourceTicket ticket in tickets)
        {
            destination.Add(
            [
                ticket.Key,
                "reporter",
                ticket.Reporter,
                DiscussionRendererSchema.NormalizeSortKey(ticket.Reporter),
                0,
            ]);
            destination.Add(
            [
                ticket.Key,
                "assignee",
                ticket.Assignee,
                DiscussionRendererSchema.NormalizeSortKey(ticket.Assignee),
                0,
            ]);

            string[] normalizedRequesters = requesters
                .GetValueOrDefault(ticket.Key, [])
                .Order(StringComparer.Ordinal)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToArray();
            for (int index = 0; index < normalizedRequesters.Length; index++)
            {
                string displayName = normalizedRequesters[index];
                destination.Add(
                [
                    ticket.Key,
                    "in-person-requester",
                    displayName,
                    DiscussionRendererSchema.NormalizeSortKey(displayName),
                    index,
                ]);
            }
        }
    }

    private static async Task ProjectFacetsAsync(
        SqliteConnection source,
        IReadOnlyList<SourceTicket> tickets,
        ICollection<object?[]> destination,
        CancellationToken ct)
    {
        Dictionary<string, List<string?>> artifacts =
            await ReadTicketValuesAsync(
                source,
                "prepared_ticket_artifacts",
                tickets,
                ct).ConfigureAwait(false);
        Dictionary<string, List<string?>> pages =
            await ReadTicketValuesAsync(
                source,
                "prepared_ticket_pages",
                tickets,
                ct).ConfigureAwait(false);

        foreach (SourceTicket ticket in tickets)
        {
            AddFacetRows(destination, ticket.Key, "project", [ticket.Project]);
            AddFacetRows(destination, ticket.Key, "wg", [ticket.WorkGroup]);
            AddFacetRows(destination, ticket.Key, "type", [ticket.Type]);
            AddFacetRows(
                destination,
                ticket.Key,
                "artifact",
                artifacts.GetValueOrDefault(ticket.Key, []));
            AddFacetRows(
                destination,
                ticket.Key,
                "page",
                pages.GetValueOrDefault(ticket.Key, []));
            AddFacetRows(
                destination,
                ticket.Key,
                "impact",
                [ticket.ProposalAImpact, ticket.ProposalBImpact]);
            AddFacetRows(destination, ticket.Key, "spec", [ticket.Specification]);
        }
    }

    private static async Task<Dictionary<string, List<string?>>>
        ReadTicketValuesAsync(
            SqliteConnection source,
            string table,
            IReadOnlyList<SourceTicket> tickets,
            CancellationToken ct)
    {
        HashSet<string> selected = tickets
            .Select(ticket => ticket.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string?>> values =
            new(StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            $"""
            SELECT TicketKey, Value
            FROM {table}
            ORDER BY TicketKey COLLATE NOCASE, Value COLLATE NOCASE,
                     Value, RowId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string ticketKey = reader.GetString(0);
            if (!selected.Contains(ticketKey))
            {
                continue;
            }
            if (!values.TryGetValue(ticketKey, out List<string?>? bucket))
            {
                bucket = [];
                values[ticketKey] = bucket;
            }
            bucket.Add(ReadNullableString(reader, 1));
        }
        return values;
    }

    private static void AddFacetRows(
        ICollection<object?[]> destination,
        string ticketKey,
        string dimension,
        IEnumerable<string?> values)
    {
        DiscussionFacetValue[] normalized = values
            .Select(DiscussionRendererSchema.NormalizeFacetValue)
            .OrderBy(value => value.ValueKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.ValueKey, StringComparer.Ordinal)
            .DistinctBy(
                value => value.ValueKey,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (DiscussionFacetValue value in normalized)
        {
            destination.Add(
            [
                ticketKey,
                dimension,
                value.ValueKey,
                value.DisplayValue,
                value.SortKey,
                value.IsUnknown,
            ]);
        }
    }

    private static async Task ProjectRelatedItemsAsync(
        SqliteConnection source,
        IReadOnlyDictionary<string, SourceTicket> ticketByKey,
        ICollection<object?[]> relatedDestination,
        ICollection<object?[]> summaryDestination,
        CancellationToken ct)
    {
        Dictionary<string, JiraHydration> jiraHydration =
            await ReadJiraHydrationAsync(source, ticketByKey.Keys, ct)
                .ConfigureAwait(false);
        Dictionary<string, ZulipHydration> zulipHydration =
            await ReadZulipHydrationAsync(source, ticketByKey.Keys, ct)
                .ConfigureAwait(false);
        Dictionary<string, GitHubHydration> githubHydration =
            await ReadGitHubHydrationAsync(source, ticketByKey.Keys, ct)
                .ConfigureAwait(false);
        Dictionary<string, RepoHydration> repoHydration =
            await ReadRepoHydrationAsync(source, ticketByKey.Keys, ct)
                .ConfigureAwait(false);
        HashSet<string> relatedCoordinates =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> summaryCoordinates =
            new(StringComparer.OrdinalIgnoreCase);

        await ProjectReposAsync(
            source,
            ticketByKey,
            repoHydration,
            relatedDestination,
            relatedCoordinates,
            ct).ConfigureAwait(false);
        await ProjectJiraAsync(
            source,
            ticketByKey,
            jiraHydration,
            relatedDestination,
            summaryDestination,
            relatedCoordinates,
            summaryCoordinates,
            ct).ConfigureAwait(false);
        await ProjectZulipAsync(
            source,
            ticketByKey,
            zulipHydration,
            relatedDestination,
            summaryDestination,
            relatedCoordinates,
            summaryCoordinates,
            ct).ConfigureAwait(false);
        await ProjectGitHubAsync(
            source,
            ticketByKey,
            githubHydration,
            relatedDestination,
            relatedCoordinates,
            ct).ConfigureAwait(false);
        await ProjectJiraXrefsAsync(
            source,
            ticketByKey,
            jiraHydration,
            relatedDestination,
            relatedCoordinates,
            ct).ConfigureAwait(false);
    }

    private static async Task ProjectReposAsync(
        SqliteConnection source,
        IReadOnlyDictionary<string, SourceTicket> ticketByKey,
        IReadOnlyDictionary<string, RepoHydration> hydration,
        ICollection<object?[]> destination,
        ISet<string> coordinates,
        CancellationToken ct)
    {
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT TicketKey, Repo, RepoCategory, Justification
            FROM prepared_ticket_repos
            ORDER BY TicketKey COLLATE NOCASE, Repo COLLATE NOCASE,
                     RepoCategory COLLATE NOCASE, RowId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string ticketKey = reader.GetString(0);
            if (!ticketByKey.ContainsKey(ticketKey))
            {
                continue;
            }
            string itemKey = RequireSourceValue(
                ReadNullableString(reader, 1),
                "prepared_ticket_repos.Repo");
            string? linkType = NormalizeOptional(ReadNullableString(reader, 2));
            string linkTypeKey =
                DiscussionRendererSchema.NormalizeLinkTypeKey(linkType);
            if (!coordinates.Add(
                RelatedCoordinate(ticketKey, "repo", itemKey, linkTypeKey)))
            {
                continue;
            }
            hydration.TryGetValue(
                HydrationCoordinate(ticketKey, itemKey),
                out RepoHydration? hydrated);
            AddRelatedRow(
                destination,
                ticketKey,
                "repo",
                itemKey,
                linkType,
                itemKey,
                SafeUrl(hydrated?.Url),
                hydrated?.Description,
                ReadNullableString(reader, 3),
                hydrated?.HydrationStatus,
                hydrated?.HydrationReason);
        }
    }

    private static async Task ProjectJiraAsync(
        SqliteConnection source,
        IReadOnlyDictionary<string, SourceTicket> ticketByKey,
        IReadOnlyDictionary<string, JiraHydration> hydration,
        ICollection<object?[]> relatedDestination,
        ICollection<object?[]> summaryDestination,
        ISet<string> relatedCoordinates,
        ISet<string> summaryCoordinates,
        CancellationToken ct)
    {
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT TicketKey, AssociatedTicketKey, LinkType, Justification
            FROM prepared_ticket_related_jira
            ORDER BY TicketKey COLLATE NOCASE,
                     AssociatedTicketKey COLLATE NOCASE,
                     LinkType COLLATE NOCASE, RowId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string ticketKey = reader.GetString(0);
            if (!ticketByKey.TryGetValue(ticketKey, out SourceTicket? ticket))
            {
                continue;
            }
            string itemKey = RequireSourceValue(
                ReadNullableString(reader, 1),
                "prepared_ticket_related_jira.AssociatedTicketKey");
            string? linkType = NormalizeOptional(ReadNullableString(reader, 2));
            string linkTypeKey =
                DiscussionRendererSchema.NormalizeLinkTypeKey(linkType);
            hydration.TryGetValue(
                HydrationCoordinate(ticketKey, itemKey),
                out JiraHydration? hydrated);
            if (relatedCoordinates.Add(
                RelatedCoordinate(ticketKey, "jira", itemKey, linkTypeKey)))
            {
                AddRelatedRow(
                    relatedDestination,
                    ticketKey,
                    "jira",
                    itemKey,
                    linkType,
                    itemKey,
                    JiraUrl(itemKey, hydrated?.Url),
                    JiraDetail(hydrated),
                    ReadNullableString(reader, 3),
                    hydrated?.HydrationStatus,
                    hydrated?.HydrationReason);
            }

            string? summaryKind = linkTypeKey switch
            {
                "LINKED" when !string.IsNullOrWhiteSpace(
                    ticket.LinkedTicketSummary) => "linked-jira",
                "RELATED" when !string.IsNullOrWhiteSpace(
                    ticket.RelatedTicketSummary) => "related-jira",
                _ => null,
            };
            if (summaryKind is null ||
                !summaryCoordinates.Add(
                    SummaryCoordinate(ticketKey, summaryKind, itemKey)))
            {
                continue;
            }
            string label = itemKey.Trim();
            summaryDestination.Add(
            [
                ticketKey,
                summaryKind,
                label,
                label,
                JiraUrl(label, hydrated?.Url),
                DiscussionRendererSchema.NormalizeSortKey(label),
            ]);
        }
    }

    private static async Task ProjectZulipAsync(
        SqliteConnection source,
        IReadOnlyDictionary<string, SourceTicket> ticketByKey,
        IReadOnlyDictionary<string, ZulipHydration> hydration,
        ICollection<object?[]> relatedDestination,
        ICollection<object?[]> summaryDestination,
        ISet<string> relatedCoordinates,
        ISet<string> summaryCoordinates,
        CancellationToken ct)
    {
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT TicketKey, ZulipThreadId, Justification
            FROM prepared_ticket_related_zulip
            ORDER BY TicketKey COLLATE NOCASE, ZulipThreadId COLLATE NOCASE,
                     RowId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string ticketKey = reader.GetString(0);
            if (!ticketByKey.TryGetValue(ticketKey, out SourceTicket? ticket))
            {
                continue;
            }
            string itemKey = RequireSourceValue(
                ReadNullableString(reader, 1),
                "prepared_ticket_related_zulip.ZulipThreadId");
            hydration.TryGetValue(
                HydrationCoordinate(ticketKey, itemKey),
                out ZulipHydration? hydrated);
            string label = ZulipLabel(itemKey, hydrated);
            if (relatedCoordinates.Add(
                RelatedCoordinate(ticketKey, "zulip", itemKey, string.Empty)))
            {
                AddRelatedRow(
                    relatedDestination,
                    ticketKey,
                    "zulip",
                    itemKey,
                    null,
                    label,
                    SafeUrl(hydrated?.Url),
                    ZulipDetail(hydrated),
                    ReadNullableString(reader, 2),
                    hydrated?.HydrationStatus,
                    hydrated?.HydrationReason);
            }

            if (string.IsNullOrWhiteSpace(ticket.RelatedZulipSummary) ||
                !summaryCoordinates.Add(
                    SummaryCoordinate(
                        ticketKey,
                        "related-zulip",
                        itemKey)))
            {
                continue;
            }
            summaryDestination.Add(
            [
                ticketKey,
                "related-zulip",
                itemKey.Trim(),
                label,
                SafeUrl(hydrated?.Url),
                DiscussionRendererSchema.NormalizeSortKey(label),
            ]);
        }
    }

    private static async Task ProjectGitHubAsync(
        SqliteConnection source,
        IReadOnlyDictionary<string, SourceTicket> ticketByKey,
        IReadOnlyDictionary<string, GitHubHydration> hydration,
        ICollection<object?[]> destination,
        ISet<string> coordinates,
        CancellationToken ct)
    {
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT TicketKey, GitHubItemId, Justification
            FROM prepared_ticket_related_github
            ORDER BY TicketKey COLLATE NOCASE, GitHubItemId COLLATE NOCASE,
                     RowId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string ticketKey = reader.GetString(0);
            if (!ticketByKey.ContainsKey(ticketKey))
            {
                continue;
            }
            string itemKey = RequireSourceValue(
                ReadNullableString(reader, 1),
                "prepared_ticket_related_github.GitHubItemId");
            if (!coordinates.Add(
                RelatedCoordinate(
                    ticketKey,
                    "github",
                    itemKey,
                    string.Empty)))
            {
                continue;
            }
            hydration.TryGetValue(
                HydrationCoordinate(ticketKey, itemKey),
                out GitHubHydration? hydrated);
            AddRelatedRow(
                destination,
                ticketKey,
                "github",
                itemKey,
                null,
                itemKey,
                null,
                GitHubDetail(itemKey, hydrated),
                ReadNullableString(reader, 2),
                hydrated?.HydrationStatus,
                hydrated?.HydrationReason);
        }
    }

    private static async Task ProjectJiraXrefsAsync(
        SqliteConnection source,
        IReadOnlyDictionary<string, SourceTicket> ticketByKey,
        IReadOnlyDictionary<string, JiraHydration> hydration,
        ICollection<object?[]> destination,
        ISet<string> coordinates,
        CancellationToken ct)
    {
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT TicketKey, JiraKey, Source
            FROM prepared_ticket_jira_xref
            ORDER BY TicketKey COLLATE NOCASE, JiraKey COLLATE NOCASE,
                     Source COLLATE NOCASE, RowId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string ticketKey = reader.GetString(0);
            if (!ticketByKey.ContainsKey(ticketKey))
            {
                continue;
            }
            string itemKey = RequireSourceValue(
                ReadNullableString(reader, 1),
                "prepared_ticket_jira_xref.JiraKey");
            string? linkType = NormalizeOptional(ReadNullableString(reader, 2));
            string linkTypeKey =
                DiscussionRendererSchema.NormalizeLinkTypeKey(linkType);
            if (!coordinates.Add(
                RelatedCoordinate(
                    ticketKey,
                    "jira-xref",
                    itemKey,
                    linkTypeKey)))
            {
                continue;
            }
            hydration.TryGetValue(
                HydrationCoordinate(ticketKey, itemKey),
                out JiraHydration? hydrated);
            AddRelatedRow(
                destination,
                ticketKey,
                "jira-xref",
                itemKey,
                linkType,
                itemKey,
                JiraUrl(itemKey, hydrated?.Url),
                JiraDetail(hydrated),
                null,
                hydrated?.HydrationStatus,
                hydrated?.HydrationReason);
        }
    }

    private static void AddRelatedRow(
        ICollection<object?[]> destination,
        string ticketKey,
        string kind,
        string itemKey,
        string? linkType,
        string label,
        string? url,
        string? detail,
        string? justification,
        string? hydrationStatus,
        string? hydrationReason)
    {
        string normalizedLabel = label.Trim();
        destination.Add(
        [
            ticketKey,
            kind,
            itemKey.Trim(),
            linkType,
            DiscussionRendererSchema.NormalizeLinkTypeKey(linkType),
            normalizedLabel,
            url,
            NormalizeOptional(detail),
            NormalizeOptional(justification),
            NormalizeOptional(hydrationStatus),
            NormalizeOptional(hydrationReason),
            DiscussionRendererSchema.NormalizeSortKey(normalizedLabel),
        ]);
    }

    private static async Task<Dictionary<string, JiraHydration>>
        ReadJiraHydrationAsync(
            SqliteConnection source,
            IEnumerable<string> selectedTicketKeys,
            CancellationToken ct)
    {
        HashSet<string> selected =
            selectedTicketKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JiraHydration> rows =
            new(StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT TicketKey, JiraKey, Title, Status, Type, Resolution, Url,
                   HydrationStatus, HydrationReason
            FROM prepared_jira_hydration
            ORDER BY RowId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string ticketKey = reader.GetString(0);
            if (!selected.Contains(ticketKey))
            {
                continue;
            }
            string itemKey = reader.GetString(1);
            rows.TryAdd(
                HydrationCoordinate(ticketKey, itemKey),
                new JiraHydration(
                    ReadNullableString(reader, 2),
                    ReadNullableString(reader, 3),
                    ReadNullableString(reader, 4),
                    ReadNullableString(reader, 5),
                    ReadNullableString(reader, 6),
                    ReadNullableString(reader, 7),
                    ReadNullableString(reader, 8)));
        }
        return rows;
    }

    private static async Task<Dictionary<string, ZulipHydration>>
        ReadZulipHydrationAsync(
            SqliteConnection source,
            IEnumerable<string> selectedTicketKeys,
            CancellationToken ct)
    {
        HashSet<string> selected =
            selectedTicketKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ZulipHydration> rows =
            new(StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT TicketKey, ZulipThreadId, StreamName, Topic, MessageCount,
                   LastMessageAt, FirstMessageExcerpt, Url, HydrationStatus,
                   HydrationReason
            FROM prepared_zulip_hydration
            ORDER BY RowId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string ticketKey = reader.GetString(0);
            if (!selected.Contains(ticketKey))
            {
                continue;
            }
            string itemKey = reader.GetString(1);
            rows.TryAdd(
                HydrationCoordinate(ticketKey, itemKey),
                new ZulipHydration(
                    ReadNullableString(reader, 2),
                    ReadNullableString(reader, 3),
                    ReadNullableInt64(reader, 4),
                    ReadNullableString(reader, 5),
                    ReadNullableString(reader, 6),
                    ReadNullableString(reader, 7),
                    ReadNullableString(reader, 8),
                    ReadNullableString(reader, 9)));
        }
        return rows;
    }

    private static async Task<Dictionary<string, GitHubHydration>>
        ReadGitHubHydrationAsync(
            SqliteConnection source,
            IEnumerable<string> selectedTicketKeys,
            CancellationToken ct)
    {
        HashSet<string> selected =
            selectedTicketKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GitHubHydration> rows =
            new(StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT TicketKey, GitHubItemId, Repo, Number, Path, Title, State,
                   IsPullRequest, HydrationStatus, HydrationReason
            FROM prepared_github_hydration
            ORDER BY RowId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string ticketKey = reader.GetString(0);
            if (!selected.Contains(ticketKey))
            {
                continue;
            }
            string itemKey = reader.GetString(1);
            rows.TryAdd(
                HydrationCoordinate(ticketKey, itemKey),
                new GitHubHydration(
                    ReadNullableString(reader, 2),
                    ReadNullableInt64(reader, 3),
                    ReadNullableString(reader, 4),
                    ReadNullableString(reader, 5),
                    ReadNullableString(reader, 6),
                    ReadNullableBoolean(reader, 7),
                    ReadNullableString(reader, 8),
                    ReadNullableString(reader, 9)));
        }
        return rows;
    }

    private static async Task<Dictionary<string, RepoHydration>>
        ReadRepoHydrationAsync(
            SqliteConnection source,
            IEnumerable<string> selectedTicketKeys,
            CancellationToken ct)
    {
        HashSet<string> selected =
            selectedTicketKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RepoHydration> rows =
            new(StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand command = source.CreateCommand();
        command.CommandText =
            """
            SELECT TicketKey, Repo, Description, Url, HydrationStatus,
                   HydrationReason
            FROM prepared_repo_hydration
            ORDER BY RowId
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string ticketKey = reader.GetString(0);
            if (!selected.Contains(ticketKey))
            {
                continue;
            }
            string itemKey = reader.GetString(1);
            rows.TryAdd(
                HydrationCoordinate(ticketKey, itemKey),
                new RepoHydration(
                    ReadNullableString(reader, 2),
                    ReadNullableString(reader, 3),
                    ReadNullableString(reader, 4),
                    ReadNullableString(reader, 5)));
        }
        return rows;
    }

    private static async Task ProjectTopicsAsync(
        SqliteConnection source,
        IReadOnlyDictionary<string, SourceTicket> ticketByKey,
        ICollection<object?[]> topicDestination,
        ICollection<object?[]> groupDestination,
        ICollection<object?[]> memberDestination,
        CancellationToken ct)
    {
        Dictionary<long, SourceTopic> topics = [];
        await using (SqliteCommand command = source.CreateCommand())
        {
            command.CommandText =
                """
                SELECT RowId, Id, WorkGroupClean, WorkGroupDisplay,
                       Specification, Type, ShortDescription,
                       LongerDescription, RenderOrderHint
                FROM prepared_ticket_topics
                ORDER BY RowId
                """;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long rowId = reader.GetInt64(0);
                topics.Add(
                    rowId,
                    new SourceTopic(
                        rowId,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        ReadNullableString(reader, 7),
                        ReadNullableInt64(reader, 8)));
            }
        }

        Dictionary<long, SourceTopicGroup> groups = [];
        await using (SqliteCommand command = source.CreateCommand())
        {
            command.CommandText =
                """
                SELECT RowId, Id, TopicRowId, FirstTicketKey, Rationale,
                       OrderInTopic
                FROM prepared_ticket_topic_groups
                ORDER BY RowId
                """;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long rowId = reader.GetInt64(0);
                groups.Add(
                    rowId,
                    new SourceTopicGroup(
                        rowId,
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        ReadNullableString(reader, 4),
                        reader.GetInt64(5)));
            }
        }

        List<SourceTopicMember> members = [];
        await using (SqliteCommand command = source.CreateCommand())
        {
            command.CommandText =
                """
                SELECT TopicRowId, TopicGroupRowId, TicketKey,
                       OrderInContainer
                FROM prepared_ticket_topic_members
                ORDER BY TopicRowId, OrderInContainer, TicketKey COLLATE NOCASE
                """;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string ticketKey = reader.GetString(2);
                if (!ticketByKey.ContainsKey(ticketKey) ||
                    !topics.ContainsKey(reader.GetInt64(0)))
                {
                    continue;
                }
                members.Add(
                    new SourceTopicMember(
                        reader.GetInt64(0),
                        ReadNullableInt64(reader, 1),
                        ticketKey,
                        reader.GetInt64(3)));
            }
        }

        foreach (SourceTopicMember member in members)
        {
            if (member.TopicGroupRowId is not long groupId ||
                !groups.TryGetValue(groupId, out SourceTopicGroup? group) ||
                group.TopicRowId != member.TopicRowId)
            {
                member.TopicGroupRowId = null;
                continue;
            }
            member.AuthoredGroupOrderInTopic = group.OrderInTopic;
        }

        HashSet<long> validGroups = groups.Values
            .Where(group =>
            {
                SourceTopicMember[] groupMembers = members
                    .Where(member =>
                        member.TopicGroupRowId == group.RowId &&
                        member.TopicRowId == group.TopicRowId)
                    .ToArray();
                return groupMembers.Length >= 2 &&
                       groupMembers.Any(member => string.Equals(
                           member.TicketKey,
                           group.FirstTicketKey,
                           StringComparison.OrdinalIgnoreCase));
            })
            .Select(group => group.RowId)
            .ToHashSet();
        foreach (SourceTopicMember member in members)
        {
            if (member.TopicGroupRowId is long groupId &&
                !validGroups.Contains(groupId))
            {
                member.TopicGroupRowId = null;
            }
        }

        HashSet<long> validTopics = topics.Keys
            .Where(topicId =>
                members.Count(member => member.TopicRowId == topicId) >= 2)
            .ToHashSet();
        foreach (long topicId in validTopics)
        {
            foreach (long groupId in validGroups.Where(groupId =>
                groups[groupId].TopicRowId == topicId))
            {
                SourceTopicMember[] groupMembers = members
                    .Where(member =>
                        member.TopicRowId == topicId &&
                        member.TopicGroupRowId == groupId)
                    .OrderBy(member => member.AuthoredOrderInContainer)
                    .ThenBy(
                        member => member.TicketKey,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(
                        member => member.TicketKey,
                        StringComparer.Ordinal)
                    .ToArray();
                for (int index = 0; index < groupMembers.Length; index++)
                {
                    groupMembers[index].OrderInContainer = index;
                }
            }

            SourceTopicMember[] ungroupedMembers = members
                .Where(member =>
                    member.TopicRowId == topicId &&
                    member.TopicGroupRowId is null)
                .OrderBy(member =>
                    member.AuthoredGroupOrderInTopic is null ? 1 : 0)
                .ThenBy(member =>
                    member.AuthoredGroupOrderInTopic ?? 0)
                .ThenBy(member => member.AuthoredOrderInContainer)
                .ThenBy(
                    member => member.TicketKey,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    member => member.TicketKey,
                    StringComparer.Ordinal)
                .ToArray();
            for (int index = 0; index < ungroupedMembers.Length; index++)
            {
                ungroupedMembers[index].OrderInContainer = index;
            }
        }

        foreach (long topicId in validTopics.Order())
        {
            SourceTopic topic = topics[topicId];
            topicDestination.Add(
            [
                topic.RowId,
                topic.Id,
                topic.WorkGroupClean,
                topic.WorkGroupDisplay,
                topic.Specification,
                topic.Type,
                topic.ShortDescription,
                topic.LongerDescription,
                topic.RenderOrderHint,
            ]);
        }

        foreach (SourceTopicGroup group in groups.Values
            .Where(group =>
                validTopics.Contains(group.TopicRowId) &&
                validGroups.Contains(group.RowId))
            .OrderBy(group => group.TopicRowId)
            .ThenBy(group => group.OrderInTopic)
            .ThenBy(group => group.RowId))
        {
            groupDestination.Add(
            [
                group.RowId,
                group.Id,
                group.TopicRowId,
                group.FirstTicketKey,
                group.Rationale,
                group.OrderInTopic,
            ]);
        }

        HashSet<string> memberCoordinates =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceTopicMember member in members
            .Where(member => validTopics.Contains(member.TopicRowId))
            .OrderBy(member => member.TopicRowId)
            .ThenBy(member => member.TopicGroupRowId is null ? 1 : 0)
            .ThenBy(member => member.TopicGroupRowId is long groupId
                ? groups[groupId].OrderInTopic
                : 0)
            .ThenBy(member => member.OrderInContainer)
            .ThenBy(member => member.TicketKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.TicketKey, StringComparer.Ordinal))
        {
            if (!memberCoordinates.Add(
                $"{member.TopicRowId}\0{member.TicketKey}"))
            {
                throw new InvalidOperationException(
                    $"Discussion source topic {member.TopicRowId} contains ticket '{member.TicketKey}' more than once.");
            }
            SourceTicket ticket = ticketByKey[member.TicketKey];
            memberDestination.Add(
            [
                member.TopicRowId,
                member.TopicGroupRowId,
                member.TicketKey,
                ticket.Title,
                ticket.Status,
                ticket.Type,
                member.OrderInContainer,
            ]);
        }
    }

    private static async Task InsertRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscussionRendererTable table,
        object?[] row,
        CancellationToken ct)
    {
        if (row.Length != table.Columns.Count)
        {
            throw new InvalidOperationException(
                $"Renderer row for '{table.Name}' has {row.Length} values; expected {table.Columns.Count}.");
        }
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        string[] parameterNames = table.Columns
            .Select((_, index) => $"@p{index}")
            .ToArray();
        command.CommandText =
            $"INSERT INTO \"{table.Name}\"(" +
            string.Join(
                ", ",
                table.Columns.Select(column =>
                    $"\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\"")) +
            $") VALUES({string.Join(", ", parameterNames)})";
        for (int index = 0; index < row.Length; index++)
        {
            command.Parameters.AddWithValue(
                parameterNames[index],
                row[index] ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string JiraDetail(JiraHydration? hydration)
        => JoinDetail(
            hydration?.Title,
            hydration?.Status,
            hydration?.Type,
            hydration?.Resolution);

    private static string ZulipLabel(
        string threadId,
        ZulipHydration? hydration)
    {
        string? stream = NormalizeOptional(hydration?.StreamName);
        string? topic = NormalizeOptional(hydration?.Topic);
        if (stream is not null && topic is not null)
        {
            return $"{stream} \u203a {topic}";
        }
        return threadId.Trim();
    }

    private static string ZulipDetail(ZulipHydration? hydration)
    {
        List<string?> parts = [];
        if (hydration?.MessageCount is long count)
        {
            parts.Add($"{count} messages");
        }
        if (!string.IsNullOrWhiteSpace(hydration?.LastMessageAt))
        {
            parts.Add($"last {hydration.LastMessageAt}");
        }
        if (!string.IsNullOrWhiteSpace(hydration?.FirstMessageExcerpt))
        {
            parts.Add($"\u201c{hydration.FirstMessageExcerpt}\u201d");
        }
        return JoinDetail(parts.ToArray());
    }

    private static string GitHubDetail(
        string itemKey,
        GitHubHydration? hydration)
    {
        if (hydration is null)
        {
            return string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(hydration.Path))
        {
            return JoinDetail(
                string.IsNullOrWhiteSpace(hydration.Repo)
                    ? hydration.Path
                    : $"{hydration.Repo}: {hydration.Path}",
                hydration.Title);
        }

        string headline = string.Empty;
        if (!string.IsNullOrWhiteSpace(hydration.Repo))
        {
            headline = hydration.Repo;
        }
        if (hydration.Number is long number)
        {
            headline += $"#{number}";
        }
        if (string.IsNullOrWhiteSpace(headline))
        {
            headline = itemKey;
        }
        return JoinDetail(
            headline,
            hydration.Title,
            hydration.State,
            hydration.IsPullRequest is null
                ? null
                : hydration.IsPullRequest.Value ? "(PR)" : "(Issue)");
    }

    private static string JoinDetail(params string?[] values)
        => string.Join(
            " \u00b7 ",
            values.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim()));

    private static string? JiraUrl(string key, string? hydratedUrl)
    {
        if (!DiscussionRendererSchema.IsValidFhirJiraKey(key))
        {
            return null;
        }
        return SafeUrl(hydratedUrl) ??
            $"https://jira.hl7.org/browse/{key.Trim().ToUpperInvariant()}";
    }

    private static string? SafeUrl(string? value)
        => DiscussionRendererSchema.IsSafeExternalUrl(value)
            ? value!.Trim()
            : null;

    private static string HydrationCoordinate(
        string ticketKey,
        string itemKey)
        => $"{ticketKey.Trim()}\0{itemKey.Trim()}";

    private static string RelatedCoordinate(
        string ticketKey,
        string kind,
        string itemKey,
        string linkTypeKey)
        => $"{ticketKey.Trim()}\0{kind}\0{itemKey.Trim()}\0{linkTypeKey}";

    private static string SummaryCoordinate(
        string ticketKey,
        string kind,
        string itemKey)
        => $"{ticketKey.Trim()}\0{kind}\0{itemKey.Trim()}";

    private static string RequireSourceValue(
        string? value,
        string coordinate)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Discussion source value '{coordinate}' is required.");
        }
        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeDisplayName(string? value)
        => NormalizeOptional(value);

    private static DateTimeOffset ParseSourceTimestamp(
        string value,
        string coordinate)
    {
        if (!DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset timestamp))
        {
            throw new InvalidOperationException(
                $"Discussion source timestamp '{coordinate}' is invalid.");
        }
        return timestamp;
    }

    private static DateTimeOffset Max(
        DateTimeOffset? current,
        DateTimeOffset candidate)
        => current is null || candidate > current.Value
            ? candidate
            : current.Value;

    private static string? ReadNullableString(
        SqliteDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? ReadNullableInt64(
        SqliteDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static bool? ReadNullableBoolean(
        SqliteDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : reader.GetInt64(ordinal) != 0;

    private static SqliteConnection OpenReadOnly(string databasePath)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class SourceTicket
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required string Project { get; init; }
        public string? WorkGroup { get; init; }
        public string? Status { get; init; }
        public string? Type { get; init; }
        public string? Specification { get; init; }
        public string? Priority { get; init; }
        public string? Resolution { get; init; }
        public string? RaisedInVersion { get; init; }
        public string? SelectedBallot { get; init; }
        public string? ChangeCategory { get; init; }
        public string? Impact { get; init; }
        public long? CommentCount { get; init; }
        public string? Recommendation { get; init; }
        public string? RecommendationJustification { get; init; }
        public string? SavedAt { get; init; }
        public string? RequestHtml { get; init; }
        public string? RequestPlain { get; init; }
        public string? ResolutionHtml { get; init; }
        public string? ResolutionPlain { get; init; }
        public string? RequestSummary { get; init; }
        public string? CommentSummary { get; init; }
        public string? LinkedTicketSummary { get; init; }
        public string? RelatedTicketSummary { get; init; }
        public string? RelatedZulipSummary { get; init; }
        public string? RelatedGitHubSummary { get; init; }
        public string? ExistingProposed { get; init; }
        public string? ProposalA { get; init; }
        public string? ProposalAJustification { get; init; }
        public string? ProposalAImpact { get; init; }
        public string? ProposalB { get; init; }
        public string? ProposalBJustification { get; init; }
        public string? ProposalBImpact { get; init; }
        public string? ProposalC { get; init; }
        public string? ProposalCJustification { get; init; }
        public string? Reporter { get; init; }
        public string? Assignee { get; init; }
        public string? SourceProject { get; init; }
        public string? SourceLastSuccessfulRefreshAt { get; init; }
        public long? SourceContentRevision { get; init; }

        public object?[] ToRendererRow() =>
        [
            Key,
            Title,
            Project,
            WorkGroup,
            Status,
            Type,
            Specification,
            Priority,
            Resolution,
            RaisedInVersion,
            SelectedBallot,
            ChangeCategory,
            Impact,
            CommentCount,
            Recommendation,
            RecommendationJustification,
            SavedAt,
            RequestHtml,
            RequestPlain,
            ResolutionHtml,
            ResolutionPlain,
            RequestSummary,
            CommentSummary,
            LinkedTicketSummary,
            RelatedTicketSummary,
            RelatedZulipSummary,
            RelatedGitHubSummary,
            ExistingProposed,
            ProposalA,
            ProposalAJustification,
            ProposalAImpact,
            ProposalB,
            ProposalBJustification,
            ProposalBImpact,
            ProposalC,
            ProposalCJustification,
        ];
    }

    private sealed record JiraHydration(
        string? Title,
        string? Status,
        string? Type,
        string? Resolution,
        string? Url,
        string? HydrationStatus,
        string? HydrationReason);

    private sealed record ZulipHydration(
        string? StreamName,
        string? Topic,
        long? MessageCount,
        string? LastMessageAt,
        string? FirstMessageExcerpt,
        string? Url,
        string? HydrationStatus,
        string? HydrationReason);

    private sealed record GitHubHydration(
        string? Repo,
        long? Number,
        string? Path,
        string? Title,
        string? State,
        bool? IsPullRequest,
        string? HydrationStatus,
        string? HydrationReason);

    private sealed record RepoHydration(
        string? Description,
        string? Url,
        string? HydrationStatus,
        string? HydrationReason);

    private sealed record SourceTopic(
        long RowId,
        string Id,
        string WorkGroupClean,
        string WorkGroupDisplay,
        string Specification,
        string Type,
        string ShortDescription,
        string? LongerDescription,
        long? RenderOrderHint);

    private sealed record SourceTopicGroup(
        long RowId,
        string Id,
        long TopicRowId,
        string FirstTicketKey,
        string? Rationale,
        long OrderInTopic);

    private sealed class SourceTopicMember(
        long topicRowId,
        long? topicGroupRowId,
        string ticketKey,
        long orderInContainer)
    {
        public long TopicRowId { get; } = topicRowId;
        public long? TopicGroupRowId { get; set; } = topicGroupRowId;
        public string TicketKey { get; } = ticketKey;
        public long AuthoredOrderInContainer { get; } = orderInContainer;
        public long? AuthoredGroupOrderInTopic { get; set; }
        public long OrderInContainer { get; set; } = orderInContainer;
    }
}
