using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
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

public sealed class JiraAuthoringRunCoordinator(
    AuthoringRunStore authoringStore,
    JiraProcessingSourceTicketStore sourceStore,
    JiraProcessingFilterResolver filterResolver,
    IOptions<JiraProcessingOptions> optionsAccessor)
{
    private readonly JiraProcessingOptions _options = optionsAccessor.Value;

    public string ProcessorKind => $"jira-{_options.SourceTicketShape.ToLowerInvariant()}";

    public async Task<JiraAuthoringRunCreation?> CreateScheduledRunAsync(
        int maxItems = 1000,
        bool databaseOnly = false,
        CancellationToken ct = default)
    {
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
        try
        {
            return await CreateRunAsync([ticket], databaseOnly, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            JiraAuthoringRunCreation? existing = await GetExistingRunForTicketAsync(ticket, ct);
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
        try
        {
            return await CreateRunAsync(tickets, databaseOnly, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            JiraAuthoringRunCreation? existing = await ResolveExactReplayAsync(
                tickets,
                databaseOnly,
                ct);
            if (existing is not null)
            {
                return existing;
            }
            throw new AuthoringConflictException(
                AuthoringConflictCode.RevisionAlreadyScheduled,
                "One or more Jira source revisions are already scheduled in a different authoring run.");
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

        AuthoringRunItemDefinition[] definitions = tickets
            .GroupBy(ticket => ticket.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(ticket => new AuthoringRunItemDefinition(
                ticket.Key,
                ticket.SourceTicketShape,
                JiraProcessingSourceTicketStore.GetSourceRevision(ticket)))
            .ToArray();
        AuthoringRunRecord run = await authoringStore.CreateRunAsync(
            ProcessorKind,
            definitions,
            databaseOnly,
            ct: ct);
        await TryActivateNextQueuedRunAsync(ct);
        return new JiraAuthoringRunCreation(
            (await authoringStore.GetRunAsync(run.Id, ct))!,
            await authoringStore.GetRunItemsAsync(run.Id, ct));
    }

    public async Task<bool> TryActivateNextQueuedRunAsync(CancellationToken ct = default)
    {
        AuthoringProcessorModeRecord mode = await authoringStore.GetProcessorModeAsync(ProcessorKind, ct);
        if (!string.Equals(mode.Mode, AuthoringStatusValues.ProcessorModes.RunBacked, StringComparison.Ordinal))
        {
            return false;
        }

        while (true)
        {
            string? runId = await GetOldestQueuedRunIdAsync(ct);
            if (runId is null)
            {
                return false;
            }
            bool wholeRunSuperseded = await SupersedeStaleItemsAsync(runId, ct);
            if (wholeRunSuperseded)
            {
                continue;
            }

            return await authoringStore.TryAcquireMutationFenceAsync(
                ProcessorKind,
                runId,
                ct: ct);
        }
    }

    public async Task<bool> SupersedeStaleItemsAsync(
        string runId,
        CancellationToken ct = default)
    {
        IReadOnlyList<AuthoringRunItemRecord> items = await authoringStore.GetRunItemsAsync(runId, ct);
        List<string> staleItemIds = [];
        foreach (AuthoringRunItemRecord item in items.Where(item =>
                     item.AcceptedReceiptId is null &&
                     item.Status is not (AuthoringStatusValues.Items.Complete or AuthoringStatusValues.Items.Superseded)))
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
        return staleItemIds.Count > 0 &&
            await authoringStore.SupersedeRunItemsAsync(
                runId,
                staleItemIds,
                "A newer Jira source revision replaced one or more frozen run items.",
                ct: ct);
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
                SELECT RunId, Id
                FROM authoring_run_items
                WHERE BusinessKey COLLATE NOCASE = @businessKey
                  AND ItemKind COLLATE NOCASE = @itemKind
                  AND ExpectedSourceRevision = @sourceRevision
                LIMIT 1
                """;
            command.Parameters.AddWithValue("@businessKey", ticket.Key);
            command.Parameters.AddWithValue("@itemKind", ticket.SourceTicketShape);
            command.Parameters.AddWithValue(
                "@sourceRevision",
                JiraProcessingSourceTicketStore.GetSourceRevision(ticket));
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
            SELECT RunId
            FROM authoring_run_items
            WHERE BusinessKey COLLATE NOCASE = @businessKey
              AND ItemKind COLLATE NOCASE = @itemKind
              AND ExpectedSourceRevision = @sourceRevision
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@businessKey", ticket.Key);
        command.Parameters.AddWithValue("@itemKind", ticket.SourceTicketShape);
        command.Parameters.AddWithValue(
            "@sourceRevision",
            JiraProcessingSourceTicketStore.GetSourceRevision(ticket));
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

    private async Task<string?> GetOldestQueuedRunIdAsync(CancellationToken ct)
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
            SELECT Id
            FROM authoring_runs
            WHERE ProcessorKind = @processorKind AND Status = @status
            ORDER BY CreatedAt, RowId
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@processorKind", ProcessorKind);
        command.Parameters.AddWithValue("@status", AuthoringStatusValues.Runs.Queued);
        return (string?)await command.ExecuteScalarAsync(ct);
    }
}
