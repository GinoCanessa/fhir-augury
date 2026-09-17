using System.Globalization;
using System.Text.Json;
using FhirAugury.Common.Text;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketSnapshotMaterializer(
    PreparerDatabase database,
    AuthoringRunStore authoringStore,
    SqliteReviewSnapshotReconciler snapshotReconciler,
    IOptions<PreparerServiceOptions> optionsAccessor)
{
    private readonly PreparerServiceOptions _options = optionsAccessor.Value;

    public async Task<PreparedTicketPublicationCandidateSnapshot>
        MaterializeReconciliationCandidateAsync(
            AuthoringRunRecord run,
            PreparedTicketPublicationReconciliationProof proof,
            AuthoringSnapshotSchemaCatalog? snapshotSchema = null,
            CancellationToken ct = default)
    {
        PreparedTicketPublicationCandidateSnapshot candidate =
            await MaterializeProvisionalReconciliationCandidateAsync(
                run,
                proof,
                snapshotSchema,
                ct);
        await PersistTrustedReconciliationCandidateAsync(
            candidate,
            proof,
            ct);
        return candidate;
    }

    public async Task<PreparedTicketPublicationCandidateSnapshot>
        MaterializeProvisionalReconciliationCandidateAsync(
            AuthoringRunRecord run,
            PreparedTicketPublicationReconciliationProof proof,
            AuthoringSnapshotSchemaCatalog? snapshotSchema = null,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.ContractVersion !=
            PreparedTicketPublicationReconciliationContract.CurrentVersion)
        {
            throw new NotSupportedException(
                $"Reconciliation proof contract version {proof.ContractVersion} cannot be materialized.");
        }
        snapshotSchema ??= PreparedTicketSnapshotSchemaResolver.Resolve(
            _options.SnapshotSchemaVersion);
        if (!string.Equals(
                run.Purpose,
                PreparedTicketPublicationContract
                    .PublicationReconciliationPurpose,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.Purpose,
                PreparedTicketPublicationContract
                    .PublicationReconciliationPurpose,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.SourceRunId,
                run.SourceRunId,
                StringComparison.Ordinal) ||
            snapshotSchema.Version != PreparedTicketSnapshotSchemaV3.Version)
        {
            throw new InvalidOperationException(
                $"Run '{run.Id}' does not have a valid schema-v3 reconciliation materialization context.");
        }

        PreparedTicketPublicationReconciliationComparison comparison =
            await database.GetPublicationReconciliationComparisonAsync(
                run.Id,
                ct)
            ?? throw new InvalidOperationException(
                $"Run '{run.Id}' has no reconciliation comparison.");
        if (comparison.ContractVersion !=
            PreparedTicketPublicationReconciliationContract.CurrentVersion)
        {
            throw new NotSupportedException(
                $"Reconciliation contract version {comparison.ContractVersion} cannot be materialized.");
        }
        PreparedTicketPublicationGroupingDelta delta =
            await database
                .PreparePublicationReconciliationGroupingDeltaAsync(
                    run.Id,
                    ct);
        if (delta.Impacts.Any(impact => !impact.Complete))
        {
            throw new InvalidOperationException(
                "The complete grouping-impact closure has not been staged.");
        }
        await database.ValidatePublicationReconciliationUnaffectedAsync(
            run.Id,
            ct);
        string impactFingerprint = PreparedTicketPublicationContract
            .ComputeGroupingImpactFingerprint(delta.Impacts);
        if (!string.Equals(
                proof.SourceSnapshotId,
                comparison.SourceSnapshotId,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.StableJiraGeneration,
                comparison.StableJiraGeneration,
                StringComparison.Ordinal) ||
            proof.AcceptedTicketCount != comparison.Items.Count ||
            proof.CarryForwardTicketCount != comparison.Items.Count(item =>
                item.Disposition ==
                PreparedTicketPublicationReconciliationDispositionValues
                    .CarryForward) ||
            proof.ReAuthorTicketCount != comparison.Items.Count(item =>
                item.Disposition ==
                PreparedTicketPublicationReconciliationDispositionValues
                    .ReAuthor) ||
            !string.Equals(
                proof.CorpusFingerprint,
                delta.OverlayCorpusFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                proof.GroupingImpactFingerprint,
                impactFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The reconciliation proof does not match the verified overlay.");
        }

        string groupingFingerprint = AuthoringResultHasher.HashNormalizedUtf8(
            string.Join(
                "\n",
                delta.Unaffected.GroupingRowsFingerprint,
                delta.Impacts.OrderBy(
                        impact => impact.PartitionKey,
                        StringComparer.Ordinal)
                    .Select(impact =>
                        $"{impact.PartitionKey}:{impact.StagedOutputFingerprint}")));
        string directory = Path.GetFullPath(_options.SnapshotDirectory);
        Directory.CreateDirectory(directory);
        string safeProcessor = string.Concat(
            run.ProcessorKind.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '-'));
        string temporaryPath = Path.Combine(
            directory,
            $"{safeProcessor}-{run.Id}.reconciliation.tmp");
        string finalPath = Path.Combine(
            directory,
            $"{safeProcessor}-{run.Id}.db");
        PreparerDatabase.PublicationReconciliationSnapshotReservation
            reservation =
                await database.ReservePublicationReconciliationSnapshotAsync(
                    run.Id,
                    temporaryPath,
                    finalPath,
                    snapshotSchema.Version,
                    ct);
        if (reservation.Candidate is not null)
        {
            PreparedTicketPublicationCandidateSnapshot existing =
                reservation.Candidate;
            EnsureCandidateMatchesReservation(existing, reservation);
            if (!string.Equals(
                    existing.OverlayCorpusFingerprint,
                    delta.OverlayCorpusFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    existing.GroupingFingerprint,
                    groupingFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    existing.GroupingImpactFingerprint,
                    impactFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The reserved reconciliation candidate no longer matches the verified overlay.");
            }
            await ValidateCandidateSnapshotAsync(existing, ct);
            return existing;
        }

        DeleteCandidateArtifacts(temporaryPath);
        IReadOnlyDictionary<string, long> counts;
        await using (SqliteConnection source = database.OpenConnection())
        await using (SqliteConnection destination = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString()))
        {
            await destination.OpenAsync(ct);
            source.BackupDatabase(destination);
            await database.ApplyPublicationReconciliationOverlayAsync(
                destination,
                run.Id,
                ct);
            await CreateSnapshotProvenanceTableAsync(destination, ct);
            PreparedTicketSnapshotSanitizer sanitizer = new(
                run.Id,
                snapshotSchema,
                comparison.SourceRunId,
                clearSnapshotProvenance: true);
            await sanitizer.SanitizeAsync(destination, ct);
            counts = await ReadTableCountsAsync(
                destination,
                snapshotSchema.CountedTables,
                ct);
            await WriteSnapshotProvenanceAsync(
                destination,
                reservation,
                counts,
                ct);
            await CheckpointAsync(destination, ct);
            await VerifyIntegrityAsync(destination, ct);
            await VerifyTableCountsAsync(destination, counts, ct);
            await VerifySnapshotProvenanceAsync(
                destination,
                reservation,
                counts,
                ct);
        }

        string sha256 = await SqliteReviewSnapshotWriter
            .ComputeSha256Async(temporaryPath, ct);
        PreparedTicketPublicationCandidateSnapshot candidate = new(
            run.Id,
            reservation.SnapshotId,
            reservation.ProcessorKind,
            temporaryPath,
            finalPath,
            snapshotSchema.Version,
            reservation.Sequence,
            reservation.AuthoringEpoch,
            reservation.ItemCount,
            reservation.ReceiptCount,
            sha256,
            new FileInfo(temporaryPath).Length,
            counts,
            delta.OverlayCorpusFingerprint,
            groupingFingerprint,
            impactFingerprint,
            reservation.CreatedAt);
        await ValidateCandidateSnapshotAsync(candidate, ct);
        return candidate;
    }

    public async Task PersistTrustedReconciliationCandidateAsync(
        PreparedTicketPublicationCandidateSnapshot candidate,
        PreparedTicketPublicationReconciliationProof proof,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(proof);
        if (!string.Equals(
                candidate.OverlayCorpusFingerprint,
                proof.CorpusFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.GroupingImpactFingerprint,
                proof.GroupingImpactFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The provisional reconciliation candidate does not match its publication proof.");
        }
        await ValidateCandidateSnapshotAsync(candidate, ct);
        await database.SavePublicationReconciliationCandidateEvidenceAsync(
            candidate,
            proof,
            ct);
    }

    public void DiscardProvisionalReconciliationCandidate(
        PreparedTicketPublicationCandidateSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        DeleteCandidateArtifacts(candidate.TemporaryPath);
    }

    public async Task<AuthoringSnapshotDescriptor> MaterializeAsync(
        AuthoringRunRecord run,
        AuthoringSnapshotSchemaCatalog snapshotSchema,
        AuthoringSnapshotPublicationProof? publicationProof = null,
        IReadOnlyList<PreparedTicketGroupingCertificationEvidence>?
            groupingCertifications = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(snapshotSchema);
        PreparedTicketPublicationEnrichmentInput? enrichmentInput =
            run.Purpose == AuthoringRunPurposeValues.PublicationRefresh
                ? PreparerDatabase.ReadPublicationEnrichmentInput(run) : null;
        if (enrichmentInput is not null)
        {
            if (publicationProof is null)
            {
                throw new PreparedTicketPublicationProtectionException(
                    PreparedTicketPublicationRefreshFailureCodes.InvalidRecipe,
                    "Publication enrichment materialization requires its durable Jira proof.");
            }
            await database.ValidatePublicationEnrichmentAsync(run.Id, enrichmentInput, ct);
        }
        ValidatePublicationContext(
            run,
            snapshotSchema,
            publicationProof,
            groupingCertifications);
        if (publicationProof is not null)
        {
            await ValidateDurablePublicationContextAsync(
                run,
                publicationProof,
                groupingCertifications!,
                enrichmentInput,
                ct);
        }

        AuthoringSnapshotDescriptor? recovered =
            await RecoverExistingSnapshotAsync(
                run,
                snapshotSchema,
                publicationProof,
                ct);
        if (recovered is not null)
        {
            return recovered;
        }

        IReadOnlyList<AuthoringRunItemRecord> items =
            await authoringStore.GetRunItemsAsync(run.Id, ct);
        IReadOnlyDictionary<string, long> counts =
            await database.GetSnapshotTableCountsAsync(
                snapshotSchema.Version,
                ct);
        int receiptCount =
            await database.GetSnapshotReceiptCountAsync(run.Id, ct);
        SqliteReviewSnapshotWriter writer =
            new(database.OpenConnection, authoringStore);
        return await writer.WriteAsync(
            new SqliteReviewSnapshotRequest(
                run.ProcessorKind,
                run.Id,
                Path.GetFullPath(_options.SnapshotDirectory),
                snapshotSchema.Version,
                items.Count(item =>
                    item.Status is
                        AuthoringStatusValues.Items.Complete or
                        AuthoringStatusValues.Items.Superseded),
                receiptCount,
                counts,
                enrichmentInput is null
                    ? new PreparedTicketSnapshotSanitizer(
                        run.Id, snapshotSchema, publicationProof?.SourceRunId, groupingCertifications)
                    : new ProtectedPublicationSnapshotSanitizer(
                        run.Id, snapshotSchema, enrichmentInput, groupingCertifications),
                publicationProof),
            ct);
    }

    private sealed class ProtectedPublicationSnapshotSanitizer(
        string runId,
        AuthoringSnapshotSchemaCatalog catalog,
        PreparedTicketPublicationEnrichmentInput input,
        IReadOnlyList<PreparedTicketGroupingCertificationEvidence>? certifications)
        : AuthoringSnapshotSanitizer(catalog)
    {
        private readonly PreparedTicketSnapshotSanitizer _sanitizer =
            new(runId, catalog, input.Source.RunId, certifications);

        public override async Task SanitizeAsync(SqliteConnection connection, CancellationToken ct = default)
        {
            // Validate the actual backup too, closing the gap between live preflight and SQLite backup.
            await PreparerDatabase.ValidatePublicationEnrichmentAsync(connection, runId, input, ct);
            await _sanitizer.SanitizeAsync(connection, ct);
        }
    }

    private async Task<AuthoringSnapshotDescriptor?>
        RecoverExistingSnapshotAsync(
            AuthoringRunRecord run,
            AuthoringSnapshotSchemaCatalog snapshotSchema,
            AuthoringSnapshotPublicationProof? publicationProof,
            CancellationToken ct)
    {
        IReadOnlyList<AuthoringReviewSnapshotRecord> records =
            await authoringStore.GetSnapshotRecordsAsync(ct);
        AuthoringReviewSnapshotRecord[] recoverable = records
            .Where(record =>
                string.Equals(
                    record.RunId,
                    run.Id,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    record.Status,
                    AuthoringStatusValues.Snapshots.Error,
                    StringComparison.Ordinal))
            .ToArray();
        if (recoverable.Length == 0)
        {
            return null;
        }

        _ = await snapshotReconciler.ReconcileAsync(ct);
        records = await authoringStore.GetSnapshotRecordsAsync(ct);
        AuthoringReviewSnapshotRecord[] ready = records
            .Where(record =>
                string.Equals(
                    record.RunId,
                    run.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    record.Status,
                    AuthoringStatusValues.Snapshots.Ready,
                    StringComparison.Ordinal))
            .OrderByDescending(record => record.Sequence)
            .ToArray();
        if (ready.Length == 0)
        {
            return null;
        }
        if (ready.Length != 1)
        {
            throw new InvalidOperationException(
                $"Run '{run.Id}' has more than one ready snapshot.");
        }

        AuthoringSnapshotDescriptor descriptor =
            await authoringStore.GetSnapshotDescriptorAsync(
                ready[0].Id,
                ct)
            ?? throw new InvalidOperationException(
                $"Ready snapshot '{ready[0].Id}' has no descriptor.");
        if (descriptor.SchemaVersion != snapshotSchema.Version ||
            !string.Equals(
                descriptor.ProcessorKind,
                run.ProcessorKind,
                StringComparison.Ordinal) ||
            !Equals(descriptor.PublicationProof, publicationProof))
        {
            throw new InvalidOperationException(
                $"Recovered snapshot '{descriptor.SnapshotId}' does not match the requested immutable materialization contract.");
        }
        return descriptor;
    }

    private static void ValidatePublicationContext(
        AuthoringRunRecord run,
        AuthoringSnapshotSchemaCatalog snapshotSchema,
        AuthoringSnapshotPublicationProof? publicationProof,
        IReadOnlyList<PreparedTicketGroupingCertificationEvidence>?
            groupingCertifications)
    {
        if (publicationProof is null)
        {
            if (groupingCertifications is not null)
            {
                throw new ArgumentException(
                    "Grouping publication certifications require a publication proof.",
                    nameof(groupingCertifications));
            }
            return;
        }

        PreparedTicketPublicationContract.EnsureSupportedVersion(
            publicationProof.ContractVersion);
        if (snapshotSchema.Version !=
                PreparedTicketSnapshotSchemaV3.Version ||
            !string.Equals(
                run.Purpose,
                AuthoringRunPurposeValues.PublicationRefresh,
                StringComparison.Ordinal) ||
            !string.Equals(
                publicationProof.Purpose,
                PreparedTicketPublicationContract
                    .PublicationRefreshPurpose,
                StringComparison.Ordinal) ||
            !string.Equals(
                publicationProof.SourceRunId,
                run.SourceRunId,
                StringComparison.Ordinal) ||
            !string.Equals(
                publicationProof.SourceName,
                PreparedTicketPublicationContract.JiraSourceName,
                StringComparison.OrdinalIgnoreCase) ||
            publicationProof.SourceLastSuccessfulRefreshAt.Offset !=
                TimeSpan.Zero ||
            publicationProof.CapturedAt.Offset != TimeSpan.Zero ||
            publicationProof.SourceContentRevision < 0 ||
            publicationProof.PublicDisplayNamePolicyVersion !=
                PublicDisplayNamePolicy.CurrentVersion ||
            groupingCertifications is null)
        {
            throw new InvalidOperationException(
                $"Run '{run.Id}' does not have a valid schema-v3 publication materialization context.");
        }

        string groupingFingerprint =
            PreparedTicketPublicationContract.ComputeGroupingFingerprint(
                groupingCertifications.Select(certification =>
                    new PreparedTicketPublicationGroupingPartition(
                        certification.PartitionKey,
                        certification.OutputFingerprint)),
                publicationProof.ContractVersion);
        if (!string.Equals(
                groupingFingerprint,
                publicationProof.GroupingFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The publication proof does not match the certified grouping output.");
        }
    }

    private async Task ValidateDurablePublicationContextAsync(
        AuthoringRunRecord run,
        AuthoringSnapshotPublicationProof publicationProof,
        IReadOnlyList<PreparedTicketGroupingCertificationEvidence>
            groupingCertifications,
        PreparedTicketPublicationEnrichmentInput? enrichmentInput,
        CancellationToken ct)
    {
        if (enrichmentInput is not null)
        {
            string fingerprint = PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(enrichmentInput);
            AuthoringRunStageRecord[] metadataStages = (await authoringStore.GetRunStagesAsync(run.Id, ct))
                .Where(stage => stage.StageName == PreparedTicketPublicationEnrichmentContract.StageName &&
                    stage.PartitionKey == string.Empty && stage.InputFingerprint == fingerprint &&
                    stage.Status == AuthoringStatusValues.Stages.Complete).ToArray();
            if (metadataStages.Length != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    "Publication enrichment has no unique completed stage for its frozen recipe.");
            }
            PreparedTicketPublicationRefreshReceiptRecord? receipt =
                await database.GetMatchingPublicationRefreshReceiptAsync(
                    run.Id, metadataStages[0].Id, fingerprint, enrichmentInput.CorpusFingerprint, ct);
            if (receipt is null || receipt.SourceContentRevision != publicationProof.SourceContentRevision ||
                receipt.PublicDisplayNamePolicyVersion != publicationProof.PublicDisplayNamePolicyVersion ||
                receipt.SourceLastSuccessfulRefreshAt.ToUniversalTime() != publicationProof.SourceLastSuccessfulRefreshAt)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StageFingerprintMismatch,
                    "The publication proof does not match the durable enrichment receipt.");
            }
        }
        PreparedTicketPublicationRefreshInventory inventory =
            await database.GetPublicationRefreshInventoryAsync(ct);
        if (!string.Equals(
                inventory.CorpusFingerprint,
                publicationProof.CorpusFingerprint,
                StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                "The publication proof corpus no longer matches the current accepted receipts.");
        }

        IReadOnlyList<PreparedTicketRunPartition> partitions =
            await database.GetRunPartitionsAsync(run.Id, ct);
        IReadOnlyList<PreparedTicketGroupingCertificationEvidence>
            durableCertifications =
                await database.GetPublicationGroupingCertificationsAsync(
                    run.Id,
                    partitions,
                    ct);
        PreparedTicketGroupingCertificationEvidence[] supplied =
            groupingCertifications
                .OrderBy(
                    certification => certification.PartitionKey,
                    StringComparer.Ordinal)
                .ToArray();
        PreparedTicketGroupingCertificationEvidence[] durable =
            durableCertifications
                .OrderBy(
                    certification => certification.PartitionKey,
                    StringComparer.Ordinal)
                .ToArray();
        if (!supplied.SequenceEqual(durable))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                "The publication proof grouping certifications no longer match durable state.");
        }
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            string result =
                (string?)await command.ExecuteScalarAsync(ct) ?? "unknown";
            if (!string.Equals(
                    result,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Snapshot integrity check failed: {result}");
            }
        }
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException(
                    "Snapshot foreign-key integrity check failed.");
            }
        }
    }

    private static async Task CreateSnapshotProvenanceTableAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            DROP TABLE IF EXISTS authoring_snapshot_provenance;
            CREATE TABLE authoring_snapshot_provenance(
                SnapshotId TEXT NOT NULL,
                ProcessorKind TEXT NOT NULL,
                RunId TEXT NOT NULL,
                AuthoringEpoch INTEGER NOT NULL,
                Sequence INTEGER NOT NULL,
                SchemaVersion INTEGER NOT NULL,
                ItemCount INTEGER NOT NULL,
                ReceiptCount INTEGER NOT NULL,
                TableCountsJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteSnapshotProvenanceAsync(
        SqliteConnection connection,
        PreparerDatabase.PublicationReconciliationSnapshotReservation
            reservation,
        IReadOnlyDictionary<string, long> tableCounts,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO authoring_snapshot_provenance(
                SnapshotId, ProcessorKind, RunId, AuthoringEpoch, Sequence,
                SchemaVersion, ItemCount, ReceiptCount, TableCountsJson,
                CreatedAt)
            VALUES(
                @snapshotId, @processorKind, @runId, @authoringEpoch,
                @sequence, @schemaVersion, @itemCount, @receiptCount,
                @tableCountsJson, @createdAt)
            """;
        command.Parameters.AddWithValue(
            "@snapshotId",
            reservation.SnapshotId);
        command.Parameters.AddWithValue(
            "@processorKind",
            reservation.ProcessorKind);
        command.Parameters.AddWithValue("@runId", reservation.RunId);
        command.Parameters.AddWithValue(
            "@authoringEpoch",
            reservation.AuthoringEpoch);
        command.Parameters.AddWithValue("@sequence", reservation.Sequence);
        command.Parameters.AddWithValue(
            "@schemaVersion",
            reservation.SchemaVersion);
        command.Parameters.AddWithValue("@itemCount", reservation.ItemCount);
        command.Parameters.AddWithValue(
            "@receiptCount",
            reservation.ReceiptCount);
        command.Parameters.AddWithValue(
            "@tableCountsJson",
            JsonSerializer.Serialize(tableCounts));
        command.Parameters.AddWithValue(
            "@createdAt",
            reservation.CreatedAt.ToString(
                "O",
                CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                "Snapshot provenance was not written exactly once.");
        }
    }

    private static async Task CheckpointAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetInt32(0) != 0)
        {
            throw new InvalidOperationException(
                "Snapshot WAL checkpoint did not complete.");
        }
    }

    private static async Task VerifyTableCountsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, long> expected,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, long> actual =
            await ReadTableCountsAsync(connection, expected.Keys, ct);
        if (actual.Count != expected.Count ||
            expected.Any(pair =>
                !actual.TryGetValue(pair.Key, out long value) ||
                value != pair.Value))
        {
            throw new InvalidOperationException(
                "Snapshot table counts changed before hashing.");
        }
    }

    private static async Task VerifySnapshotProvenanceAsync(
        SqliteConnection connection,
        PreparerDatabase.PublicationReconciliationSnapshotReservation
            reservation,
        IReadOnlyDictionary<string, long> tableCounts,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SnapshotId, ProcessorKind, RunId, AuthoringEpoch, Sequence,
                   SchemaVersion, ItemCount, ReceiptCount, TableCountsJson,
                   CreatedAt
            FROM authoring_snapshot_provenance
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        bool matches = await reader.ReadAsync(ct) &&
            !Enumerable.Range(0, reader.FieldCount).Any(reader.IsDBNull) &&
            string.Equals(
                reader.GetString(0),
                reservation.SnapshotId,
                StringComparison.Ordinal) &&
            string.Equals(
                reader.GetString(1),
                reservation.ProcessorKind,
                StringComparison.Ordinal) &&
            string.Equals(
                reader.GetString(2),
                reservation.RunId,
                StringComparison.Ordinal) &&
            reader.GetInt64(3) == reservation.AuthoringEpoch &&
            reader.GetInt64(4) == reservation.Sequence &&
            reader.GetInt32(5) == reservation.SchemaVersion &&
            reader.GetInt32(6) == reservation.ItemCount &&
            reader.GetInt32(7) == reservation.ReceiptCount &&
            string.Equals(
                reader.GetString(8),
                JsonSerializer.Serialize(tableCounts),
                StringComparison.Ordinal) &&
            string.Equals(
                reader.GetString(9),
                reservation.CreatedAt.ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        if (!matches || await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                "Snapshot provenance does not match the reserved coordinates.");
        }
    }

    private static async Task<IReadOnlyDictionary<string, long>>
        ReadTableCountsAsync(
            SqliteConnection connection,
            IEnumerable<string> tables,
            CancellationToken ct)
    {
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach (string table in tables.Distinct(StringComparer.Ordinal))
        {
            await using SqliteCommand command = connection.CreateCommand();
            string quoted =
                $"\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            command.CommandText = $"SELECT COUNT(*) FROM {quoted}";
            counts[table] = Convert.ToInt64(
                await command.ExecuteScalarAsync(ct));
        }
        return counts;
    }

    private static void EnsureCandidateMatchesReservation(
        PreparedTicketPublicationCandidateSnapshot candidate,
        PreparerDatabase.PublicationReconciliationSnapshotReservation
            reservation)
    {
        if (!string.Equals(
                candidate.RunId,
                reservation.RunId,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.SnapshotId,
                reservation.SnapshotId,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.ProcessorKind,
                reservation.ProcessorKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFullPath(candidate.TemporaryPath),
                reservation.TemporaryPath,
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFullPath(candidate.FinalPath),
                reservation.FinalPath,
                StringComparison.Ordinal) ||
            candidate.SchemaVersion != reservation.SchemaVersion ||
            candidate.Sequence != reservation.Sequence ||
            candidate.AuthoringEpoch != reservation.AuthoringEpoch ||
            candidate.ItemCount != reservation.ItemCount ||
            candidate.ReceiptCount != reservation.ReceiptCount ||
            candidate.CreatedAt != reservation.CreatedAt)
        {
            throw new InvalidOperationException(
                "The trusted reconciliation candidate does not match its reserved snapshot coordinates.");
        }
    }

    private static async Task ValidateCandidateSnapshotAsync(
        PreparedTicketPublicationCandidateSnapshot candidate,
        CancellationToken ct)
    {
        AuthoringReviewSnapshotRecord expected = new()
        {
            Id = candidate.SnapshotId,
            ProcessorKind = candidate.ProcessorKind,
            RunId = candidate.RunId,
            AuthoringEpoch = candidate.AuthoringEpoch,
            Sequence = candidate.Sequence,
            SchemaVersion = candidate.SchemaVersion,
            Status = AuthoringStatusValues.Snapshots.Creating,
            TempPath = candidate.TemporaryPath,
            Path = candidate.FinalPath,
            ChecksumSha256 = candidate.Sha256,
            SizeBytes = candidate.SizeBytes,
            ItemCount = candidate.ItemCount,
            ReceiptCount = candidate.ReceiptCount,
            TableCountsJson = JsonSerializer.Serialize(candidate.TableCounts),
            CreatedAt = candidate.CreatedAt,
        };
        SqliteReviewSnapshotValidationResult validation =
            await SqliteReviewSnapshotValidator.ValidateAsync(
                expected,
                candidate.TemporaryPath,
                ct: ct);
        if (!validation.IsValid ||
            validation.SizeBytes != candidate.SizeBytes ||
            !string.Equals(
                validation.ChecksumSha256,
                candidate.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reconciliation candidate validation failed: {validation.Error ?? "digest or size mismatch"}");
        }
    }

    private static void DeleteCandidateArtifacts(string path)
    {
        foreach (string candidate in
                 new[] { path, path + "-journal", path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }
}
