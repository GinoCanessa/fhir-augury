using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Authoring;

public sealed record JiraAuthoringRunCreation(
    AuthoringRunRecord Run,
    IReadOnlyList<AuthoringRunItemRecord> Items);

public sealed record JiraAuthoringRunRequestSnapshot(
    IReadOnlyList<string> TicketKeys,
    bool DatabaseOnly);

public sealed class JiraAuthoringRunCoordinator(
    AuthoringRunStore authoringStore,
    JiraProcessingSourceTicketStore sourceStore,
    JiraProcessingFilterResolver filterResolver,
    IOptions<JiraProcessingOptions> optionsAccessor,
    IOptions<ProcessingServiceOptions>? processingOptionsAccessor = null)
    : IAuthoringRunLifecycleAdapter
{
    private const string ProvenanceSource = "jira";

    private readonly JiraProcessingOptions _options = optionsAccessor.Value;
    private readonly int _maxActiveRuns =
        processingOptionsAccessor?.Value.MaxActiveAuthoringRuns ?? int.MaxValue;

    public string ProcessorKind => $"jira-{_options.SourceTicketShape.ToLowerInvariant()}";

    public Task EnsureActiveRunCapacityAvailableAsync(CancellationToken ct = default)
        => authoringStore.EnsureActiveRunCapacityAvailableAsync(
            ProcessorKind,
            _maxActiveRuns,
            ct);

    public async Task<JiraAuthoringRunCreation?> CreateScheduledRunAsync(
        int maxItems = 1000,
        bool databaseOnly = false,
        CancellationToken ct = default)
    {
        await EnsureActiveRunCapacityAvailableAsync(ct);
        ResolvedJiraProcessingFilters filters = filterResolver.Resolve(_options);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IReadOnlyList<JiraProcessingSourceTicketRecord> candidates =
                await sourceStore.GetAuthoringCandidatesAsync(filters, maxItems, ct);
            if (candidates.Count == 0)
            {
                return null;
            }

            try
            {
                return await CreateRunAsync(candidates, databaseOnly, ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && attempt < 2)
            {
            }
        }

        return null;
    }

    public async Task<JiraAuthoringRunCreation> CreateOneItemRunAsync(
        JiraProcessingSourceTicketRecord ticket,
        bool databaseOnly = true,
        CancellationToken ct = default)
    {
        JiraAuthoringRunCreation? existing =
            await GetExistingRunForTicketAsync(ticket, ct);
        if (existing is not null)
        {
            return existing;
        }

        try
        {
            return await CreateRunAsync([ticket], databaseOnly, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            existing = await GetExistingRunForTicketAsync(ticket, ct);
            if (existing is not null)
            {
                return existing;
            }
            throw;
        }
    }

    public async Task<JiraAuthoringRunCreation> CreateExplicitRunAsync(
        IReadOnlyCollection<JiraProcessingSourceTicketRecord> tickets,
        bool databaseOnly,
        CancellationToken ct = default)
    {
        JiraAuthoringRunCreation? existing = await ResolveExactReplayAsync(
            tickets,
            databaseOnly,
            ct);
        if (existing is not null)
        {
            return existing;
        }

        try
        {
            return await CreateRunAsync(tickets, databaseOnly, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            existing = await ResolveExactReplayAsync(
                tickets,
                databaseOnly,
                ct);
            if (existing is not null)
            {
                return existing;
            }
            IReadOnlyList<string> conflictingRunIds =
                await GetConflictingRunIdsAsync(tickets, ct);
            throw new AuthoringConflictException(
                AuthoringConflictCode.RevisionAlreadyScheduled,
                "One or more Jira source revisions are already scheduled in a different authoring run.",
                conflictingRunIds);
        }
    }

    public async Task<JiraAuthoringRunCreation> CreateRunAsync(
        IReadOnlyCollection<JiraProcessingSourceTicketRecord> tickets,
        bool databaseOnly,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        if (tickets.Count == 0)
        {
            throw new ArgumentException("An authoring run requires at least one Jira ticket.", nameof(tickets));
        }

        JiraProcessingSourceTicketRecord[] distinctTickets = tickets
            .GroupBy(ticket => ticket.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        AuthoringRunItemDefinition[] definitions = distinctTickets
            .Select(ticket => new AuthoringRunItemDefinition(
                ticket.Key,
                ticket.SourceTicketShape,
                JiraProcessingSourceTicketStore.GetSourceRevision(ticket)))
            .ToArray();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        AuthoringRunRecord run = await authoringStore.CreateRunAsync(
            ProcessorKind,
            definitions,
            databaseOnly,
            now: createdAt,
            ct: ct,
            requestJson: JsonSerializer.Serialize(
                new JiraAuthoringRunRequestSnapshot(
                    definitions.Select(definition => definition.BusinessKey).ToArray(),
                    databaseOnly),
                JsonSerializerOptions.Web),
            maxActiveRuns: _maxActiveRuns,
            inputProvenance:
            [
                CreateInputProvenance(distinctTickets),
            ]);
        return new JiraAuthoringRunCreation(
            run,
            await authoringStore.GetRunItemsAsync(run.Id, ct));
    }

    public async Task<AuthoringRunReconciliationResult> ReconcileRunAsync(
        AuthoringRunRecord run,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!string.Equals(run.ProcessorKind, ProcessorKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Run '{run.Id}' belongs to processor '{run.ProcessorKind}', not '{ProcessorKind}'.");
        }

        AuthoringProcessorModeRecord mode =
            await authoringStore.GetProcessorModeAsync(ProcessorKind, ct);
        bool initialRevalidation = mode.RevalidationRequired &&
            string.Equals(mode.RevalidationRunId, run.Id, StringComparison.Ordinal);
        if (!await SupersedeStaleItemsAsync(run.Id, ct))
        {
            return AuthoringRunReconciliationResult.Current;
        }

        if (initialRevalidation)
        {
            AuthoringProcessorModeRecord refreshed =
                await authoringStore.GetProcessorModeAsync(ProcessorKind, ct);
            return new AuthoringRunReconciliationResult(
                AuthoringRunReconciliationOutcome.Replaced,
                refreshed.RevalidationRunId);
        }

        await CreateScheduledRunAsync(ct: ct);
        return new AuthoringRunReconciliationResult(
            AuthoringRunReconciliationOutcome.Superseded);
    }

    public async Task<bool> SupersedeStaleItemsAsync(
        string runId,
        CancellationToken ct = default)
    {
        AuthoringProcessorModeRecord mode =
            await authoringStore.GetProcessorModeAsync(ProcessorKind, ct);
        bool initialRevalidation = mode.RevalidationRequired &&
            string.Equals(
                mode.RevalidationRunId,
                runId,
                StringComparison.Ordinal);
        IReadOnlyList<AuthoringRunItemRecord> items = initialRevalidation
            ? await authoringStore.GetRevalidationCorpusItemsAsync(runId, ct)
            : await authoringStore.GetRunItemsAsync(runId, ct);
        List<string> staleItemIds = [];
        foreach (AuthoringRunItemRecord item in items.Where(item =>
                     item.Status != AuthoringStatusValues.Items.Superseded &&
                     (initialRevalidation || item.AcceptedReceiptId is null)))
        {
            JiraProcessingSourceTicketRecord? source = await sourceStore.GetByKeyAsync(
                item.BusinessKey,
                item.ItemKind,
                ct);
            if (source is null ||
                !string.Equals(
                    JiraProcessingSourceTicketStore.GetSourceRevision(source),
                    item.ExpectedSourceRevision,
                    StringComparison.Ordinal))
            {
                staleItemIds.Add(item.Id);
            }
        }
        if (staleItemIds.Count == 0)
        {
            return false;
        }

        if (initialRevalidation)
        {
            HashSet<string> staleIds = staleItemIds.ToHashSet(
                StringComparer.Ordinal);
            bool hasUnfinishedAcceptedCurrentItem = items.Any(item =>
                item.Status != AuthoringStatusValues.Items.Superseded &&
                item.Status != AuthoringStatusValues.Items.Complete &&
                item.AcceptedReceiptId is not null &&
                !staleIds.Contains(item.Id));
            if (hasUnfinishedAcceptedCurrentItem)
            {
                return false;
            }

            List<JiraProcessingSourceTicketRecord> replacementTickets = [];
            foreach (AuthoringRunItemRecord item in items.Where(item =>
                         item.Status != AuthoringStatusValues.Items.Superseded &&
                         (item.AcceptedReceiptId is null ||
                          staleIds.Contains(item.Id))))
            {
                JiraProcessingSourceTicketRecord? source =
                    await sourceStore.GetByKeyAsync(
                        item.BusinessKey,
                        item.ItemKind,
                        ct);
                if (source is null)
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.SourceRevisionMismatch,
                        $"Jira source ticket '{item.BusinessKey}' disappeared during initial revalidation.");
                }
                replacementTickets.Add(source);
            }
            AuthoringRunItemDefinition[] replacements = replacementTickets
                .Select(source => new AuthoringRunItemDefinition(
                    source.Key,
                    source.SourceTicketShape,
                    JiraProcessingSourceTicketStore.GetSourceRevision(source)))
                .ToArray();
            DateTimeOffset createdAt = DateTimeOffset.UtcNow;
            await authoringStore.ReplaceRevalidationRunAsync(
                ProcessorKind,
                runId,
                replacements,
                now: createdAt,
                ct: ct,
                inputProvenance:
                [
                    CreateInputProvenance(replacementTickets),
                ]);
            return true;
        }

        return await authoringStore.SupersedeRunItemsAsync(
            runId,
            staleItemIds,
            "A newer Jira source revision replaced one or more frozen run items.",
            ct: ct);
    }

    private async Task<IReadOnlyList<string>> GetConflictingRunIdsAsync(
        IReadOnlyCollection<JiraProcessingSourceTicketRecord> tickets,
        CancellationToken ct)
    {
        JiraProcessingSourceTicketRecord[] distinct = tickets
            .GroupBy(ticket => ticket.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        List<string> runIds = [];
        HashSet<string> seenRunIds = new(StringComparer.Ordinal);
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = sourceStore.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(ct);
        foreach (JiraProcessingSourceTicketRecord ticket in distinct)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT i.RunId
                FROM authoring_run_items i
                INNER JOIN authoring_runs r ON r.Id = i.RunId
                WHERE i.BusinessKey COLLATE NOCASE = @businessKey
                  AND i.ItemKind COLLATE NOCASE = @itemKind
                  AND i.ExpectedSourceRevision = @sourceRevision
                  AND i.Status <> @superseded
                  AND r.Status <> @runSuperseded
                ORDER BY r.CreatedAt DESC, r.RowId DESC
                """;
            command.Parameters.AddWithValue("@businessKey", ticket.Key);
            command.Parameters.AddWithValue("@itemKind", ticket.SourceTicketShape);
            command.Parameters.AddWithValue(
                "@sourceRevision",
                JiraProcessingSourceTicketStore.GetSourceRevision(ticket));
            command.Parameters.AddWithValue(
                "@superseded",
                AuthoringStatusValues.Items.Superseded);
            command.Parameters.AddWithValue(
                "@runSuperseded",
                AuthoringStatusValues.Runs.Superseded);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string runId = reader.GetString(0);
                if (seenRunIds.Add(runId))
                {
                    runIds.Add(runId);
                }
            }
        }
        return runIds;
    }

    private async Task<JiraAuthoringRunCreation?> ResolveExactReplayAsync(
        IReadOnlyCollection<JiraProcessingSourceTicketRecord> tickets,
        bool databaseOnly,
        CancellationToken ct)
    {
        JiraProcessingSourceTicketRecord[] distinct = tickets
            .GroupBy(ticket => ticket.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        List<(string RunId, string ItemId)> matches = [];
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = sourceStore.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(ct);
        foreach (JiraProcessingSourceTicketRecord ticket in distinct)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT i.RunId, i.Id
                FROM authoring_run_items i
                INNER JOIN authoring_runs r ON r.Id = i.RunId
                WHERE i.BusinessKey COLLATE NOCASE = @businessKey
                  AND i.ItemKind COLLATE NOCASE = @itemKind
                  AND i.ExpectedSourceRevision = @sourceRevision
                  AND i.Status <> @superseded
                  AND r.Status <> @runSuperseded
                ORDER BY r.RowId DESC
                LIMIT 1
                """;
            command.Parameters.AddWithValue("@businessKey", ticket.Key);
            command.Parameters.AddWithValue("@itemKind", ticket.SourceTicketShape);
            command.Parameters.AddWithValue(
                "@sourceRevision",
                JiraProcessingSourceTicketStore.GetSourceRevision(ticket));
            command.Parameters.AddWithValue(
                "@superseded",
                AuthoringStatusValues.Items.Superseded);
            command.Parameters.AddWithValue(
                "@runSuperseded",
                AuthoringStatusValues.Runs.Superseded);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            matches.Add((reader.GetString(0), reader.GetString(1)));
        }

        string[] runIds = matches.Select(match => match.RunId).Distinct(StringComparer.Ordinal).ToArray();
        if (runIds.Length != 1)
        {
            return null;
        }
        string runId = runIds[0];
        AuthoringRunRecord? run = await authoringStore.GetRunAsync(runId, ct);
        IReadOnlyList<AuthoringRunItemRecord> items = await authoringStore.GetRunItemsAsync(runId, ct);
        if (run is null ||
            run.DatabaseOnly != databaseOnly ||
            items.Count != distinct.Length ||
            matches.Any(match => items.All(item => item.Id != match.ItemId)))
        {
            return null;
        }

        return new JiraAuthoringRunCreation(run, items);
    }

    private async Task<JiraAuthoringRunCreation?> GetExistingRunForTicketAsync(
        JiraProcessingSourceTicketRecord ticket,
        CancellationToken ct)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = sourceStore.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.RunId
            FROM authoring_run_items i
            INNER JOIN authoring_runs r ON r.Id = i.RunId
            WHERE i.BusinessKey COLLATE NOCASE = @businessKey
              AND i.ItemKind COLLATE NOCASE = @itemKind
              AND i.ExpectedSourceRevision = @sourceRevision
              AND i.Status <> @superseded
              AND r.Status <> @runSuperseded
            ORDER BY r.RowId DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@businessKey", ticket.Key);
        command.Parameters.AddWithValue("@itemKind", ticket.SourceTicketShape);
        command.Parameters.AddWithValue(
            "@sourceRevision",
            JiraProcessingSourceTicketStore.GetSourceRevision(ticket));
        command.Parameters.AddWithValue(
            "@superseded",
            AuthoringStatusValues.Items.Superseded);
        command.Parameters.AddWithValue(
            "@runSuperseded",
            AuthoringStatusValues.Runs.Superseded);
        string? runId = (string?)await command.ExecuteScalarAsync(ct);
        if (runId is null)
        {
            return null;
        }

        AuthoringRunRecord? run = await authoringStore.GetRunAsync(runId, ct);
        return run is null
            ? null
            : new JiraAuthoringRunCreation(
                run,
                await authoringStore.GetRunItemsAsync(runId, ct));
    }

    private static AuthoringRunInputProvenanceDefinition CreateInputProvenance(
        IReadOnlyCollection<JiraProcessingSourceTicketRecord> tickets)
    {
        DateTimeOffset? latestSuccessfulRefreshAt =
            tickets.All(ticket =>
                ticket.SourceProjectLastSuccessfulRefreshAt is not null)
                ? tickets.Max(ticket =>
                    ticket.SourceProjectLastSuccessfulRefreshAt!.Value)
                : null;

        long? contentRevision = null;
        if (tickets.All(ticket => ticket.SourceContentRevision is not null))
        {
            long candidate = tickets.First().SourceContentRevision!.Value;
            if (tickets.All(ticket =>
                    ticket.SourceContentRevision == candidate))
            {
                contentRevision = candidate;
            }
        }

        return new AuthoringRunInputProvenanceDefinition(
            ProvenanceSource,
            latestSuccessfulRefreshAt,
            contentRevision);
    }

}
