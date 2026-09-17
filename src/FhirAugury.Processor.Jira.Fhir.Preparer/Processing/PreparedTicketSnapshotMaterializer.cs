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
        string temporaryPath = Path.Combine(
            directory,
            $"{run.ProcessorKind}-{run.Id}.reconciliation.tmp");
        DeleteCandidateArtifacts(temporaryPath);
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
            PreparedTicketSnapshotSanitizer sanitizer = new(
                run.Id,
                snapshotSchema,
                comparison.SourceRunId);
            await sanitizer.SanitizeAsync(destination, ct);
            await VerifyIntegrityAsync(destination, ct);
        }

        IReadOnlyDictionary<string, long> counts =
            await ReadTableCountsAsync(
                temporaryPath,
                snapshotSchema.CountedTables,
                ct);
        string sha256 = await SqliteReviewSnapshotWriter
            .ComputeSha256Async(temporaryPath, ct);
        PreparedTicketPublicationCandidateSnapshot candidate = new(
            run.Id,
            temporaryPath,
            snapshotSchema.Version,
            sha256,
            new FileInfo(temporaryPath).Length,
            counts,
            delta.OverlayCorpusFingerprint,
            groupingFingerprint,
            impactFingerprint,
            DateTimeOffset.UtcNow);
        await database.SavePublicationReconciliationSnapshotDescriptorAsync(
            new(
                run.Id,
                JsonSerializer.Serialize(candidate),
                candidate.Sha256,
                candidate.CapturedAt),
            ct);
        await database.SavePublicationReconciliationProofAsync(
            run.Id,
            proof,
            ct);
        return candidate;
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

    private static async Task<IReadOnlyDictionary<string, long>>
        ReadTableCountsAsync(
            string path,
            IEnumerable<string> tables,
            CancellationToken ct)
    {
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(ct);
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
