using System.Globalization;
using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketPublicationReconciliationException(
    string failureCode,
    string detail,
    IReadOnlyList<string>? ticketKeys = null)
    : InvalidOperationException(detail)
{
    public string FailureCode { get; } = failureCode;
    public IReadOnlyList<string> TicketKeys { get; } = ticketKeys ?? [];
}

public sealed class PreparedTicketPublicationReconciliationPlanner(
    PreparedTicketPublicationBaselineReader baselineReader,
    OrchestratorHydrationFetcher fetcher,
    PreparerDatabase database,
    AuthoringRunControlService runControlService,
    JiraAuthoringRunCoordinator coordinator,
    AuthoringRunSchedulerWakeSignal wakeSignal)
{
    public async Task<PreparedTicketPublicationReconciliationStartResult>
        StartAsync(
            string sourceRunId,
            CancellationToken ct = default)
    {
        PreparedTicketPublicationBaseline baseline;
        try
        {
            baseline = await baselineReader.ReadAsync(sourceRunId, ct);
        }
        catch (PreparedTicketPublicationProtectionException ex)
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .InvalidBaseline,
                ex.Message);
        }
        PreparedTicketPublicationProtectedInventory currentInventory =
            await ReadMatchingCurrentInventoryAsync(baseline, ct);

        Observation observation =
            await ObserveAsync(baseline.Inventory.Corpus, ct);
        PreparedTicketPublicationReconciliationComparison comparison =
            CreateComparison(baseline, currentInventory, observation);
        AuthoringRunRecord run =
            await database.CreatePublicationReconciliationAsync(
                comparison,
                ct: ct,
                validateFrozenObservation:
                    async (connection, cancellationToken) =>
                    {
                        _ = await ReadMatchingCurrentInventoryAsync(
                            baseline,
                            connection,
                            cancellationToken);
                        Observation repeated = await ObserveAsync(
                            baseline.Inventory.Corpus,
                            cancellationToken);
                        EnsureObservationUnchanged(observation, repeated);
                    });
        wakeSignal.Signal();
        AuthoringRunControlStatus status = await runControlService
            .GetStatusAsync(coordinator.ProcessorKind, run.Id, ct);
        return new(
            status.Run,
            status.Items,
            comparison,
            Counts(comparison, invalidatedCount: 0));
    }

    public async Task<PreparedTicketPublicationReconciliationStatusResult>
        GetStatusAsync(string runId, CancellationToken ct = default)
    {
        AuthoringRunControlStatus status = await runControlService
            .GetStatusAsync(coordinator.ProcessorKind, runId, ct);
        if (status.Run.Purpose !=
            PreparedTicketPublicationReconciliationContract.Purpose)
        {
            throw new KeyNotFoundException(
                $"Authoring run '{runId}' is not a publication reconciliation.");
        }
        PreparedTicketPublicationReconciliationComparison comparison =
            await database.GetPublicationReconciliationComparisonAsync(
                runId,
                ct)
            ?? throw new KeyNotFoundException(
                $"Publication reconciliation '{runId}' was not found.");
        IReadOnlyList<string> invalidated =
            await FindInvalidatedTicketsAsync(comparison, ct);
        (
            IReadOnlyList<PreparedTicketPublicationReconciliationGroupingImpact>
                impacts,
            PreparedTicketPublicationReconciliationPromotionStatus promotion,
            PreparedTicketPublicationReconciliationProof? proof) =
                await ReadPersistenceStatusAsync(runId, ct);
        return new(
            status.Run,
            status.Items,
            comparison,
            Counts(comparison, invalidated.Count),
            impacts,
            promotion,
            invalidated,
            proof,
            invalidated.Count == 0
                ? null
                : PreparedTicketPublicationReconciliationFailureCodes
                    .RevisionInvalidation,
            invalidated.Count == 0
                ? null
                : CreateRevisionInvalidationDetail(
                    comparison,
                    invalidated));
    }

    public async Task EnsureFrozenCorpusCurrentAsync(
        string runId,
        CancellationToken ct = default)
    {
        PreparedTicketPublicationReconciliationComparison comparison =
            await database.GetPublicationReconciliationComparisonAsync(
                runId,
                ct)
            ?? throw new KeyNotFoundException(
                $"Publication reconciliation '{runId}' was not found.");
        if (comparison.ContractVersion !=
            PreparedTicketPublicationReconciliationContract.CurrentVersion)
        {
            throw new NotSupportedException(
                $"Reconciliation contract version {comparison.ContractVersion} cannot be revalidated.");
        }
        IReadOnlyList<string> invalidated =
            await FindInvalidatedTicketsAsync(comparison, ct);
        if (invalidated.Count != 0)
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .RevisionInvalidation,
                CreateRevisionInvalidationDetail(
                    comparison,
                    invalidated),
                invalidated);
        }
    }

    private async Task<Observation> ObserveAsync(
        IReadOnlyList<PreparedTicketPublicationCorpusItem> corpus,
        CancellationToken ct)
    {
        List<ObservedTicket> tickets = new(corpus.Count);
        List<string> failures = [];
        foreach (PreparedTicketPublicationCorpusItem item in corpus)
        {
            PublicationMetadataFetchResult result =
                await fetcher.FetchPublicationMetadataAsync(
                    item.TicketKey,
                    DateTimeOffset.UtcNow,
                    ct);
            if (!result.IsSuccess ||
                result.SourceIsStable != true ||
                result.SourceContentRevision is null ||
                string.IsNullOrWhiteSpace(result.ObservedSourceRevision))
            {
                failures.Add(
                    $"{item.TicketKey}: {result.Failure?.Detail ?? "incomplete stable source provenance"}");
                continue;
            }
            tickets.Add(new(
                item.TicketKey,
                AuthoringSourceRevision.CanonicalizeTimestamp(
                    result.ObservedSourceRevision),
                result.SourceContentRevision.Value));
        }
        if (failures.Count != 0)
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .UnstableJiraGeneration,
                string.Join("; ", failures),
                failures.Select(value => value.Split(':', 2)[0]).ToArray());
        }
        long[] generations = tickets
            .Select(ticket => ticket.Generation)
            .Distinct()
            .ToArray();
        if (generations.Length != 1)
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .UnstableJiraGeneration,
                "The complete accepted Jira corpus was not read from one stable content generation.",
                tickets.Select(ticket => ticket.TicketKey).ToArray());
        }
        return new(
            generations[0].ToString(CultureInfo.InvariantCulture),
            tickets);
    }

    private static PreparedTicketPublicationReconciliationComparison
        CreateComparison(
            PreparedTicketPublicationBaseline baseline,
            PreparedTicketPublicationProtectedInventory currentInventory,
            Observation observation)
    {
        Dictionary<string, ObservedTicket> current = observation.Tickets
            .ToDictionary(value => value.TicketKey,
                StringComparer.OrdinalIgnoreCase);
        PreparedTicketPublicationReconciliationItemDecision[] decisions =
            baseline.Inventory.Corpus.Select(item =>
            {
                ObservedTicket observed = current[item.TicketKey];
                PreparedTicketPublicationProtectedRow state =
                    currentInventory.Rows.Single(row =>
                        row.Table == "prepared_ticket_authoring_state" &&
                        string.Equals(
                            row.Scope,
                            item.TicketKey,
                            StringComparison.OrdinalIgnoreCase));
                string authoredFingerprint = state.Values.Single(value =>
                    value.Column == "GraphHash").Value
                    ?? throw new InvalidOperationException(
                        $"Accepted graph '{item.TicketKey}' has no fingerprint.");
                PreparedTicketPublicationProtectedGrouping grouping =
                    baseline.Inventory.Grouping.Single(partition =>
                        partition.Corpus.Any(candidate =>
                            string.Equals(
                                candidate.TicketKey,
                                item.TicketKey,
                                StringComparison.OrdinalIgnoreCase)));
                bool carryForward = JiraSourceRevision.AreEquivalent(
                    item.ExpectedSourceRevision,
                    observed.Revision);
                return new PreparedTicketPublicationReconciliationItemDecision(
                    item.TicketKey,
                    carryForward
                        ? PreparedTicketPublicationReconciliationDispositionValues
                            .CarryForward
                        : PreparedTicketPublicationReconciliationDispositionValues
                            .ReAuthor,
                    item.ExpectedSourceRevision,
                    observed.Revision,
                    item.ReceiptId,
                    item.RunItemId,
                    item.ContributingRunId,
                    authoredFingerprint,
                    grouping.Fingerprint.OutputFingerprint,
                    carryForward ? item.ItemKind : "fhir",
                    carryForward
                        ? item.ExpectedSourceRevision
                        : observed.Revision);
            })
            .OrderBy(item => item.TicketKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TicketKey, StringComparer.Ordinal)
            .ToArray();
        return new(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            baseline.Source.RunId,
            baseline.Source.SnapshotId,
            baseline.Source.SnapshotSha256,
            observation.Generation,
            DateTimeOffset.UtcNow,
            baseline.Inventory.CorpusFingerprint,
            decisions);
    }

    private async Task<PreparedTicketPublicationProtectedInventory>
        ReadMatchingCurrentInventoryAsync(
            PreparedTicketPublicationBaseline baseline,
            CancellationToken ct)
    {
        await using SqliteConnection connection =
            database.OpenConnection();
        return await ReadMatchingCurrentInventoryAsync(
            baseline,
            connection,
            ct);
    }

    private static async Task<PreparedTicketPublicationProtectedInventory>
        ReadMatchingCurrentInventoryAsync(
            PreparedTicketPublicationBaseline baseline,
            SqliteConnection connection,
            CancellationToken ct)
    {
        try
        {
            PreparedTicketPublicationProtectedInventory current =
                await PreparedTicketPublicationProtectionReader
                    .ReadCurrentAsync(connection, ct);
            _ = PreparedTicketPublicationProtectionReader.Compare(
                baseline,
                current);
            return current;
        }
        catch (PreparedTicketPublicationProtectionException ex)
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .InvalidBaseline,
                ex.Message);
        }
    }

    private static void EnsureObservationUnchanged(
        Observation frozen,
        Observation repeated)
    {
        Dictionary<string, ObservedTicket> repeatedByKey = repeated.Tickets
            .ToDictionary(value => value.TicketKey,
                StringComparer.OrdinalIgnoreCase);
        string[] changed = frozen.Tickets
            .Where(ticket =>
                !repeatedByKey.TryGetValue(
                    ticket.TicketKey,
                    out ObservedTicket? current) ||
                current.Revision != ticket.Revision ||
                current.Generation != ticket.Generation)
            .Select(ticket => ticket.TicketKey)
            .ToArray();
        if (frozen.Generation != repeated.Generation || changed.Length != 0)
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .UnstableJiraGeneration,
                "Jira changed while the reconciliation comparison was being frozen.",
                changed);
        }
    }

    private async Task<IReadOnlyList<string>> FindInvalidatedTicketsAsync(
        PreparedTicketPublicationReconciliationComparison comparison,
        CancellationToken ct)
    {
        List<string> invalidated = [];
        foreach (PreparedTicketPublicationReconciliationItemDecision item in
                 comparison.Items
                     .OrderBy(
                         item => item.TicketKey,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenBy(
                         item => item.TicketKey,
                         StringComparer.Ordinal))
        {
            PublicationMetadataFetchResult result =
                await fetcher.FetchPublicationMetadataAsync(
                    item.TicketKey,
                    DateTimeOffset.UtcNow,
                    ct);
            if (!result.IsSuccess ||
                !string.Equals(
                    result.TicketKey,
                    item.TicketKey,
                    StringComparison.OrdinalIgnoreCase) ||
                result.SourceIsStable != true ||
                result.SourceContentRevision?.ToString(
                    CultureInfo.InvariantCulture) !=
                    comparison.StableJiraGeneration ||
                string.IsNullOrWhiteSpace(result.ObservedSourceRevision) ||
                !JiraSourceRevision.AreEquivalent(
                    item.CurrentSourceRevision,
                    result.ObservedSourceRevision))
            {
                invalidated.Add(item.TicketKey);
            }
        }
        return invalidated;
    }

    private static string CreateRevisionInvalidationDetail(
        PreparedTicketPublicationReconciliationComparison comparison,
        IReadOnlyList<string> invalidated)
        => $"Frozen Jira revisions changed or could not be observed at generation '{comparison.StableJiraGeneration}': {string.Join(", ", invalidated)}";

    private async Task<(
        IReadOnlyList<PreparedTicketPublicationReconciliationGroupingImpact>,
        PreparedTicketPublicationReconciliationPromotionStatus,
        PreparedTicketPublicationReconciliationProof?)>
        ReadPersistenceStatusAsync(string runId, CancellationToken ct)
    {
        await using SqliteConnection connection = database.OpenConnection();
        List<PreparedTicketPublicationReconciliationGroupingImpact> impacts = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT ImpactJson
                FROM prepared_ticket_publication_grouping_impacts
                WHERE RunId = @runId
                ORDER BY PartitionKey
                """;
            command.Parameters.AddWithValue("@runId", runId);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                impacts.Add(JsonSerializer.Deserialize<
                    PreparedTicketPublicationReconciliationGroupingImpact>(
                        reader.GetString(0))
                    ?? throw new InvalidOperationException(
                        $"Reconciliation '{runId}' has an invalid grouping impact."));
            }
        }
        string state;
        string? journalState;
        DateTimeOffset? recoveryAt;
        string? failureCode;
        string? failureDetail;
        DateTimeOffset? abandonedAt;
        string? abandonmentReason;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT reconciliation.PromotionState, journal.State,
                       journal.LastRecoveryAttemptAt, journal.FailureCode,
                       journal.FailureDetail, reconciliation.AbandonedAt,
                       reconciliation.AbandonmentReason
                FROM prepared_ticket_publication_reconciliations reconciliation
                LEFT JOIN prepared_ticket_publication_reconciliation_journal journal
                  ON journal.RunId = reconciliation.RunId
                WHERE reconciliation.RunId = @runId
                """;
            command.Parameters.AddWithValue("@runId", runId);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new KeyNotFoundException(
                    $"Publication reconciliation '{runId}' was not found.");
            }
            state = reader.GetString(0);
            journalState = reader.IsDBNull(1) ? null : reader.GetString(1);
            recoveryAt = reader.IsDBNull(2)
                ? null : reader.GetDateTimeOffset(2);
            failureCode = reader.IsDBNull(3) ? null : reader.GetString(3);
            failureDetail = reader.IsDBNull(4) ? null : reader.GetString(4);
            abandonedAt = reader.IsDBNull(5)
                ? null : reader.GetDateTimeOffset(5);
            abandonmentReason =
                reader.IsDBNull(6) ? null : reader.GetString(6);
        }
        bool fenceHeld;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM prepared_ticket_publication_reconciliation_fences
                    WHERE RunId = @runId)
                """;
            command.Parameters.AddWithValue("@runId", runId);
            fenceHeld = Convert.ToBoolean(
                await command.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture);
        }
        PreparedTicketPublicationReconciliationProof? proof = null;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT ProofJson
                FROM prepared_ticket_publication_reconciliation_proofs
                WHERE RunId = @runId
                """;
            command.Parameters.AddWithValue("@runId", runId);
            string? json = (string?)await command.ExecuteScalarAsync(ct);
            if (json is not null)
            {
                proof = JsonSerializer.Deserialize<
                    PreparedTicketPublicationReconciliationProof>(json);
            }
        }
        return (
            impacts,
            new(
                state,
                journalState,
                fenceHeld,
                recoveryAt,
                failureCode,
                failureDetail,
                abandonedAt,
                abandonmentReason),
            proof);
    }

    private static AuthoringRunReconciliationCounts Counts(
        PreparedTicketPublicationReconciliationComparison comparison,
        int invalidatedCount)
        => new(
            comparison.Items.Count,
            comparison.Items.Count(item =>
                item.Disposition ==
                PreparedTicketPublicationReconciliationDispositionValues
                    .CarryForward),
            comparison.Items.Count(item =>
                item.Disposition ==
                PreparedTicketPublicationReconciliationDispositionValues
                    .ReAuthor),
            invalidatedCount);

    private sealed record ObservedTicket(
        string TicketKey,
        string Revision,
        long Generation);

    private sealed record Observation(
        string Generation,
        IReadOnlyList<ObservedTicket> Tickets);
}
