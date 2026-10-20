using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Tests.Authoring;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Database;

public class JiraProcessingSourceTicketStoreTests
{
    [Fact]
    public void SourceRevision_UsesTimestampOrCanonicalFieldHash()
    {
        JiraProcessingSourceTicketRecord timestamped = new()
        {
            Id = "source-1",
            Key = "FHIR-123",
            Title = "Title",
            Project = "FHIR",
            Status = "Triaged",
            WorkGroup = "FHIR-I",
            Type = "Change Request",
            Specification = "FHIR",
            SourceTicketShape = "fhir",
            LastSyncedAt = DateTimeOffset.UtcNow,
            LastUpdated = new DateTimeOffset(
                2026,
                9,
                14,
                12,
                34,
                56,
                TimeSpan.FromHours(-5)),
        };

        Assert.Equal(
            "2026-09-14T12:34:56.0000000-05:00",
            JiraSourceRevision.Compute(timestamped));
        Assert.Equal(
            JiraSourceRevision.Compute(timestamped),
            JiraProcessingSourceTicketStore.GetSourceRevision(timestamped));

        timestamped.LastUpdated = null;
        Assert.Equal(
            "5a78e63020f4973af8a2441e608d011161e7de642d353ba2e4078046dea575e0",
            JiraSourceRevision.Compute(timestamped));
        Assert.Equal(
            JiraSourceRevision.Compute(timestamped),
            JiraSourceRevision.Compute(
                updatedAt: null,
                timestamped.Key,
                timestamped.Title,
                timestamped.Status,
                timestamped.WorkGroup,
                timestamped.Type,
                timestamped.Specification));
    }

    [Theory]
    [InlineData(
        "2025-07-17T16:12:12-05:00",
        "2025-07-17T21:12:12+00:00",
        true)]
    [InlineData(
        "2025-07-17T16:12:12.1234567-05:00",
        "2025-07-17T21:12:12.1234567Z",
        true)]
    [InlineData(
        "2025-07-17T16:12:12-05:00",
        "2025-07-17T21:12:13+00:00",
        false)]
    [InlineData(
        "5a78e63020f4973af8a2441e608d011161e7de642d353ba2e4078046dea575e0",
        "5a78e63020f4973af8a2441e608d011161e7de642d353ba2e4078046dea575e0",
        true)]
    [InlineData(
        "5a78e63020f4973af8a2441e608d011161e7de642d353ba2e4078046dea575e0",
        "6b89f74131f5a84bf9b3552f719e122272f8f753e464cb3f518156817b686f1a",
        false)]
    [InlineData("malformed-revision", "malformed-revision", true)]
    [InlineData("malformed-revision", "MALFORMED-REVISION", false)]
    [InlineData(
        "07/17/2025 16:12:12 -05:00",
        "07/17/2025 21:12:12 +00:00",
        false)]
    public void SourceRevision_EquivalenceComparesOnlyIsoTimestampsByInstant(
        string expected,
        string observed,
        bool equivalent)
    {
        Assert.Equal(
            equivalent,
            JiraSourceRevision.AreEquivalent(expected, observed));
    }

    [Fact]
    public async Task Upsert_InsertsNewSourceTicket()
    {
        JiraProcessingSourceTicketStore store = CreateStore();

        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);

        Assert.Equal("FHIR-1", record.Key);
        Assert.Null(record.ProcessingStatus);
        Assert.NotNull(await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_UpdatesExistingTicketAndPreservesCompletedStatusUnlessResetRequested()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        await store.MarkCompleteAsync(record, DateTimeOffset.UtcNow, CancellationToken.None);

        JiraProcessingSourceTicketRecord updated = await store.UpsertAsync(CreateTicket("FHIR-1", title: "Updated"), "fhir", false, CancellationToken.None);

        Assert.Equal("Updated", updated.Title);
        Assert.Equal(ProcessingStatusValues.Complete, updated.ProcessingStatus);
    }

    [Fact]
    public async Task Upsert_OverwritesAndClearsProjectSpecificProvenance()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        DateTimeOffset fhirRefresh =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        SourceReadProvenance provenance = new()
        {
            Source = "jira",
            ContentRevision = 42,
            IsStable = true,
            ProjectLastSuccessfulRefreshAt =
                new Dictionary<string, DateTimeOffset?>
                {
                    ["PSS"] = fhirRefresh.AddHours(1),
                    ["FHIR"] = fhirRefresh,
                },
        };

        JiraProcessingSourceTicketRecord inserted = await store.UpsertAsync(
            CreateTicket("FHIR-1"),
            "fhir",
            false,
            provenance,
            CancellationToken.None);

        Assert.Equal(
            fhirRefresh,
            inserted.SourceProjectLastSuccessfulRefreshAt);
        Assert.Equal(42, inserted.SourceContentRevision);

        JiraProcessingSourceTicketRecord cleared = await store.UpsertAsync(
            CreateTicket("FHIR-1", title: "Updated"),
            "fhir",
            false,
            provenance: null,
            CancellationToken.None);

        Assert.Null(cleared.SourceProjectLastSuccessfulRefreshAt);
        Assert.Null(cleared.SourceContentRevision);
        JiraProcessingSourceTicketRecord reloaded =
            (await store.GetByKeyAsync(
                "FHIR-1",
                "fhir",
                CancellationToken.None))!;
        Assert.Null(reloaded.SourceProjectLastSuccessfulRefreshAt);
        Assert.Null(reloaded.SourceContentRevision);
    }

    [Fact]
    public async Task Upsert_DoesNotApplyAnotherProjectsWatermark()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        DateTimeOffset pssRefresh =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        SourceReadProvenance provenance = new()
        {
            Source = "jira",
            ContentRevision = 43,
            IsStable = true,
            ProjectLastSuccessfulRefreshAt =
                new Dictionary<string, DateTimeOffset?>
                {
                    ["PSS"] = pssRefresh,
                },
        };

        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(
            CreateTicket("FHIR-1"),
            "fhir",
            false,
            provenance,
            CancellationToken.None);

        Assert.Null(record.SourceProjectLastSuccessfulRefreshAt);
        Assert.Equal(43, record.SourceContentRevision);
    }

    [Fact]
    public async Task ResetForReprocessing_ClearsTimingStatusAndErrorColumns()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        await store.MarkErrorAsync(record, "failed", 42, DateTimeOffset.UtcNow, CancellationToken.None);

        JiraProcessingSourceTicketRecord? reset = await store.ResetForReprocessingAsync("FHIR-1", "fhir", CancellationToken.None);

        Assert.NotNull(reset);
        Assert.Null(reset.ProcessingStatus);
        Assert.Null(reset.ErrorMessage);
        Assert.Null(reset.AgentExitCode);
        Assert.Null(reset.StartedProcessingAt);
    }

    [Fact]
    public async Task TryClaimNext_ClaimsOnlyPendingRowsPassingFilters()
    {
        ResolvedJiraProcessingFilters filters = new() { TicketStatuses = ["Triaged"], SourceTicketShape = "fhir" };
        JiraProcessingSourceTicketStore store = CreateStore(filters);
        await store.UpsertAsync(CreateTicket("FHIR-1", status: "Triaged"), "fhir", false, CancellationToken.None);
        await store.UpsertAsync(CreateTicket("FHIR-2", status: "Submitted"), "fhir", false, CancellationToken.None);

        IReadOnlyList<JiraProcessingSourceTicketRecord> pending = await store.GetPendingAsync(10, CancellationToken.None);
        bool claimed = await store.ClaimItemAsync(pending[0], DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(claimed);
        Assert.Single(pending);
        Assert.Equal("FHIR-1", pending[0].Key);
    }

    [Fact]
    public async Task TryClaimNext_IsAtomicAcrossConcurrentCallers()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);

        Task<bool>[] claims = Enumerable.Range(0, 8)
            .Select(_ => store.ClaimItemAsync(record, DateTimeOffset.UtcNow, CancellationToken.None))
            .ToArray();
        bool[] results = await Task.WhenAll(claims);

        Assert.Equal(1, results.Count(static claimed => claimed));
    }

    [Fact]
    public async Task MarkComplete_SetsCompletedAtAndCompleteStatus()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        DateTimeOffset completedAt = DateTimeOffset.UtcNow;

        await store.MarkCompleteAsync(record, completedAt, CancellationToken.None);

        Assert.Equal(ProcessingStatusValues.Complete, record.ProcessingStatus);
        Assert.Equal(completedAt, record.CompletedProcessingAt);
    }

    [Fact]
    public async Task MarkError_SetsErrorStatusExitCodeAndMessage()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);

        await store.MarkErrorAsync(record, "boom", 7, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(ProcessingStatusValues.Error, record.ProcessingStatus);
        Assert.Equal("boom", record.ErrorMessage);
        Assert.Equal(7, record.AgentExitCode);
    }

    [Fact]
    public async Task MarkComplete_StampsCompletionIdAndIsIdempotent()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        DateTimeOffset firstCompletedAt = DateTimeOffset.UtcNow;
        DateTimeOffset secondCompletedAt = firstCompletedAt.AddSeconds(1);

        await store.MarkCompleteAsync(record, firstCompletedAt, CancellationToken.None);
        string firstCompletionId = record.CompletionId!;
        Assert.False(string.IsNullOrWhiteSpace(firstCompletionId));

        await store.MarkCompleteAsync(record, secondCompletedAt, CancellationToken.None);
        Assert.Equal(firstCompletionId, record.CompletionId);

        JiraProcessingSourceTicketRecord? reloaded = await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(firstCompletionId, reloaded.CompletionId);
    }

    [Fact]
    public async Task MarkError_ClearsCompletionId()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        await store.MarkCompleteAsync(record, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(record.CompletionId));

        await store.MarkErrorAsync(record, "boom", 7, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(record.CompletionId);
        JiraProcessingSourceTicketRecord? reloaded = await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded.CompletionId);
    }

    [Fact]
    public async Task ResetForReprocessing_ClearsCompletionId()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        await store.MarkCompleteAsync(record, DateTimeOffset.UtcNow, CancellationToken.None);

        JiraProcessingSourceTicketRecord? reset = await store.ResetForReprocessingAsync("FHIR-1", "fhir", CancellationToken.None);

        Assert.NotNull(reset);
        Assert.Null(reset.CompletionId);
    }

    [Fact]
    public async Task MarkStale_ClearsCompletionIdAndAllowsClaim()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        await store.MarkCompleteAsync(record, completedAt, CancellationToken.None);
        DateTimeOffset markedAt = completedAt.AddMinutes(5);

        await store.MarkStaleAsync(record, markedAt, CancellationToken.None);

        Assert.Equal(ProcessingStatusValues.Stale, record.ProcessingStatus);
        Assert.Null(record.CompletionId);
        Assert.Equal(completedAt, record.CompletedProcessingAt);

        JiraProcessingSourceTicketRecord? reloaded = await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(ProcessingStatusValues.Stale, reloaded.ProcessingStatus);
        Assert.Null(reloaded.CompletionId);
    }

    [Fact]
    public async Task GetPending_IncludesStaleRows()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord recordA = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        JiraProcessingSourceTicketRecord recordB = await store.UpsertAsync(CreateTicket("FHIR-2"), "fhir", false, CancellationToken.None);
        await store.MarkCompleteAsync(recordA, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.MarkStaleAsync(recordA, DateTimeOffset.UtcNow, CancellationToken.None);

        IReadOnlyList<JiraProcessingSourceTicketRecord> pending = await store.GetPendingAsync(10, CancellationToken.None);

        Assert.Contains(pending, p => p.Key == "FHIR-1");
        Assert.Contains(pending, p => p.Key == "FHIR-2");
        _ = recordB;
    }

    [Fact]
    public async Task ClaimItem_ClaimsStaleRow()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        await store.MarkCompleteAsync(record, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.MarkStaleAsync(record, DateTimeOffset.UtcNow, CancellationToken.None);

        bool claimed = await store.ClaimItemAsync(record, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(claimed);
        Assert.Equal(ProcessingStatusValues.InProgress, record.ProcessingStatus);
    }

    [Fact]
    public async Task GetQueueStats_CountsStaleAsPending()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord recordA = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        await store.UpsertAsync(CreateTicket("FHIR-2"), "fhir", false, CancellationToken.None);
        await store.MarkCompleteAsync(recordA, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.MarkStaleAsync(recordA, DateTimeOffset.UtcNow, CancellationToken.None);

        ProcessingQueueStats stats = await store.GetQueueStatsAsync(CancellationToken.None);

        Assert.Equal(0, stats.ProcessedCount);
        Assert.Equal(2, stats.RemainingCount);
    }

    [Fact]
    public void Schema_HasRowIdPrimaryKeyAndIdUnique()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"jira-processing-{Guid.NewGuid():N}.db");
        _ = new JiraProcessingSourceTicketStore(path);

        using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        connection.Open();

        Dictionary<string, (int Pk, string Type)> columns = ReadTableInfo(connection, "jira_processing_source_tickets");

        Assert.True(columns.ContainsKey("RowId"), "RowId column missing");
        Assert.True(columns.ContainsKey("Id"), "Id column missing");
        Assert.Equal(1, columns["RowId"].Pk);
        Assert.Equal(0, columns["Id"].Pk);
        Assert.Contains("INT", columns["RowId"].Type, StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<(string Name, bool Unique)> indexes = ReadIndexes(connection, "jira_processing_source_tickets");
        Assert.Contains(indexes, i => i.Unique && IndexCovers(connection, i.Name, ["Id"]));
        Assert.Contains(indexes, i => i.Unique && IndexCovers(connection, i.Name, ["Key", "SourceTicketShape"]));
        Assert.True(columns.ContainsKey("CompletionId"), "CompletionId column missing");
        Assert.Contains(indexes, i => IndexCovers(connection, i.Name, ["CompletionId"]));
    }

    [Fact]
    public async Task Insert_AutoincrementsRowIdAndPreservesId()
    {
        JiraProcessingSourceTicketStore store = CreateStore();

        JiraProcessingSourceTicketRecord first = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        JiraProcessingSourceTicketRecord second = await store.UpsertAsync(CreateTicket("FHIR-2"), "fhir", false, CancellationToken.None);

        JiraProcessingSourceTicketRecord? firstReloaded = await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None);
        JiraProcessingSourceTicketRecord? secondReloaded = await store.GetByKeyAsync("FHIR-2", "fhir", CancellationToken.None);

        Assert.NotNull(firstReloaded);
        Assert.NotNull(secondReloaded);
        Assert.NotEqual(0, firstReloaded.RowId);
        Assert.NotEqual(0, secondReloaded.RowId);
        Assert.NotEqual(firstReloaded.RowId, secondReloaded.RowId);
        Assert.Equal(first.Id, firstReloaded.Id);
        Assert.Equal(second.Id, secondReloaded.Id);
    }

    [Fact]
    public async Task ReadRecord_RoundTripsRowId()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);

        IReadOnlyList<JiraProcessingSourceTicketRecord> pending = await store.GetPendingAsync(10, CancellationToken.None);

        JiraProcessingSourceTicketRecord row = Assert.Single(pending);
        Assert.NotEqual(0, row.RowId);
    }

    [Fact]
    public async Task Upsert_PersistsSpecification_OnInsert()
    {
        JiraProcessingSourceTicketStore store = CreateStore();

        await store.UpsertAsync(CreateTicket("FHIR-1", specification: "fhir-extensions"), "fhir", false, CancellationToken.None);

        JiraProcessingSourceTicketRecord? reloaded = await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal("fhir-extensions", reloaded.Specification);
    }

    [Fact]
    public async Task Upsert_UpdatesSpecification_OnExistingTicket()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        await store.UpsertAsync(CreateTicket("FHIR-1", specification: "fhir-core"), "fhir", false, CancellationToken.None);

        await store.UpsertAsync(CreateTicket("FHIR-1", specification: "fhir-extensions"), "fhir", false, CancellationToken.None);

        JiraProcessingSourceTicketRecord? reloaded = await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal("fhir-extensions", reloaded.Specification);
    }

    [Fact]
    public async Task Upsert_DefaultsSpecification_ToEmptyString()
    {
        JiraProcessingSourceTicketStore store = CreateStore();

        await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);

        JiraProcessingSourceTicketRecord? reloaded = await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(string.Empty, reloaded.Specification);
    }

    [Fact]
    public async Task EnsureSchema_AddsSpecification_ToLegacyDb()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"jira-processing-legacy-{Guid.NewGuid():N}.db");
        // Hand-write a legacy (pre-Specification) schema, mirroring the CsLightDbGen
        // CREATE TABLE shape but with the Specification column omitted.
        await using (SqliteConnection seed = new($"Data Source={path};Pooling=False"))
        {
            await seed.OpenAsync();
            await using SqliteCommand cmd = seed.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE jira_processing_source_tickets (
                    RowId INTEGER UNIQUE PRIMARY KEY NOT NULL,
                    Id TEXT UNIQUE NOT NULL,
                    Key TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    Description TEXT,
                    Project TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    WorkGroup TEXT NOT NULL,
                    Type TEXT NOT NULL,
                    SourceTicketShape TEXT NOT NULL,
                    LastSyncedAt TEXT NOT NULL,
                    LastUpdated TEXT,
                    StartedProcessingAt TEXT,
                    CompletedProcessingAt TEXT,
                    LastProcessingAttemptAt TEXT,
                    ProcessingStatus TEXT,
                    ProcessingError TEXT,
                    ProcessingAttemptCount INTEGER NOT NULL,
                    CompletionId TEXT,
                    ErrorMessage TEXT,
                    AgentExitCode INTEGER,
                    ErrorOccurredAt TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Constructing the store triggers EnsureSchema, which must add Specification.
        JiraProcessingSourceTicketStore store = new(path);

        await using SqliteConnection verify = new($"Data Source={path};Pooling=False");
        await verify.OpenAsync();
        Dictionary<string, (int Pk, string Type)> columns = ReadTableInfo(verify, "jira_processing_source_tickets");
        Assert.True(columns.ContainsKey("Specification"), "Specification column should exist after EnsureSchema");
        Assert.True(
            columns.ContainsKey("SourceProjectLastSuccessfulRefreshAt"),
            "SourceProjectLastSuccessfulRefreshAt column should exist after EnsureSchema");
        Assert.True(
            columns.ContainsKey("SourceContentRevision"),
            "SourceContentRevision column should exist after EnsureSchema");

        // Upsert + readback should succeed against the migrated DB.
        await store.UpsertAsync(CreateTicket("FHIR-9", specification: "fhir-core"), "fhir", false, CancellationToken.None);
        JiraProcessingSourceTicketRecord? row = await store.GetByKeyAsync("FHIR-9", "fhir", CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("fhir-core", row.Specification);
        Assert.Null(row.SourceProjectLastSuccessfulRefreshAt);
        Assert.Null(row.SourceContentRevision);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task EnsureSchema_UpgradesSupportedLegacyShapesWithoutChangingRetainedValues(
        bool compatibilityColumns,
        bool completionColumn,
        bool populated)
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        fixture.CreateLegacy(connection, compatibilityColumns, completionColumn);
        if (populated)
        {
            fixture.SeedRows(connection);
        }

        Dictionary<string, (int Pk, string Type)> columns =
            ReadTableInfo(connection, SourceSchemaFixture.Table);
        Assert.Equal(completionColumn, columns.ContainsKey("CompletionId"));
        if (compatibilityColumns && !completionColumn)
        {
            Assert.Equal(24, columns.Count);
        }
        SqliteRows before = ReadSourceRows(connection);
        SqliteRows columnDefinitions = ReadTypedRows(
            connection, $"PRAGMA table_info({SourceSchemaFixture.Table})");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            JiraProcessingSourceTicketStore.EnsureSchema(connection);
            _ = new JiraProcessingSourceTicketStore(fixture.DatabasePath);

            AssertRowsEqual(before, ReadSourceRows(connection, before.Columns));
            SqliteRows afterDefinitions = ReadTypedRows(
                connection, $"PRAGMA table_info({SourceSchemaFixture.Table})");
            AssertRowsEqual(
                columnDefinitions,
                afterDefinitions with
                {
                    Values = afterDefinitions.Values.Take(columnDefinitions.Values.Length).ToArray(),
                });
            foreach (string column in new[]
                     {
                         "Specification", "SourceProjectLastSuccessfulRefreshAt",
                         "SourceContentRevision", "CompletionId",
                     }.Except(before.Columns))
            {
                SqliteRows added = ReadSourceRows(connection, [column]);
                Assert.All(added.Values, row =>
                    Assert.Equal(column == "Specification" ? string.Empty : null, row[0]));
                object?[] definition = Assert.Single(afterDefinitions.Values, row => Equals(row[1], column));
                Assert.Equal(column == "SourceContentRevision" ? "INTEGER" : "TEXT", definition[2]);
                Assert.Equal(column == "Specification" ? 1L : 0L, definition[3]);
                Assert.Equal(column == "Specification" ? "''" : null, definition[4]);
            }
        }

        AssertCompletionIndex(connection);
        await AssertStoreIsUsableAsync(fixture, populated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureSchema_FreshAndCurrentSchemasRemainUsableOnReopen(bool current)
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        if (current)
        {
            JiraProcessingSourceTicketRecord.CreateTable(connection);
            fixture.SeedRows(connection);
        }
        else
        {
            Assert.Empty(ReadTableInfo(connection, SourceSchemaFixture.Table));
        }

        JiraProcessingSourceTicketStore.EnsureSchema(connection);
        SqliteRows before = ReadSourceRows(connection);
        DatabaseSnapshot currentSchema = ReadDatabaseSnapshot(connection);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            JiraProcessingSourceTicketStore.EnsureSchema(connection);
            _ = new JiraProcessingSourceTicketStore(fixture.DatabasePath);
            AssertRowsEqual(before, ReadSourceRows(connection));
            AssertDatabaseEqual(currentSchema, ReadDatabaseSnapshot(connection));
            AssertCompletionIndex(connection);
        }

        await AssertStoreIsUsableAsync(fixture, current);
    }

    [Fact]
    public async Task EnsureSchema_RepairsMissingCompletionIndexWithoutRewritingValues()
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        JiraProcessingSourceTicketStore.EnsureSchema(connection);
        fixture.SeedRows(connection);
        string completionIndex = AssertCompletionIndex(connection);
        SourceSchemaFixture.Execute(connection, $"DROP INDEX {QuoteIdentifier(completionIndex)}");
        DatabaseSnapshot before = ReadDatabaseSnapshot(connection);

        JiraProcessingSourceTicketStore.EnsureSchema(connection);

        Assert.Equal(completionIndex, AssertCompletionIndex(connection));
        AssertRowsEqual(before.Tables[SourceSchemaFixture.Table], ReadSourceRows(connection));
        AssertSchemaExcept(before, ReadDatabaseSnapshot(connection), completionIndex);
        DatabaseSnapshot repaired = ReadDatabaseSnapshot(connection);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            _ = new JiraProcessingSourceTicketStore(fixture.DatabasePath);
            JiraProcessingSourceTicketStore.EnsureSchema(connection);
            AssertDatabaseEqual(repaired, ReadDatabaseSnapshot(connection));
        }
        await AssertStoreIsUsableAsync(fixture, populated: true);
    }

    [Theory]
    [InlineData("absent", false)]
    [InlineData("absent", true)]
    [InlineData("binary", false)]
    [InlineData("binary", true)]
    [InlineData("current", false)]
    [InlineData("current", true)]
    [InlineData("current-descending", false)]
    [InlineData("current-descending", true)]
    public void EnsureCompositeUniqueIndex_PreservesOrUpgradesOnlyKnownDefinitions(
        string definition,
        bool fullSchema)
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        JiraProcessingSourceTicketRecord.CreateTable(connection);
        fixture.SeedRows(connection);
        if (definition != "absent")
        {
            string collation = definition == "binary" ? "BINARY" : "NOCASE";
            string direction = definition == "current-descending" ? " DESC" : string.Empty;
            SourceSchemaFixture.Execute(connection, $"""
                CREATE UNIQUE INDEX {SourceSchemaFixture.CompositeIndex}
                ON {SourceSchemaFixture.Table}(Key COLLATE {collation}{direction}, SourceTicketShape COLLATE {collation}{direction});
                """);
        }
        SourceSchemaFixture.Execute(connection, $"""
            CREATE INDEX "unrelated ""title"" index" ON {SourceSchemaFixture.Table}(Title DESC);
            """);
        DatabaseSnapshot before = ReadDatabaseSnapshot(connection);

        Initialize(connection, fullSchema);

        AssertCompositeIndex(connection);
        DatabaseSnapshot after = ReadDatabaseSnapshot(connection);
        if (definition.StartsWith("current", StringComparison.Ordinal))
        {
            AssertDatabaseEqual(before, after);
        }
        else
        {
            AssertSchemaExcept(before, after, SourceSchemaFixture.CompositeIndex);
        }
        Initialize(connection, fullSchema);
        AssertDatabaseEqual(after, ReadDatabaseSnapshot(connection));
        Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
    }

    [Fact]
    public void EnsureCompositeUniqueIndex_IsStandaloneAndDoesNotMigrateColumns()
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        fixture.CreateLegacy(connection, compatibilityColumns: false);
        fixture.SeedRows(connection);
        DatabaseSnapshot before = ReadDatabaseSnapshot(connection);

        JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex(connection);

        AssertCompositeIndex(connection);
        AssertSchemaExcept(before, ReadDatabaseSnapshot(connection), SourceSchemaFixture.CompositeIndex);
        Assert.DoesNotContain("CompletionId", ReadTableInfo(connection, SourceSchemaFixture.Table).Keys);
        Assert.DoesNotContain("Specification", ReadTableInfo(connection, SourceSchemaFixture.Table).Keys);
        DatabaseSnapshot after = ReadDatabaseSnapshot(connection);
        JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex(connection);
        AssertDatabaseEqual(after, ReadDatabaseSnapshot(connection));
    }

    [Fact]
    public void EnsureCompositeUniqueIndex_RefusesAnAbsentTableWithoutCreatingIt()
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        DatabaseSnapshot before = ReadDatabaseSnapshot(connection);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex(connection));
        Assert.Contains("source table is absent", failure.Message);
        AssertDatabaseEqual(before, ReadDatabaseSnapshot(connection));
        Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
    }

    public static IEnumerable<object[]> UnsupportedSourceSchemas()
    {
        string schema = SourceSchemaFixture.CurrentSchema;
        string[] required =
        [
            "RowId", "Id", "Key", "Title", "Description", "Project", "Status", "WorkGroup", "Type",
            "SourceTicketShape", "LastSyncedAt", "LastUpdated", "StartedProcessingAt",
            "CompletedProcessingAt", "LastProcessingAttemptAt", "ProcessingStatus", "ProcessingError",
            "ProcessingAttemptCount", "ErrorMessage", "AgentExitCode", "ErrorOccurredAt",
        ];
        foreach (string column in required)
        {
            yield return [$"missing {column}", RemoveColumn(schema, column), "", "missing required columns"];
        }
        string[] integers = ["RowId", "ProcessingAttemptCount", "AgentExitCode", "SourceContentRevision"];
        string[] notNull =
        [
            "Id", "Key", "Title", "Project", "Status", "WorkGroup", "Type",
            "SourceTicketShape", "LastSyncedAt", "ProcessingAttemptCount", "Specification",
        ];
        foreach (string column in required.Concat(
                     ["Specification", "SourceProjectLastSuccessfulRefreshAt", "SourceContentRevision", "CompletionId"]))
        {
            yield return
            [
                $"affinity {column}",
                ReplaceColumnText(schema, column, integers.Contains(column) ? "INTEGER" : "TEXT", "BLOB"),
                "",
                column == "RowId" ? "RowId contract" : "column contract",
            ];
            if (column != "RowId")
            {
                yield return
                [
                    $"nullability {column}",
                    notNull.Contains(column)
                        ? ReplaceColumnText(schema, column, " NOT NULL", "")
                        : ReplaceColumnText(schema, column, integers.Contains(column) ? "INTEGER" : "TEXT",
                            integers.Contains(column) ? "INTEGER NOT NULL" : "TEXT NOT NULL"),
                    "",
                    "column contract",
                ];
            }
        }

        yield return ["INT primary key", ReplaceColumnText(schema, "RowId", "INTEGER", "INT"), "", "RowId contract"];
        yield return ["descending primary key", ReplaceColumnText(schema, "RowId", "PRIMARY KEY", "PRIMARY KEY DESC"), "", "RowId contract"];
        yield return ["no primary key", ReplaceColumnText(schema, "RowId", " PRIMARY KEY", ""), "", "RowId contract"];
        yield return ["WITHOUT ROWID", schema.Replace(");", ") WITHOUT ROWID;"), "", "RowId contract"];
        yield return
        [
            "composite primary key",
            AddDefinition(ReplaceColumnText(schema, "RowId", " PRIMARY KEY", ""), "PRIMARY KEY (RowId, Id)"),
            "", "RowId contract",
        ];
        yield return ["Id not unique", ReplaceColumnText(schema, "Id", " UNIQUE", ""), "", "Id uniqueness"];
        foreach ((string name, string definition) in new[]
                 {
                     ("partial", "Id) WHERE Id <> ''"),
                     ("multiple columns", "Id, Key)"),
                     ("expression", "LOWER(Id))"),
                 })
        {
            yield return
            [
                $"Id {name}",
                ReplaceColumnText(schema, "Id", " UNIQUE", ""),
                $"CREATE UNIQUE INDEX id_contract ON {SourceSchemaFixture.Table}({definition};",
                "Id uniqueness",
            ];
        }
        foreach (string collation in new[] { "NOCASE", "RTRIM" })
        {
            yield return
            [
                $"Id {collation} with only BINARY uniqueness",
                ReplaceColumnText(schema, "Id", "TEXT UNIQUE", $"TEXT COLLATE {collation}"),
                $"""
                CREATE UNIQUE INDEX id_contract ON {SourceSchemaFixture.Table}(Id COLLATE BINARY);
                UPDATE {SourceSchemaFixture.Table} SET Id = '{(collation == "NOCASE" ? "RETAINED-101" : "retained-101 ")}' WHERE RowId = 202;
                """,
                "Id uniqueness",
            ];
        }
        foreach (string definition in new[]
                 {
                     "ExtraRequired TEXT NOT NULL",
                     "ExtraRequired TEXT NOT NULL DEFAULT NULL",
                     "ExtraRequired INTEGER NOT NULL DEFAULT (1 / 0)",
                 })
        {
            yield return [definition, AddDefinition(schema, definition), "", "extra column contract"];
        }
        yield return
        [
            "generated required column",
            ReplaceColumnText(schema, "Title", "TEXT NOT NULL", "TEXT GENERATED ALWAYS AS ('generated') VIRTUAL"),
            "", "column contract",
        ];
        yield return
        [
            "generated additive column",
            ReplaceColumnText(schema, "CompletionId", "TEXT", "TEXT GENERATED ALWAYS AS (Title) VIRTUAL"),
            "", "column contract",
        ];
        yield return
        [
            "generated required extra",
            AddDefinition(schema, "ExtraValue TEXT NOT NULL GENERATED ALWAYS AS (CASE WHEN Title LIKE 'Retained%' THEN Title ELSE NULL END) VIRTUAL"),
            "", "extra column contract",
        ];
        yield return
        [
            "restrictive extra unique index", schema,
            $"CREATE UNIQUE INDEX extra_unique ON {SourceSchemaFixture.Table}(Title)",
            "extra index contract",
        ];
        yield return
        [
            "restrictive extra partial index", schema,
            $"CREATE UNIQUE INDEX extra_unique ON {SourceSchemaFixture.Table}(Title) WHERE Title <> ''",
            "extra index contract",
        ];
        yield return
        [
            "restrictive processing-state uniqueness", schema,
            $"CREATE UNIQUE INDEX extra_unique ON {SourceSchemaFixture.Table}(ProcessingStatus)",
            "extra index contract",
        ];
        yield return
        [
            "restrictive extra default uniqueness",
            AddDefinition(schema, "ExtraValue TEXT NOT NULL DEFAULT 'constant'"),
            $"CREATE UNIQUE INDEX extra_unique ON {SourceSchemaFixture.Table}(ExtraValue)",
            "extra index contract",
        ];
    }

    [Theory]
    [MemberData(nameof(UnsupportedSourceSchemas))]
    public void EnsureSchema_RefusesUnsupportedColumnContractsWithoutMutation(
        string scenario,
        string schema,
        string additionalSql,
        string category)
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        SourceSchemaFixture.Execute(connection, schema);
        fixture.SeedRows(connection, satisfyNotNull: true);
        SourceSchemaFixture.Execute(connection, additionalSql);

        InvalidOperationException failure = AssertRefusedWithoutMutation(
            fixture, connection, category);

        if (scenario.StartsWith("missing ", StringComparison.Ordinal))
        {
            Assert.Contains(scenario["missing ".Length..], failure.Message);
        }
        connection.Close();
        AssertExclusiveAccess(fixture.DatabasePath);
    }

    [Theory]
    [InlineData("fhir-key-collision", "identity collision")]
    [InlineData("fhir-shape-collision", "identity collision")]
    [InlineData("other-shape-collision", "identity collision")]
    [InlineData("lowercase-key", "noncanonical FHIR identity")]
    [InlineData("mixed-case-shape", "noncanonical FHIR identity")]
    [InlineData("padded-shape", "noncanonical FHIR identity")]
    [InlineData("nocase-column-key", "noncanonical FHIR identity")]
    [InlineData("nocase-column-shape", "noncanonical FHIR identity")]
    [InlineData("rtrim-column-shape", "noncanonical FHIR identity")]
    [InlineData("wrong-index-order", "composite index definition")]
    [InlineData("wrong-index-column", "composite index definition")]
    [InlineData("one-index-column", "composite index definition")]
    [InlineData("extra-index-column", "composite index definition")]
    [InlineData("expression-index", "composite index definition")]
    [InlineData("constant-index", "composite index definition")]
    [InlineData("partial-index", "composite index definition")]
    [InlineData("nonunique-index", "composite index definition")]
    [InlineData("mixed-index-collations", "composite index definition")]
    [InlineData("unknown-index-collation", "composite index definition")]
    [InlineData("descending-binary-index", "composite index definition")]
    [InlineData("index-name-table", "composite index ownership")]
    [InlineData("index-name-view", "composite index ownership")]
    [InlineData("index-name-trigger", "composite index ownership")]
    [InlineData("index-other-table", "composite index ownership")]
    [InlineData("table-name-view", "unsupported schema")]
    [InlineData("table-name-index", "unsupported schema")]
    public void EnsureSchema_RefusesUnsupportedOrConflictingStateWithoutMutation(
        string scenario,
        string category)
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        if (scenario == "table-name-view")
        {
            SourceSchemaFixture.Execute(connection,
                $"CREATE VIEW {SourceSchemaFixture.Table} AS SELECT * FROM unrelated_state");
        }
        else if (scenario == "table-name-index")
        {
            SourceSchemaFixture.Execute(connection,
                $"CREATE INDEX {SourceSchemaFixture.Table} ON unrelated_state(TextValue)");
        }
        else
        {
            string schema = SourceSchemaFixture.ReportedSchema;
            if (scenario.StartsWith("nocase-column", StringComparison.Ordinal))
            {
                schema = ReplaceColumnText(
                    ReplaceColumnText(schema, "Key", "TEXT", "TEXT COLLATE NOCASE"),
                    "SourceTicketShape", "TEXT", "TEXT COLLATE NOCASE");
            }
            else if (scenario == "rtrim-column-shape")
            {
                schema = ReplaceColumnText(schema, "SourceTicketShape", "TEXT", "TEXT COLLATE RTRIM");
            }
            SourceSchemaFixture.Execute(connection, schema);
            fixture.SeedRows(connection);
            string? indexTerms = scenario switch
            {
                "wrong-index-order" => "SourceTicketShape COLLATE NOCASE, Key COLLATE NOCASE",
                "wrong-index-column" => "Key COLLATE NOCASE, Title COLLATE NOCASE",
                "one-index-column" => "Key COLLATE NOCASE",
                "extra-index-column" => "Key COLLATE NOCASE, SourceTicketShape COLLATE NOCASE, Title",
                "expression-index" => "UPPER(Key), SourceTicketShape COLLATE NOCASE",
                "constant-index" => "('Key' || ''), Title",
                "partial-index" or "nonunique-index" => "Key COLLATE NOCASE, SourceTicketShape COLLATE NOCASE",
                "mixed-index-collations" => "Key COLLATE NOCASE, SourceTicketShape COLLATE BINARY",
                "unknown-index-collation" => "Key COLLATE RTRIM, SourceTicketShape COLLATE RTRIM",
                "descending-binary-index" => "Key COLLATE BINARY DESC, SourceTicketShape COLLATE BINARY",
                _ => null,
            };
            if (indexTerms is not null)
            {
                string unique = scenario == "nonunique-index" ? "" : "UNIQUE ";
                string partial = scenario == "partial-index" ? " WHERE Key <> ''" : "";
                SourceSchemaFixture.Execute(connection,
                    $"CREATE {unique}INDEX {SourceSchemaFixture.CompositeIndex} ON {SourceSchemaFixture.Table}({indexTerms}){partial}");
            }
            else
            {
                string sql = scenario switch
                {
                    "fhir-key-collision" => "UPDATE jira_processing_source_tickets SET Key = 'fhir-101' WHERE RowId = 202",
                    "fhir-shape-collision" => "UPDATE jira_processing_source_tickets SET Key = 'FHIR-101', SourceTicketShape = 'FHIR' WHERE RowId = 202",
                    "other-shape-collision" => """
                        UPDATE jira_processing_source_tickets SET SourceTicketShape = 'pss' WHERE RowId = 101;
                        UPDATE jira_processing_source_tickets SET Key = 'fhir-101', SourceTicketShape = 'PSS' WHERE RowId = 202;
                        """,
                    "lowercase-key" or "nocase-column-key" =>
                        "UPDATE jira_processing_source_tickets SET Key = 'fhir-101' WHERE RowId = 101",
                    "mixed-case-shape" or "nocase-column-shape" =>
                        "UPDATE jira_processing_source_tickets SET SourceTicketShape = 'FhIr' WHERE RowId = 101",
                    "padded-shape" or "rtrim-column-shape" =>
                        "UPDATE jira_processing_source_tickets SET SourceTicketShape = 'fhir ' WHERE RowId = 101",
                    "index-name-table" => $"CREATE TABLE {SourceSchemaFixture.CompositeIndex}(Sentinel TEXT); INSERT INTO {SourceSchemaFixture.CompositeIndex} VALUES ('untouched')",
                    "index-name-view" => $"CREATE VIEW {SourceSchemaFixture.CompositeIndex} AS SELECT * FROM unrelated_state",
                    "index-name-trigger" => $"CREATE TRIGGER {SourceSchemaFixture.CompositeIndex} BEFORE DELETE ON unrelated_state BEGIN SELECT RAISE(ABORT, 'retain'); END",
                    "index-other-table" => $"CREATE INDEX {SourceSchemaFixture.CompositeIndex} ON unrelated_state(TextValue)",
                    _ => throw new InvalidOperationException($"Unknown test scenario: {scenario}"),
                };
                SourceSchemaFixture.Execute(connection, sql);
            }
        }

        AssertRefusedWithoutMutation(fixture, connection, category);
        connection.Close();
        AssertExclusiveAccess(fixture.DatabasePath);
    }

    [Theory]
    [InlineData("rowid-without-not-null")]
    [InlineData("table-primary-key")]
    [InlineData("id-nocase")]
    [InlineData("id-nocase-explicit")]
    [InlineData("id-rtrim")]
    [InlineData("id-binary-stronger-unique")]
    [InlineData("compatible-affinities")]
    [InlineData("extra-nullable")]
    [InlineData("extra-default")]
    [InlineData("extra-generated")]
    [InlineData("extra-nullable-unique")]
    [InlineData("redundant-unique-indexes")]
    [InlineData("unrelated-expression-index")]
    [InlineData("reordered-columns")]
    [InlineData("non-fhir-whitespace")]
    [InlineData("non-ascii-distinct")]
    public async Task EnsureSchema_AcceptsCompatibleContractsWithoutChangingRetainedValues(string scenario)
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        string schema = SourceSchemaFixture.ReportedSchema;
        string additionalSql = "";
        switch (scenario)
        {
            case "rowid-without-not-null":
                schema = ReplaceColumnText(schema, "RowId", " NOT NULL", "");
                break;
            case "table-primary-key":
                schema = AddDefinition(ReplaceColumnText(schema, "RowId", " PRIMARY KEY", ""), "PRIMARY KEY (RowId)");
                break;
            case "id-nocase":
            case "id-rtrim":
                schema = ReplaceColumnText(schema, "Id", "TEXT", $"TEXT COLLATE {(scenario == "id-nocase" ? "NOCASE" : "RTRIM")}");
                break;
            case "id-nocase-explicit":
                schema = ReplaceColumnText(schema, "Id", "TEXT UNIQUE", "TEXT COLLATE NOCASE");
                additionalSql = $"""
                    CREATE UNIQUE INDEX id_binary ON {SourceSchemaFixture.Table}(Id COLLATE BINARY);
                    CREATE UNIQUE INDEX id_nocase ON {SourceSchemaFixture.Table}(Id COLLATE NOCASE);
                    """;
                break;
            case "id-binary-stronger-unique":
                schema = ReplaceColumnText(schema, "Id", " UNIQUE", "");
                additionalSql = $"CREATE UNIQUE INDEX id_nocase ON {SourceSchemaFixture.Table}(Id COLLATE NOCASE)";
                break;
            case "compatible-affinities":
                schema = ReplaceColumnText(schema, "Title", "TEXT", "VARCHAR(500)");
                schema = ReplaceColumnText(schema, "SourceContentRevision", "INTEGER", "BIGINT");
                break;
            case "extra-nullable":
                schema = AddDefinition(schema, "ExtraValue BLOB");
                break;
            case "extra-default":
                schema = AddDefinition(schema, "ExtraValue TEXT NOT NULL DEFAULT ('kept')");
                break;
            case "extra-generated":
                schema = AddDefinition(schema, "ExtraValue TEXT GENERATED ALWAYS AS (Title || Key) VIRTUAL");
                break;
            case "extra-nullable-unique":
                schema = AddDefinition(schema, "ExtraValue TEXT");
                additionalSql = $"CREATE UNIQUE INDEX extra_unique ON {SourceSchemaFixture.Table}(ExtraValue)";
                break;
            case "redundant-unique-indexes":
                additionalSql = $"""
                    CREATE UNIQUE INDEX extra_rowid ON {SourceSchemaFixture.Table}(RowId DESC, Title);
                    CREATE UNIQUE INDEX extra_id ON {SourceSchemaFixture.Table}(Id COLLATE BINARY, Title);
                    CREATE UNIQUE INDEX extra_business_key ON {SourceSchemaFixture.Table}(SourceTicketShape COLLATE BINARY, Key COLLATE NOCASE);
                    """;
                break;
            case "unrelated-expression-index":
                additionalSql = $"""
                    CREATE INDEX extra_expression ON {SourceSchemaFixture.Table}(LOWER(Title)) WHERE Title <> '';
                    """;
                break;
            case "reordered-columns":
                schema = AddDefinition(RemoveColumn(schema, "Title"), "Title TEXT NOT NULL");
                break;
        }
        SourceSchemaFixture.Execute(connection, schema);
        fixture.SeedRows(connection);
        SourceSchemaFixture.Execute(connection, additionalSql);
        if (scenario == "non-fhir-whitespace")
        {
            SourceSchemaFixture.Execute(connection, """
                UPDATE jira_processing_source_tickets SET Key = 'mixed-identity', SourceTicketShape = 'pss' WHERE RowId = 101;
                UPDATE jira_processing_source_tickets SET Key = 'MIXED-IDENTITY', SourceTicketShape = ' pss ' WHERE RowId = 202;
                """);
        }
        else if (scenario == "non-ascii-distinct")
        {
            SourceSchemaFixture.Execute(connection, """
                UPDATE jira_processing_source_tickets SET Key = 'FHIR-é' WHERE RowId = 101;
                UPDATE jira_processing_source_tickets SET Key = 'FHIR-É' WHERE RowId = 202;
                """);
        }
        SqliteRows before = ReadSourceRows(connection);
        SqliteRows sentinel = ReadTypedRows(connection, "SELECT * FROM unrelated_state ORDER BY Id");

        JiraProcessingSourceTicketStore.EnsureSchema(connection);

        AssertRowsEqual(before, ReadSourceRows(connection, before.Columns));
        AssertRowsEqual(sentinel, ReadTypedRows(connection, "SELECT * FROM unrelated_state ORDER BY Id"));
        DatabaseSnapshot after = ReadDatabaseSnapshot(connection);
        JiraProcessingSourceTicketStore.EnsureSchema(connection);
        _ = new JiraProcessingSourceTicketStore(fixture.DatabasePath);
        AssertDatabaseEqual(after, ReadDatabaseSnapshot(connection));
        await AssertStoreIsUsableAsync(
            fixture, populated: scenario is not ("non-fhir-whitespace" or "non-ascii-distinct"));
    }

    [Theory]
    [InlineData("constant", false)]
    [InlineData("constant", true)]
    [InlineData("wrong-column", true)]
    [InlineData("expression", true)]
    [InlineData("partial", true)]
    [InlineData("other-table", false)]
    public void EnsureSchema_RefusesIncompatibleCompletionIndexesWithoutMutation(
        string definition,
        bool completionColumn)
    {
        string name = DiscoverCompletionIndexName();
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        fixture.CreateLegacy(connection, completionColumn: completionColumn);
        fixture.SeedRows(connection);
        string sql = definition switch
        {
            "constant" => $"CREATE INDEX {QuoteIdentifier(name)} ON {SourceSchemaFixture.Table}(('CompletionId' || ''))",
            "wrong-column" => $"CREATE INDEX {QuoteIdentifier(name)} ON {SourceSchemaFixture.Table}(Title)",
            "expression" => $"CREATE INDEX {QuoteIdentifier(name)} ON {SourceSchemaFixture.Table}(LOWER(CompletionId))",
            "partial" => $"CREATE INDEX {QuoteIdentifier(name)} ON {SourceSchemaFixture.Table}(CompletionId) WHERE CompletionId IS NOT NULL",
            "other-table" => $"CREATE INDEX {QuoteIdentifier(name)} ON unrelated_state(TextValue)",
            _ => throw new InvalidOperationException($"Unknown test definition: {definition}"),
        };
        SourceSchemaFixture.Execute(connection, sql);

        AssertRefusedWithoutMutation(
            fixture, connection, "completion index definition", compositeToo: false, beforeDdl: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureSchema_DoesNotUpdateOrDeleteRetainedRows(bool completionColumn)
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        fixture.CreateLegacy(connection, completionColumn: completionColumn);
        fixture.SeedRows(connection);
        SourceSchemaFixture.Execute(connection, """
            CREATE UNIQUE INDEX idx_jira_processing_source_tickets_key_shape
            ON jira_processing_source_tickets(Key, SourceTicketShape);
            CREATE TRIGGER refuse_source_update BEFORE UPDATE ON jira_processing_source_tickets
            BEGIN SELECT RAISE(ABORT, 'initializer attempted UPDATE'); END;
            CREATE TRIGGER refuse_source_delete BEFORE DELETE ON jira_processing_source_tickets
            BEGIN SELECT RAISE(ABORT, 'initializer attempted DELETE'); END;
            """);
        SqliteRows before = ReadSourceRows(connection);
        SqliteRows triggers = ReadTypedRows(connection, "SELECT name, sql FROM sqlite_schema WHERE type = 'trigger' ORDER BY name");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            JiraProcessingSourceTicketStore.EnsureSchema(connection);
            JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex(connection);
            _ = new JiraProcessingSourceTicketStore(fixture.DatabasePath);
            AssertRowsEqual(before, ReadSourceRows(connection, before.Columns));
            AssertRowsEqual(triggers, ReadTypedRows(connection, "SELECT name, sql FROM sqlite_schema WHERE type = 'trigger' ORDER BY name"));
        }
    }

    [Fact]
    public async Task EnsureSchema_RollsBackAllDdlAfterGeneratedIndexFailure()
    {
        string name = DiscoverCompletionIndexName();
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        fixture.CreateLegacy(connection, compatibilityColumns: false);
        fixture.SeedRows(connection);
        SourceSchemaFixture.Execute(connection, $"""
            CREATE UNIQUE INDEX {SourceSchemaFixture.CompositeIndex}
            ON {SourceSchemaFixture.Table}(Key, SourceTicketShape);
            CREATE TABLE {QuoteIdentifier(name)}(Id INTEGER PRIMARY KEY, Value TEXT, Bytes BLOB);
            INSERT INTO {QuoteIdentifier(name)} VALUES (19, 'fixture obstruction', X'01FE');
            """);
        DatabaseSnapshot before = ReadDatabaseSnapshot(connection);
        int additions = 0;
        int indexCreations = 0;
        SetAuthorizer(connection, (_, action, _, _, _, _) =>
        {
            if (action == SQLitePCL.raw.SQLITE_ALTER_TABLE)
            {
                additions++;
            }
            if (action == SQLitePCL.raw.SQLITE_CREATE_INDEX)
            {
                indexCreations++;
            }
            return SQLitePCL.raw.SQLITE_OK;
        });
        try
        {
            SqliteException failure = Assert.Throws<SqliteException>(
                () => JiraProcessingSourceTicketStore.EnsureSchema(connection));
            Assert.Equal(1, failure.SqliteErrorCode);
        }
        finally
        {
            SetAuthorizer(connection, null);
        }

        Assert.Equal(4, additions);
        Assert.True(indexCreations > 0);
        AssertDatabaseEqual(before, ReadDatabaseSnapshot(connection));
        Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
        SourceSchemaFixture.Execute(connection, $"DROP TABLE {QuoteIdentifier(name)}");
        JiraProcessingSourceTicketStore.EnsureSchema(connection);
        AssertRowsEqual(
            before.Tables[SourceSchemaFixture.Table],
            ReadSourceRows(connection, before.Tables[SourceSchemaFixture.Table].Columns));
        AssertCompletionIndex(connection);
        AssertCompositeIndex(connection);
        await AssertStoreIsUsableAsync(fixture, populated: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureSchema_WriterContentionAllowsCleanRetry(bool fullSchema)
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection holder = fixture.OpenConnection();
        fixture.CreateLegacy(holder);
        fixture.SeedRows(holder);
        using SqliteConnection contender = fixture.OpenConnection();
        DatabaseSnapshot before = ReadDatabaseSnapshot(holder);
        List<(int Action, string Detail)> observations = [];
        SetAuthorizer(contender, (_, action, detail, _, _, _) =>
        {
            observations.Add((action, detail));
            return SQLitePCL.raw.SQLITE_OK;
        });
        SourceSchemaFixture.Execute(holder, "BEGIN IMMEDIATE");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?> attempt = Task.Run<Exception?>(() =>
        {
            started.SetResult();
            return Record.Exception(() => Initialize(contender, fullSchema));
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            SqliteException failure = Assert.IsType<SqliteException>(
                await attempt.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(5, failure.SqliteErrorCode);
            Assert.Equal((SQLitePCL.raw.SQLITE_TRANSACTION, "BEGIN"), observations[0]);
            Assert.DoesNotContain(observations, observation =>
                observation.Action is SQLitePCL.raw.SQLITE_READ or SQLitePCL.raw.SQLITE_PRAGMA);
            Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(contender.Handle));
            SetAuthorizer(contender, null);
            AssertDatabaseEqual(before, ReadDatabaseSnapshot(contender));
        }
        finally
        {
            SourceSchemaFixture.Execute(holder, "ROLLBACK");
            await attempt.WaitAsync(TimeSpan.FromSeconds(10));
            SetAuthorizer(contender, null);
        }

        Initialize(contender, fullSchema);
        Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(contender.Handle));
        SourceSchemaFixture.Execute(holder, "BEGIN IMMEDIATE; ROLLBACK");
        AssertRowsEqual(
            before.Tables[SourceSchemaFixture.Table],
            ReadSourceRows(contender, before.Tables[SourceSchemaFixture.Table].Columns));
        holder.Close();
        contender.Close();
        AssertExclusiveAccess(fixture.DatabasePath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EnsureSchema_NestedBeginFailureDoesNotRollbackCallerWork(bool fullSchema, bool managedTransaction)
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        fixture.CreateLegacy(connection);
        fixture.SeedRows(connection);
        DatabaseSnapshot before = ReadDatabaseSnapshot(connection);
        using SqliteTransaction? transaction = managedTransaction ? connection.BeginTransaction() : null;
        if (!managedTransaction)
        {
            SourceSchemaFixture.Execute(connection, "BEGIN IMMEDIATE");
        }
        using SqliteCommand caller = connection.CreateCommand();
        caller.Transaction = transaction;
        caller.CommandText = "INSERT INTO unrelated_state(Id, TextValue) VALUES (77, 'caller-owned-uncommitted')";
        caller.ExecuteNonQuery();

        Exception? failure = Record.Exception(() => Initialize(connection, fullSchema));

        Assert.True(failure is SqliteException { SqliteErrorCode: 1 } or InvalidOperationException);
        Assert.Equal(0, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
        caller.CommandText = "SELECT TextValue FROM unrelated_state WHERE Id = 77";
        Assert.Equal("caller-owned-uncommitted", caller.ExecuteScalar());
        if (transaction is not null)
        {
            transaction.Rollback();
        }
        else
        {
            SourceSchemaFixture.Execute(connection, "ROLLBACK");
        }
        AssertDatabaseEqual(before, ReadDatabaseSnapshot(connection));
        Initialize(connection, fullSchema);
        Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureSchema_RollbackFailureDoesNotReplaceOriginalRefusal(bool fullSchema)
    {
        using SourceSchemaFixture fixture = new();
        using SqliteConnection connection = fixture.OpenConnection();
        SourceSchemaFixture.Execute(connection, RemoveColumn(SourceSchemaFixture.ReportedSchema, "ErrorMessage"));
        fixture.SeedRows(connection);
        DatabaseSnapshot before = ReadDatabaseSnapshot(connection);
        int rollbacks = 0;
        SetAuthorizer(connection, (_, action, detail, _, _, _) =>
        {
            if (action == SQLitePCL.raw.SQLITE_TRANSACTION && detail == "ROLLBACK")
            {
                rollbacks++;
                return SQLitePCL.raw.SQLITE_DENY;
            }
            return SQLitePCL.raw.SQLITE_OK;
        });
        try
        {
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => Initialize(connection, fullSchema));
            Assert.Contains("missing required columns", failure.Message);
            Assert.Contains("ErrorMessage", failure.Message);
            Assert.Equal(1, rollbacks);
            Assert.Equal(0, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
        }
        finally
        {
            SetAuthorizer(connection, null);
            SourceSchemaFixture.Execute(connection, "ROLLBACK");
        }
        AssertDatabaseEqual(before, ReadDatabaseSnapshot(connection));
    }

    [Theory]
    [InlineData("fHiR-1", "fhir", "FHIR-1", "fhir")]
    [InlineData(" fHiR-2 ", " FHIR ", " FHIR-2 ", "fhir")]
    [InlineData("fhir-éßıiİ-3", "FhIr", "FHIR-éßıIİ-3", "fhir")]
    [InlineData(" pSs-4 ", " PSS ", " pSs-4 ", "pss")]
    [InlineData("fHiR-5", "other", "fHiR-5", "other")]
    public async Task Upsert_CanonicalizesOnlyNewFhirKeys(
        string inputKey,
        string inputShape,
        string persistedKey,
        string persistedShape)
    {
        using SourceSchemaFixture fixture = new();
        JiraProcessingSourceTicketStore store = new(fixture.DatabasePath);
        JiraIssueSummaryEntry input = CreateTicket(inputKey) with { UpdatedAt = null };
        JiraProcessingSourceTicketRecord inserted = await store.UpsertAsync(
            input, inputShape, false, CancellationToken.None);
        Assert.Equal(persistedKey, inserted.Key);
        Assert.Equal(persistedShape, inserted.SourceTicketShape);
        JiraProcessingSourceTicketRecord stored = Assert.IsType<JiraProcessingSourceTicketRecord>(
            await store.GetByKeyAsync(inputKey, inputShape, CancellationToken.None));
        Assert.Equal(inserted.Key, stored.Key);
        Assert.Equal(inserted.Id, stored.Id);
        Assert.Equal(JiraSourceRevision.Compute(inserted), JiraSourceRevision.Compute(stored));
        using (SqliteConnection connection = fixture.OpenConnection())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT UPPER(@key)";
            command.Parameters.AddWithValue("@key", inputKey);
            if (persistedShape == "fhir")
            {
                Assert.Equal(persistedKey, command.ExecuteScalar());
            }
            else
            {
                Assert.Equal(
                    JiraSourceRevision.Compute(stored with { Key = inputKey }),
                    JiraSourceRevision.Compute(stored));
            }
        }
        _ = new JiraProcessingSourceTicketStore(fixture.DatabasePath);
        Assert.Equal(stored, await store.GetByIdAsync(stored.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("fhir-1", "FHIR")]
    [InlineData("pss-1", "PSS")]
    public async Task Upsert_DoesNotRewriteExistingIdentity(string key, string shape)
    {
        using SourceSchemaFixture fixture = new();
        JiraProcessingSourceTicketStore store = new(fixture.DatabasePath);
        JiraProcessingSourceTicketRecord inserted = await store.UpsertAsync(
            CreateTicket(key) with { UpdatedAt = null }, shape, false, CancellationToken.None);
        await store.MarkCompleteAsync(inserted, DateTimeOffset.UtcNow, CancellationToken.None);
        using (SqliteConnection connection = fixture.OpenConnection())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE jira_processing_source_tickets SET Key = @key, SourceTicketShape = @shape WHERE Id = @id";
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@shape", shape);
            command.Parameters.AddWithValue("@id", inserted.Id);
            command.ExecuteNonQuery();
        }
        JiraProcessingSourceTicketRecord before = Assert.IsType<JiraProcessingSourceTicketRecord>(
            await store.GetByIdAsync(inserted.Id, CancellationToken.None));
        string historicalRevision = JiraSourceRevision.Compute(before);

        JiraProcessingSourceTicketRecord updated = await store.UpsertAsync(
            CreateTicket(key.ToUpperInvariant()) with { UpdatedAt = null },
            shape.ToLowerInvariant(), false, CancellationToken.None);

        Assert.Equal(before.Key, updated.Key);
        Assert.Equal(before.SourceTicketShape, updated.SourceTicketShape);
        Assert.Equal(before.Id, updated.Id);
        Assert.Equal(before.RowId, updated.RowId);
        Assert.Equal(before.CompletionId, updated.CompletionId);
        Assert.Equal(before.CompletedProcessingAt, updated.CompletedProcessingAt);
        Assert.Equal(before.ProcessingStatus, updated.ProcessingStatus);
        Assert.Equal(historicalRevision, JiraSourceRevision.Compute(updated));
        Assert.Equal(updated, await store.GetByIdAsync(updated.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceRevision_CanonicalNewKeyIsStableAcrossReopen(bool timestamped)
    {
        using SourceSchemaFixture fixture = new();
        JiraProcessingSourceTicketStore store = new(fixture.DatabasePath);
        JiraIssueSummaryEntry input = CreateTicket("fHiR-91", specification: "fhir-core") with
        {
            UpdatedAt = timestamped ? new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero) : null,
        };
        JiraProcessingSourceTicketRecord inserted = await store.UpsertAsync(input, "FHIR", false, CancellationToken.None);
        string persistedRevision = JiraSourceRevision.Compute(inserted);
        string rawRevision = JiraSourceRevision.Compute(inserted with { Key = input.Key });
        Assert.Equal(timestamped, JiraSourceRevision.AreEquivalent(persistedRevision, rawRevision));
        JiraProcessingSourceTicketRecord before = Assert.IsType<JiraProcessingSourceTicketRecord>(
            await store.GetByIdAsync(inserted.Id, CancellationToken.None));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            store = new JiraProcessingSourceTicketStore(fixture.DatabasePath);
            Assert.Equal(before, await store.GetByIdAsync(inserted.Id, CancellationToken.None));
            using SqliteConnection connection = fixture.OpenConnection();
            await JiraProcessingSourceTicketStore.EnsureCurrentSourceRevisionAsync(
                connection, input.Key, "fhir", persistedRevision, CancellationToken.None);
            if (!timestamped)
            {
                AuthoringConflictException failure = await Assert.ThrowsAsync<AuthoringConflictException>(
                    () => JiraProcessingSourceTicketStore.EnsureCurrentSourceRevisionAsync(
                        connection, input.Key, "fhir", rawRevision, CancellationToken.None));
                Assert.Equal(AuthoringConflictCode.SourceRevisionMismatch, failure.Code);
            }
        }
        JiraProcessingSourceTicketRecord repeated = await store.UpsertAsync(
            input with { Key = "fhIR-91" }, " fhir ", false, CancellationToken.None);
        Assert.Equal(before.Key, repeated.Key);
        Assert.Equal(before.Id, repeated.Id);
        Assert.Equal(before.RowId, repeated.RowId);
        Assert.Equal(persistedRevision, JiraSourceRevision.Compute(repeated));
    }

    [Fact]
    public async Task CompletedOperations_ReleaseDatabaseFileForExclusiveMoveAndDelete()
    {
        string directory = Path.Combine(
            Environment.CurrentDirectory,
            "temp",
            "jira-processing-handle-release",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "source.db");
        string movedPath = Path.Combine(directory, "source.moved.db");
        try
        {
            JiraProcessingSourceTicketStore store = new(path);
            JiraProcessingSourceTicketRecord record = await store.UpsertAsync(
                CreateTicket("FHIR-100"),
                "fhir",
                false,
                CancellationToken.None);
            await store.MarkCompleteAsync(record, DateTimeOffset.UtcNow, CancellationToken.None);

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            File.Move(path, movedPath);
            File.Delete(movedPath);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task LocalAuthoringCandidates_DoNotReselectSupersededUnchangedRevision()
    {
        ResolvedJiraProcessingFilters filters = new()
        {
            TicketStatuses = ["Triaged"],
            SourceTicketShape = "fhir",
        };
        JiraProcessingSourceTicketStore store = CreateStore(filters);
        DateTimeOffset firstRevision =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        JiraIssueSummaryEntry firstTicket = CreateTicket("FHIR-1") with
        {
            UpdatedAt = firstRevision,
        };
        JiraProcessingSourceTicketRecord source = await store.UpsertAsync(
            firstTicket,
            "fhir",
            false,
            CancellationToken.None);
        JiraProcessingDatabase database = new(
            store.DatabasePath,
            NullLogger<JiraProcessingDatabase>.Instance);
        database.Initialize();
        AuthoringRetryPolicy policy = new(Options.Create(
            new ProcessingServiceOptions { AuthoringMaxAttempts = 1 }));
        AuthoringRunStore authoringStore = new(
            database,
            retryPolicy: policy);
        await authoringStore.EnsureProcessorModeAsync("jira-fhir");
        await authoringStore.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        await authoringStore.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);
        AuthoringRunRecord run = await authoringStore.CreateRunAsync(
            "jira-fhir",
            [
                new AuthoringRunItemDefinition(
                    source.Key,
                    source.SourceTicketShape,
                    JiraProcessingSourceTicketStore.GetSourceRevision(source)),
            ]);
        Assert.True(await authoringStore.TryAcquireMutationFenceAsync(
            "jira-fhir",
            run.Id));
        AuthoringRunItemRecord item = Assert.Single(
            await authoringStore.GetRunItemsAsync(run.Id));
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await authoringStore.ClaimItemAsync(run.Id, item.Id));
        await authoringStore.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "terminal failure");

        Assert.Empty(await store.GetLocalAuthoringCandidatesAsync(filters, 10, CancellationToken.None));

        JiraIssueSummaryEntry updatedTicket =
            CreateTicket("FHIR-1", title: "Updated") with
            {
                UpdatedAt = firstRevision.AddMinutes(1),
            };
        await store.UpsertAsync(
            updatedTicket,
            "fhir",
            false,
            CancellationToken.None);
        Assert.Single(await store.GetLocalAuthoringCandidatesAsync(
            filters,
            10,
            CancellationToken.None));
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task LocalAuthoringCandidates_OptionalLimitAppliesAfterFacetsAndFrozenRevisions(
        int? maxItems,
        int expectedCount)
    {
        using AuthoringFixtureScope scope = new();
        JiraAuthoringTestFixture fixture = scope.Fixture;
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord frozen =
            await fixture.SeedAsync("FHIR-0", revision.AddDays(-1));
        await fixture.Coordinator.CreateOneItemRunAsync(frozen);
        await fixture.SourceStore.UpsertAsync(
            CreateTicket("FHIR-REJECTED", status: "Submitted") with { UpdatedAt = revision.AddDays(-2) },
            "fhir", false, CancellationToken.None);
        await fixture.SourceStore.UpsertAsync(
            CreateTicket("FHIR-SHAPE") with { UpdatedAt = revision.AddDays(-2) },
            "pss", false, CancellationToken.None);
        await fixture.SeedAsync("FHIR-2", revision);
        JiraProcessingSourceTicketRecord completed = await fixture.SeedAsync("FHIR-1", revision);
        await fixture.SourceStore.MarkCompleteAsync(completed, revision, CancellationToken.None);
        await fixture.SeedAsync("FHIR-3", revision.AddMinutes(1));
        ResolvedJiraProcessingFilters filters = new()
        {
            TicketStatuses = ["triaged"],
            LabelsToInclude = ["not-evaluated-locally"],
        };

        IReadOnlyList<JiraProcessingSourceTicketRecord> candidates =
            await fixture.SourceStore.GetLocalAuthoringCandidatesAsync(
                filters, maxItems, CancellationToken.None);

        Assert.Equal(
            new[] { "FHIR-1", "FHIR-2", "FHIR-3" }.Take(expectedCount),
            candidates.Select(ticket => ticket.Key));
        Assert.Equal(ProcessingStatusValues.Complete, candidates[0].ProcessingStatus);
        using (new FileStream(fixture.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
        Assert.Empty(fixture.Matcher.Calls);
    }

    private static void Initialize(SqliteConnection connection, bool fullSchema)
    {
        if (fullSchema)
        {
            JiraProcessingSourceTicketStore.EnsureSchema(connection);
        }
        else
        {
            JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex(connection);
        }
    }

    private static string RemoveColumn(string schema, string column) => string.Join(
        "\n", schema.Split('\n').Where(line => !line.TrimStart().StartsWith($"{column} ", StringComparison.Ordinal)));

    private static string ReplaceColumnText(string schema, string column, string oldValue, string newValue)
        => string.Join("\n", schema.Split('\n').Select(line =>
            line.TrimStart().StartsWith($"{column} ", StringComparison.Ordinal)
                ? line.Replace(oldValue, newValue, StringComparison.Ordinal)
                : line));

    private static string AddDefinition(string schema, string definition)
        => schema.Replace("\n);", $",\n    {definition}\n);", StringComparison.Ordinal);

    private static void SetAuthorizer(
        SqliteConnection connection,
        SQLitePCL.strdelegate_authorizer? authorizer)
        => Assert.Equal(
            SQLitePCL.raw.SQLITE_OK,
            SQLitePCL.raw.sqlite3_set_authorizer(connection.Handle, authorizer, null));

    private static InvalidOperationException AssertRefusedWithoutMutation(
        SourceSchemaFixture fixture,
        SqliteConnection connection,
        string category,
        bool compositeToo = true,
        bool beforeDdl = true)
    {
        DatabaseSnapshot before = ReadDatabaseSnapshot(connection);
        List<int> schemaWrites = [];
        SetAuthorizer(connection, (_, action, _, _, _, _) =>
        {
            if (action is SQLitePCL.raw.SQLITE_CREATE_TABLE or SQLitePCL.raw.SQLITE_CREATE_INDEX or
                SQLitePCL.raw.SQLITE_DROP_INDEX or SQLitePCL.raw.SQLITE_ALTER_TABLE)
            {
                schemaWrites.Add(action);
            }
            return SQLitePCL.raw.SQLITE_OK;
        });
        InvalidOperationException failure;
        try
        {
            failure = Assert.Throws<InvalidOperationException>(
                () => JiraProcessingSourceTicketStore.EnsureSchema(connection));
            if (beforeDdl)
            {
                Assert.Empty(schemaWrites);
            }
        }
        finally
        {
            SetAuthorizer(connection, null);
        }
        Assert.Contains(category, failure.Message);
        Assert.Contains(SourceSchemaFixture.Table, failure.Message);
        Assert.Contains(fixture.DatabasePath, failure.Message);
        Assert.DoesNotContain("FHIR-101", failure.Message);
        Assert.DoesNotContain("Retained title", failure.Message);
        Assert.DoesNotContain("retained error detail", failure.Message);
        Assert.DoesNotContain("Data Source=", failure.Message);
        Assert.DoesNotContain("Pooling=", failure.Message);
        AssertDatabaseEqual(before, ReadDatabaseSnapshot(connection));
        Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
        if (compositeToo)
        {
            Assert.Contains(category, Assert.Throws<InvalidOperationException>(
                () => JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex(connection)).Message);
            AssertDatabaseEqual(before, ReadDatabaseSnapshot(connection));
            Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
        }
        Assert.Contains(category, Assert.Throws<InvalidOperationException>(
            () => new JiraProcessingSourceTicketStore(fixture.DatabasePath)).Message);
        AssertDatabaseEqual(before, ReadDatabaseSnapshot(connection));
        using SqliteConnection other = fixture.OpenConnection();
        SourceSchemaFixture.Execute(other, "BEGIN IMMEDIATE; ROLLBACK");
        return failure;
    }

    private static void AssertSchemaExcept(DatabaseSnapshot before, DatabaseSnapshot after, string indexName)
    {
        AssertRowsEqual(
            before.Schema with { Values = before.Schema.Values.Where(row => !Equals(row[1], indexName)).ToArray() },
            after.Schema with { Values = after.Schema.Values.Where(row => !Equals(row[1], indexName)).ToArray() });
        Assert.Equal(before.Tables.Keys, after.Tables.Keys);
        foreach ((string table, SqliteRows rows) in before.Tables)
        {
            AssertRowsEqual(rows, after.Tables[table]);
        }
    }

    private static void AssertCompositeIndex(SqliteConnection connection)
    {
        object?[] index = Assert.Single(
            ReadTypedRows(connection, $"PRAGMA index_list({SourceSchemaFixture.Table})").Values,
            row => Equals(row[1], SourceSchemaFixture.CompositeIndex));
        Assert.Equal(1L, index[2]);
        Assert.Equal(0L, index[4]);
        object?[][] terms = ReadTypedRows(
                connection, $"PRAGMA index_xinfo({SourceSchemaFixture.CompositeIndex})").Values
            .Where(row => Equals(row[5], 1L)).ToArray();
        Assert.Equal(new[] { "Key", "SourceTicketShape" }, terms.Select(row => row[2]));
        Assert.All(terms, term =>
        {
            Assert.True(Assert.IsType<long>(term[1]) >= 0);
            Assert.Equal("NOCASE", term[4]);
        });
    }

    private static string DiscoverCompletionIndexName()
    {
        using SourceSchemaFixture control = new();
        using SqliteConnection connection = control.OpenConnection();
        JiraProcessingSourceTicketRecord.CreateTable(connection);
        return AssertCompletionIndex(connection);
    }

    private static void AssertExclusiveAccess(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task AssertStoreIsUsableAsync(SourceSchemaFixture fixture, bool populated)
    {
        JiraProcessingSourceTicketStore store = new(fixture.DatabasePath);
        SqliteRows retained;
        using (SqliteConnection connection = fixture.OpenConnection())
        {
            AuthoringRunStore.EnsureSchema(connection);
            retained = ReadSourceRows(connection);
        }

        if (populated)
        {
            JiraProcessingSourceTicketRecord pending =
                Assert.IsType<JiraProcessingSourceTicketRecord>(
                    await store.GetByKeyAsync("fhir-101", "FHIR", CancellationToken.None));
            Assert.Equal(101, pending.RowId);
            Assert.Equal("retained-101", pending.Id);
            Assert.Equal(
                pending,
                await store.GetByIdAsync(pending.Id, CancellationToken.None));
            JiraProcessingSourceTicketRecord completed =
                Assert.IsType<JiraProcessingSourceTicketRecord>(
                    await store.GetByIdAsync("retained-202", CancellationToken.None));
            Assert.Equal(ProcessingStatusValues.Complete, completed.ProcessingStatus);
            Assert.Equal(3, completed.ProcessingAttemptCount);
            Assert.Null(completed.LastUpdated);
            Assert.Equal(new DateTimeOffset(2026, 9, 4, 11, 0, 0, TimeSpan.Zero), completed.CompletedProcessingAt);
            object?[] completedValues = Assert.Single(
                retained.Values, row => Equals(row[Array.IndexOf(retained.Columns, "Id")], completed.Id));
            Assert.Equal(
                completedValues[Array.IndexOf(retained.Columns, "CompletionId")],
                completed.CompletionId);
            JiraProcessingSourceTicketRecord errored =
                Assert.IsType<JiraProcessingSourceTicketRecord>(
                    await store.GetByKeyAsync("fhir-303", "fhir", CancellationToken.None));
            Assert.Equal(ProcessingStatusValues.Error, errored.ProcessingStatus);
            Assert.Equal("retained processing error", errored.ProcessingError);
            Assert.Equal("retained error detail", errored.ErrorMessage);
            Assert.Equal(23, errored.AgentExitCode);
            Assert.Equal(
                new[] { "FHIR-101", "FHIR-505" },
                (await store.GetPendingAsync(10, CancellationToken.None))
                    .Select(row => row.Key).Order());
            Assert.Equal(
                new[] { "FHIR-101", "FHIR-202", "FHIR-303", "FHIR-404", "FHIR-505" },
                (await store.GetLocalAuthoringCandidatesAsync(
                    new ResolvedJiraProcessingFilters(),
                    10,
                    CancellationToken.None)).Select(row => row.Key).Order());
        }

        JiraProcessingSourceTicketRecord inserted = await store.UpsertAsync(
            CreateTicket("FHIR-999", specification: "fhir-extensions"),
            "fhir", false, CancellationToken.None);
        DateTimeOffset completedAt = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        await store.MarkCompleteAsync(inserted, completedAt, CancellationToken.None);
        string completionId = Assert.IsType<string>(inserted.CompletionId);
        await store.MarkCompleteAsync(inserted, completedAt, CancellationToken.None);
        Assert.Equal(completionId, inserted.CompletionId);
        JiraProcessingSourceTicketRecord reloaded =
            Assert.IsType<JiraProcessingSourceTicketRecord>(
                await store.GetByIdAsync(inserted.Id, CancellationToken.None));
        Assert.True(reloaded.RowId > 0);
        Assert.Equal(inserted.Key, reloaded.Key);
        Assert.Equal(completionId, reloaded.CompletionId);
        Assert.Equal(completedAt, reloaded.CompletedProcessingAt);
        using (SqliteConnection connection = fixture.OpenConnection())
        {
            SqliteRows after = ReadSourceRows(connection);
            int idColumn = Array.IndexOf(after.Columns, "Id");
            AssertRowsEqual(
                retained,
                after with { Values = after.Values.Where(row => !Equals(row[idColumn], inserted.Id)).ToArray() });
        }
    }

    private static string AssertCompletionIndex(SqliteConnection connection)
    {
        return Assert.Single(
            ReadIndexes(connection, SourceSchemaFixture.Table),
            index => IndexCovers(connection, index.Name, ["CompletionId"])).Name;
    }

    private sealed record SqliteRows(string[] Columns, object?[][] Values);

    private sealed record DatabaseSnapshot(
        long SchemaVersion,
        SqliteRows Schema,
        IReadOnlyDictionary<string, SqliteRows> Tables);

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static SqliteRows ReadTypedRows(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        string[] columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        List<object?[]> values = [];
        while (reader.Read())
        {
            values.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        }
        return new SqliteRows(columns, values.ToArray());
    }

    private static SqliteRows ReadSourceRows(
        SqliteConnection connection,
        IReadOnlyList<string>? columns = null)
    {
        string projection = columns is null ? "*" : string.Join(", ", columns.Select(QuoteIdentifier));
        return ReadTypedRows(
            connection,
            $"SELECT {projection} FROM {SourceSchemaFixture.Table} ORDER BY RowId");
    }

    private static DatabaseSnapshot ReadDatabaseSnapshot(SqliteConnection connection)
    {
        SqliteRows schema = ReadTypedRows(
            connection,
            "SELECT type, name, tbl_name, rootpage, sql FROM sqlite_schema ORDER BY type, name");
        Dictionary<string, SqliteRows> tables = [];
        foreach (object?[] row in schema.Values.Where(row => Equals(row[0], "table")))
        {
            string name = Assert.IsType<string>(row[1]);
            tables.Add(name, ReadTypedRows(connection, $"SELECT * FROM {QuoteIdentifier(name)} ORDER BY 1"));
        }
        long version = Assert.IsType<long>(
            Assert.Single(ReadTypedRows(connection, "PRAGMA schema_version").Values)[0]);
        return new DatabaseSnapshot(version, schema, tables);
    }

    private static void AssertRowsEqual(SqliteRows expected, SqliteRows actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Values.Length, actual.Values.Length);
        for (int row = 0; row < expected.Values.Length; row++)
        {
            Assert.Equal(expected.Values[row].Length, actual.Values[row].Length);
            for (int column = 0; column < expected.Values[row].Length; column++)
            {
                Assert.Equal(expected.Values[row][column]?.GetType(), actual.Values[row][column]?.GetType());
                Assert.Equal(expected.Values[row][column], actual.Values[row][column]);
            }
        }
    }

    private static void AssertDatabaseEqual(DatabaseSnapshot expected, DatabaseSnapshot actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        AssertRowsEqual(expected.Schema, actual.Schema);
        Assert.Equal(expected.Tables.Keys, actual.Tables.Keys);
        foreach ((string table, SqliteRows rows) in expected.Tables)
        {
            AssertRowsEqual(rows, actual.Tables[table]);
        }
    }

    private sealed class SourceSchemaFixture : IDisposable
    {
        public const string Table = "jira_processing_source_tickets";
        public const string CompositeIndex = "idx_jira_processing_source_tickets_key_shape";

        public const string ReportedSchema = """
            CREATE TABLE jira_processing_source_tickets (
                RowId INTEGER UNIQUE PRIMARY KEY NOT NULL,
                Id TEXT UNIQUE NOT NULL,
                Key TEXT NOT NULL,
                Title TEXT NOT NULL,
                Description TEXT,
                Project TEXT NOT NULL,
                Status TEXT NOT NULL,
                WorkGroup TEXT NOT NULL,
                Type TEXT NOT NULL,
                Specification TEXT NOT NULL DEFAULT '',
                SourceTicketShape TEXT NOT NULL,
                LastSyncedAt TEXT NOT NULL,
                LastUpdated TEXT,
                SourceProjectLastSuccessfulRefreshAt TEXT,
                SourceContentRevision INTEGER,
                StartedProcessingAt TEXT,
                CompletedProcessingAt TEXT,
                LastProcessingAttemptAt TEXT,
                ProcessingStatus TEXT,
                ProcessingError TEXT,
                ProcessingAttemptCount INTEGER NOT NULL,
                ErrorMessage TEXT,
                AgentExitCode INTEGER,
                ErrorOccurredAt TEXT
            );
            """;

        private const string PreCompatibilitySchema = """
            CREATE TABLE jira_processing_source_tickets (
                RowId INTEGER UNIQUE PRIMARY KEY NOT NULL,
                Id TEXT UNIQUE NOT NULL,
                Key TEXT NOT NULL,
                Title TEXT NOT NULL,
                Description TEXT,
                Project TEXT NOT NULL,
                Status TEXT NOT NULL,
                WorkGroup TEXT NOT NULL,
                Type TEXT NOT NULL,
                SourceTicketShape TEXT NOT NULL,
                LastSyncedAt TEXT NOT NULL,
                LastUpdated TEXT,
                StartedProcessingAt TEXT,
                CompletedProcessingAt TEXT,
                LastProcessingAttemptAt TEXT,
                ProcessingStatus TEXT,
                ProcessingError TEXT,
                ProcessingAttemptCount INTEGER NOT NULL,
                ErrorMessage TEXT,
                AgentExitCode INTEGER,
                ErrorOccurredAt TEXT
            );
            """;

        public static string CurrentSchema => ReportedSchema.Replace(
            "ErrorOccurredAt TEXT", "ErrorOccurredAt TEXT,\n    CompletionId TEXT");

        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), $"fhir-augury-source-schema-{Guid.NewGuid():N}");

        public string DatabasePath => Path.Combine(_directory, "source.db");

        public SourceSchemaFixture()
        {
            Directory.CreateDirectory(_directory);
            using SqliteConnection connection = OpenConnection();
            Execute(connection, """
                CREATE TABLE unrelated_state (Id INTEGER PRIMARY KEY, TextValue TEXT, NumberValue REAL, Bytes BLOB, OptionalValue TEXT);
                INSERT INTO unrelated_state VALUES (7, 'retain this sentinel', 1.25, X'0001FEFF', NULL);
                CREATE INDEX unrelated_state_text ON unrelated_state(TextValue);
                """);
        }

        public SqliteConnection OpenConnection()
        {
            SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            return connection;
        }

        public void CreateLegacy(
            SqliteConnection connection,
            bool compatibilityColumns = true,
            bool completionColumn = false)
        {
            string schema = compatibilityColumns ? ReportedSchema : PreCompatibilitySchema;
            if (completionColumn)
            {
                schema = schema.Replace("ErrorOccurredAt TEXT", "ErrorOccurredAt TEXT,\n    CompletionId TEXT");
            }
            Execute(connection, schema);
        }

        public void SeedRows(SqliteConnection connection, bool satisfyNotNull = false)
        {
            HashSet<string> columns = ReadTableInfo(connection, Table).Keys.ToHashSet();
            HashSet<string> requiredColumns = ReadTypedRows(
                    connection, $"PRAGMA table_info({Table})").Values
                .Where(row => Equals(row[3], 1L)).Select(row => (string)row[1]!).ToHashSet();
            string?[] statuses =
            [
                null, ProcessingStatusValues.Complete, ProcessingStatusValues.Error,
                ProcessingStatusValues.InProgress, ProcessingStatusValues.Stale,
            ];
            for (int index = 0; index < statuses.Length; index++)
            {
                int rowId = (index + 1) * 101;
                Dictionary<string, object?> values = new()
                {
                    ["RowId"] = rowId,
                    ["Id"] = $"retained-{rowId}",
                    ["Key"] = $"FHIR-{rowId}",
                    ["Title"] = $"Retained title {rowId}",
                    ["Description"] = $"Retained description {rowId}\nwith Unicode \u00e9 and 'quotes'",
                    ["Project"] = "FHIR",
                    ["Status"] = "Triaged",
                    ["WorkGroup"] = "Infrastructure",
                    ["Type"] = "Change Request",
                    ["Specification"] = index % 2 == 0 ? "fhir-core" : "fhir-extensions",
                    ["SourceTicketShape"] = "fhir",
                    ["LastSyncedAt"] = $"2026-09-0{index + 1}T12:00:00.0000000-05:00",
                    ["LastUpdated"] = index == 1 ? null : $"2026-08-0{index + 1}T01:02:03.1234567+00:00",
                    ["SourceProjectLastSuccessfulRefreshAt"] = index == 0 ? null : "2026-08-31T09:08:07.0000000+00:00",
                    ["SourceContentRevision"] = index == 0 ? null : 4_294_967_296L + index,
                    ["StartedProcessingAt"] = index == 0 ? null : "2026-09-04T10:00:00.0000000+00:00",
                    ["CompletedProcessingAt"] = index is 1 or 2 or 4 ? "2026-09-04T11:00:00.0000000+00:00" : null,
                    ["LastProcessingAttemptAt"] = index == 0 ? null : "2026-09-04T10:01:00.0000000+00:00",
                    ["ProcessingStatus"] = statuses[index],
                    ["ProcessingError"] = index == 2 ? "retained processing error" : null,
                    ["ProcessingAttemptCount"] = index * 3,
                    ["CompletionId"] = index == 1 ? "historical-completion-202" : null,
                    ["ErrorMessage"] = index == 2 ? "retained error detail" : null,
                    ["AgentExitCode"] = index == 2 ? 23 : null,
                    ["ErrorOccurredAt"] = index == 2 ? "2026-09-04T11:00:00.0000000+00:00" : null,
                };
                if (satisfyNotNull)
                {
                    foreach (string name in requiredColumns.Except(values.Keys))
                    {
                        values[name] = $"retained extra value {rowId}";
                    }
                }
                string[] present = values.Keys.Where(columns.Contains).ToArray();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    $"INSERT INTO {Table} ({string.Join(", ", present.Select(QuoteIdentifier))}) " +
                    $"VALUES ({string.Join(", ", present.Select(name => $"@{name}"))})";
                foreach (string name in present)
                {
                    object? value = values[name];
                    if (satisfyNotNull && value is null && requiredColumns.Contains(name))
                    {
                        value = name == "AgentExitCode" ? 0 : "retained non-null value";
                    }
                    command.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
                }
                command.ExecuteNonQuery();
            }
        }

        public static void Execute(SqliteConnection connection, string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return;
            }
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class AuthoringFixtureScope : IDisposable
    {
        public JiraAuthoringTestFixture Fixture { get; } = new();

        // All fixture connections are non-pooled and operation-scoped. Avoid the
        // shared fixture's legacy process-global pool clearing during disposal.
        public void Dispose() => Directory.Delete(
            Path.GetDirectoryName(Fixture.DatabasePath)
                ?? throw new InvalidOperationException("The fixture has no parent directory."),
            recursive: true);
    }

    private static Dictionary<string, (int Pk, string Type)> ReadTableInfo(SqliteConnection connection, string table)
    {
        Dictionary<string, (int Pk, string Type)> columns = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(reader.GetOrdinal("name"));
            string type = reader.GetString(reader.GetOrdinal("type"));
            int pk = reader.GetInt32(reader.GetOrdinal("pk"));
            columns[name] = (pk, type);
        }

        return columns;
    }

    private static IReadOnlyList<(string Name, bool Unique)> ReadIndexes(SqliteConnection connection, string table)
    {
        List<(string Name, bool Unique)> indexes = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({table});";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(reader.GetOrdinal("name"));
            bool unique = reader.GetInt32(reader.GetOrdinal("unique")) == 1;
            indexes.Add((name, unique));
        }

        return indexes;
    }

    private static bool IndexCovers(SqliteConnection connection, string indexName, IReadOnlyList<string> expectedColumns)
    {
        List<string> actual = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info({indexName});";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(reader.GetOrdinal("name")))
            {
                return false;
            }
            actual.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        if (actual.Count != expectedColumns.Count)
        {
            return false;
        }

        for (int i = 0; i < actual.Count; i++)
        {
            if (!string.Equals(actual[i], expectedColumns[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static JiraProcessingSourceTicketStore CreateStore(ResolvedJiraProcessingFilters? filters = null)
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"jira-processing-{Guid.NewGuid():N}.db");
        return new JiraProcessingSourceTicketStore(path, filters);
    }

    private static JiraIssueSummaryEntry CreateTicket(
        string key,
        string title = "Title",
        string status = "Triaged",
        string specification = "") => new()
    {
        Key = key,
        ProjectKey = "FHIR",
        Title = title,
        Type = "Change Request",
        Status = status,
        WorkGroup = "Infrastructure",
        Specification = specification,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
