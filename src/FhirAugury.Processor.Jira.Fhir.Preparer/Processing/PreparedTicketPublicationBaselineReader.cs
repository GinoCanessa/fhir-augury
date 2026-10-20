using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

/// <summary>
/// Source-run snapshot acquisition and transactional selection helpers for
/// publication-enrichment. Production admission continues to select the
/// legacy recipe until the enrichment execution/recovery stage is installed.
/// </summary>
public sealed class PreparedTicketPublicationBaselineReader(
    AuthoringRunStore store,
    IOptions<PreparerServiceOptions> optionsAccessor,
    ILogger<PreparedTicketPublicationBaselineReader>? logger = null)
{
    private readonly string _snapshotDirectory =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(optionsAccessor.Value.SnapshotDirectory));
    private readonly ILogger<PreparedTicketPublicationBaselineReader> _logger =
        logger ?? NullLogger<PreparedTicketPublicationBaselineReader>.Instance;

    public async Task<PreparedTicketPublicationBaseline> ReadAsync(
        string sourceRunId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        AuthoringRunRecord sourceRun = await store.GetRunAsync(sourceRunId, ct)
            ?? throw new KeyNotFoundException($"Source authoring run '{sourceRunId}' was not found.");
        if (sourceRun.ProcessorKind != PreparedTicketSnapshotSchemaV3.ProcessorKind ||
            sourceRun.Status != AuthoringStatusValues.Runs.Completed ||
            sourceRun.DatabaseOnly || string.IsNullOrWhiteSpace(sourceRun.SnapshotId))
        {
            throw Refuse("The source run is not a completed Preparer snapshot-producing run.");
        }
        AuthoringReviewSnapshotRecord record = await store.GetReadySnapshotRecordAsync(sourceRunId, ct)
            ?? throw Refuse("The source run has no selected ready snapshot record.");
        if (record.Id != sourceRun.SnapshotId || record.RunId != sourceRun.Id ||
            record.ProcessorKind != sourceRun.ProcessorKind ||
            record.AuthoringEpoch != sourceRun.AuthoringEpoch)
        {
            throw Refuse("The selected snapshot does not match its source run identity.");
        }
        if (!PreparedTicketSnapshotSchemaResolver.TryResolve(record.SchemaVersion, out AuthoringSnapshotSchemaCatalog? catalog) ||
            catalog is null)
        {
            throw Refuse("The selected snapshot schema version is not supported.");
        }
        string sourcePath = Path.GetFullPath(record.Path);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(sourcePath), _snapshotDirectory, pathComparison) ||
            !string.Equals(Path.GetExtension(sourcePath), ".db", StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse("The selected snapshot is outside the configured snapshot location.");
        }
        if (File.Exists(sourcePath) &&
            (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refuse("The selected snapshot is a filesystem link rather than an immutable snapshot file.");
        }

        SqliteReviewSnapshotValidationResult validation =
            await SqliteReviewSnapshotValidator.ValidateAsync(record, requireReady: true, ct: ct);
        if (validation.Error is string error)
        {
            throw Refuse(error);
        }
        IReadOnlyDictionary<string, long> counts = SqliteReviewSnapshotValidator.ReadTableCounts(record.TableCountsJson);
        if (!counts.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(catalog.CountedTables))
        {
            throw Refuse("Snapshot table-count coordinates do not cover the supported catalog.");
        }

        DirectoryInfo privateDirectory = Directory.CreateTempSubdirectory("fhir-augury-publication-baseline-");
        string privatePath = Path.Combine(privateDirectory.FullName, "baseline.db");
        try
        {
            await using (FileStream source = new(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true))
            await using (FileStream destination = new(
                privatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await source.CopyToAsync(destination, ct);
            }
            File.SetAttributes(privatePath, FileAttributes.ReadOnly);
            SqliteReviewSnapshotValidationResult copied =
                await SqliteReviewSnapshotValidator.ValidateAsync(record, privatePath, requireReady: true, ct: ct);
            if (copied.Error is string copyError)
            {
                throw Refuse(copyError);
            }

            await using SqliteConnection connection =
                await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(privatePath, ct);
            await ValidateExportCoordinatesAsync(connection, sourceRun, record, ct);
            PreparedTicketPublicationProtectedInventory inventory =
                await PreparedTicketPublicationProtectionReader.ReadSnapshotAsync(connection, record.SchemaVersion, ct);
            AuthoringReviewSnapshotRecord? stillSelected = await store.GetReadySnapshotRecordAsync(sourceRunId, ct);
            if (stillSelected is null || stillSelected.Id != record.Id ||
                stillSelected.ProcessorKind != record.ProcessorKind ||
                stillSelected.AuthoringEpoch != record.AuthoringEpoch ||
                stillSelected.Sequence != record.Sequence ||
                stillSelected.SchemaVersion != record.SchemaVersion ||
                stillSelected.ChecksumSha256 != record.ChecksumSha256 ||
                stillSelected.SizeBytes != record.SizeBytes ||
                stillSelected.ItemCount != record.ItemCount ||
                stillSelected.ReceiptCount != record.ReceiptCount ||
                stillSelected.TableCountsJson != record.TableCountsJson ||
                stillSelected.Path != record.Path)
            {
                throw Refuse("The source snapshot selection changed during baseline acquisition.");
            }
            return new PreparedTicketPublicationBaseline(
                new PreparedTicketPublicationEnrichmentSource(
                    sourceRunId, record.Id,
                    copied.ChecksumSha256 ?? throw Refuse("The verified copy has no digest."),
                    record.AuthoringEpoch, record.Sequence, record.SchemaVersion,
                    copied.SizeBytes, inventory.Corpus.Count),
                inventory);
        }
        finally
        {
            if (File.Exists(privatePath))
            {
                File.SetAttributes(privatePath, FileAttributes.Normal);
            }
            privateDirectory.Delete(recursive: true);
        }
    }

    public async Task<AuthoringMaintenanceRunSelection> CreateSelectionAsync(
        SqliteConnection connection,
        PreparedTicketPublicationBaseline baseline,
        CancellationToken ct = default)
    {
        PreparedTicketPublicationProtectedInventory current =
            await PreparedTicketPublicationProtectionReader.ReadCurrentAsync(connection, ct);
        PreparedTicketPublicationEnrichmentInput input =
            PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, current);
        AuthoringRunCorpusComparison comparison = new(
            input.Source.SnapshotId, input.Source.ExportedTicketCount,
            input.Corpus.Count, input.AdditionalTicketKeys.Count);
        comparison.Validate();
        if (input.AdditionalTicketKeys.Count != 0)
        {
            _logger.LogInformation(
                "Publication enrichment from snapshot {SourceSnapshotId} retains {SourceTicketCount} original tickets and includes {AdditionalTicketCount} additional current tickets: {AdditionalTicketKeys}",
                input.Source.SnapshotId, input.Source.ExportedTicketCount,
                input.AdditionalTicketKeys.Count, string.Join(", ", input.AdditionalTicketKeys));
        }
        AuthoringMaintenanceRunRequest request = new(
            AuthoringMaintenanceRunRequest.CurrentVersion,
            PreparedTicketPublicationEnrichmentContract.RecipeName,
            PreparedTicketPublicationEnrichmentContract.CurrentVersion,
            comparison,
            PreparedTicketPublicationEnrichmentContract.SerializeInputJson(input));
        return new AuthoringMaintenanceRunSelection(
            input.Corpus.Select(item => new AuthoringMaintenanceRunItem(
                item.TicketKey, item.ItemKind, item.ExpectedSourceRevision, item.ReceiptId)).ToArray(),
            request.Serialize());
    }

    private static async Task ValidateExportCoordinatesAsync(
        SqliteConnection connection,
        AuthoringRunRecord sourceRun,
        AuthoringReviewSnapshotRecord snapshot,
        CancellationToken ct)
    {
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT ProcessorKind, AuthoringEpoch, DatabaseOnly, TotalItems FROM authoring_runs WHERE Id = @runId";
            command.Parameters.AddWithValue("@runId", sourceRun.Id);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) ||
                reader.GetString(0) != sourceRun.ProcessorKind ||
                reader.GetInt64(1) != sourceRun.AuthoringEpoch ||
                reader.GetInt64(2) != 0 || reader.GetInt32(3) != sourceRun.TotalItems ||
                await reader.ReadAsync(ct))
            {
                throw Refuse("The exported source-run identity is missing, ambiguous, or mismatched.");
            }
        }
        await using SqliteCommand counts = connection.CreateCommand();
        counts.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM authoring_run_items
                 WHERE RunId = @runId AND Status IN ('complete', 'superseded')),
                (SELECT COUNT(*) FROM authoring_result_receipts)
            """;
        counts.Parameters.AddWithValue("@runId", sourceRun.Id);
        await using SqliteDataReader countReader = await counts.ExecuteReaderAsync(ct);
        if (!await countReader.ReadAsync(ct) ||
            countReader.GetInt32(0) != snapshot.ItemCount ||
            countReader.GetInt32(1) != snapshot.ReceiptCount)
        {
            throw Refuse("The exported item or receipt count does not match snapshot provenance.");
        }
    }

    private static PreparedTicketPublicationProtectionException Refuse(string detail)
        => new("invalid-source-snapshot", detail);
}
