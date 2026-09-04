using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Authoring;

public sealed class JiraAuthoringRunCoordinatorTests
{
    [Fact]
    public async Task CreateScheduledRun_LegacyModeIsNotActivatable()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.SeedAsync("FHIR-1", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => fixture.Coordinator.CreateScheduledRunAsync());

        Assert.Equal(AuthoringConflictCode.AuthoringNotActivated, conflict.Code);
    }

    [Fact]
    public async Task CreateScheduledRun_FreezesRevisionAndIgnoresLegacyCompletion()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset firstRevision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord source = await fixture.SeedAsync("FHIR-1", firstRevision);
        await fixture.SourceStore.MarkCompleteAsync(source, DateTimeOffset.UtcNow, CancellationToken.None);
        await fixture.ActivateAsync();

        JiraAuthoringRunCreation first =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.Equal(AuthoringStatusValues.Runs.Running, first.Run.Status);
        Assert.Equal(firstRevision.ToString("O"), Assert.Single(first.Items).ExpectedSourceRevision);
        Assert.Null(await fixture.Coordinator.CreateScheduledRunAsync());

        DateTimeOffset secondRevision = firstRevision.AddDays(1);
        await fixture.SeedAsync("FHIR-1", secondRevision, title: "Updated");
        JiraAuthoringRunCreation second =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;

        Assert.Equal(AuthoringStatusValues.Runs.Queued, second.Run.Status);
        Assert.Equal(secondRevision.ToString("O"), Assert.Single(second.Items).ExpectedSourceRevision);
        Assert.Equal(
            firstRevision.ToString("O"),
            Assert.Single(await fixture.AuthoringStore.GetRunItemsAsync(first.Run.Id)).ExpectedSourceRevision);
    }

    [Fact]
    public async Task CreateRun_DeduplicatesRepeatedBusinessKeys()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        JiraProcessingSourceTicketRecord source = await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        JiraAuthoringRunCreation creation = await fixture.Coordinator.CreateRunAsync(
            [source, source],
            databaseOnly: true);

        Assert.Single(creation.Items);
    }

    [Fact]
    public async Task CreateScheduledRun_ConcurrentAdmissionCreatesOneRevisionRun()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        JiraAuthoringRunCreation?[] results = await Task.WhenAll(
            fixture.Coordinator.CreateScheduledRunAsync(),
            fixture.Coordinator.CreateScheduledRunAsync());

        Assert.Single(results, result => result is not null);
        Assert.Null(results.SingleOrDefault(result => result is null));
        using Microsoft.Data.Sqlite.SqliteConnection connection =
            fixture.SourceStoreConnection();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM authoring_run_items";
        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public async Task CreateRun_SupersedesStaleQueuedRunBeforeNewerRevision()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", revision);
        JiraAuthoringRunCreation first =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;

        await fixture.SeedAsync("FHIR-1", revision.AddDays(1), title: "Second");
        JiraAuthoringRunCreation second =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;
        Assert.Equal(AuthoringStatusValues.Runs.Queued, second.Run.Status);
        await CompleteDatabaseOnlyRunAsync(fixture, first);

        await fixture.SeedAsync("FHIR-1", revision.AddDays(2), title: "Third");
        JiraAuthoringRunCreation third =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;

        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            (await fixture.AuthoringStore.GetRunAsync(second.Run.Id))!.Status);
        Assert.Equal(
            AuthoringStatusValues.Runs.Running,
            (await fixture.AuthoringStore.GetRunAsync(third.Run.Id))!.Status);
        JiraAuthoringWorkItemStore queue = new(
            fixture.AuthoringStore,
            fixture.SourceStore,
            fixture.Coordinator);
        JiraAuthoringWorkItem current = Assert.Single(
            await queue.GetPendingAsync(10, CancellationToken.None));
        Assert.Equal(revision.AddDays(2).ToString("O"), current.RunItem.ExpectedSourceRevision);
    }

    [Fact]
    public async Task StaleBatchItemDoesNotStrandCurrentSibling()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", revision);
        await fixture.SeedAsync("FHIR-2", revision);
        JiraAuthoringRunCreation batch =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;

        await fixture.SeedAsync("FHIR-1", revision.AddDays(1), title: "Updated");
        JiraAuthoringWorkItemStore queue = new(
            fixture.AuthoringStore,
            fixture.SourceStore,
            fixture.Coordinator);
        JiraAuthoringWorkItem current = Assert.Single(
            await queue.GetPendingAsync(10, CancellationToken.None));
        AuthoringRunItemRecord[] batchItems =
            (await fixture.AuthoringStore.GetRunItemsAsync(batch.Run.Id)).ToArray();

        Assert.Equal("FHIR-2", current.SourceTicket.Key);
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            batchItems.Single(item => item.BusinessKey == "FHIR-1").Status);
        Assert.Equal(
            AuthoringStatusValues.Items.Pending,
            batchItems.Single(item => item.BusinessKey == "FHIR-2").Status);

        JiraAuthoringRunCreation replacement =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;
        Assert.Equal("FHIR-1", Assert.Single(replacement.Items).BusinessKey);
        Assert.Equal(AuthoringStatusValues.Runs.Queued, replacement.Run.Status);
    }

    private static async Task CompleteDatabaseOnlyRunAsync(
        JiraAuthoringTestFixture fixture,
        JiraAuthoringRunCreation creation)
    {
        AuthoringRunItemRecord item = Assert.Single(creation.Items);
        AuthoringOperationClaim claim = (await fixture.AuthoringStore.ClaimItemAsync(
            creation.Run.Id,
            item.Id))!;
        AuthoringReceiptAcceptance receipt = await fixture.AuthoringStore.AcceptResultAsync(
            new AuthoringResultSubmission(
                creation.Run.Id,
                item.Id,
                claim.OperationId,
                item.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            claim.OperationToken);
        await fixture.AuthoringStore.MarkItemCompleteAsync(item.Id, receipt.Receipt.ReceiptId);
        await fixture.AuthoringStore.MarkRunFinalizingAsync(creation.Run.Id);
        await fixture.AuthoringStore.CompleteRunAsync(creation.Run.Id, snapshotId: null);
    }
}

internal sealed class JiraAuthoringTestFixture : IDisposable
{
    private readonly string _directory;

    public JiraAuthoringTestFixture()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"fhir-augury-jira-authoring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "jira-processing.db");
        SourceStore = new JiraProcessingSourceTicketStore(
            DatabasePath,
            new ResolvedJiraProcessingFilters
            {
                TicketStatuses = ["Triaged"],
                SourceTicketShape = "fhir",
            });
        JiraProcessingDatabase database = new(
            DatabasePath,
            NullLogger<JiraProcessingDatabase>.Instance);
        database.Initialize();
        AuthoringStore = new AuthoringRunStore(database);
        Options = Microsoft.Extensions.Options.Options.Create(new JiraProcessingOptions
        {
            AgentCliCommand = "agent {ticketKey}",
            AuthoringAgentCliCommand = "agent {ticketKey}",
            JiraSourceAddress = "http://source",
            SourceTicketShape = "fhir",
            TicketStatusesToProcess = ["Triaged"],
        });
        Coordinator = new JiraAuthoringRunCoordinator(
            AuthoringStore,
            SourceStore,
            new JiraProcessingFilterResolver(),
            Options);
    }

    public string DatabasePath { get; }
    public JiraProcessingSourceTicketStore SourceStore { get; }
    public AuthoringRunStore AuthoringStore { get; }
    public IOptions<JiraProcessingOptions> Options { get; }
    public JiraAuthoringRunCoordinator Coordinator { get; }

    public Microsoft.Data.Sqlite.SqliteConnection SourceStoreConnection()
    {
        Microsoft.Data.Sqlite.SqliteConnection connection = new(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
        connection.Open();
        return connection;
    }

    public async Task ActivateAsync()
    {
        await AuthoringStore.EnsureProcessorModeAsync(Coordinator.ProcessorKind);
        AuthoringProcessorModeRecord mode =
            await AuthoringStore.GetProcessorModeAsync(Coordinator.ProcessorKind);
        if (mode.Mode == AuthoringStatusValues.ProcessorModes.Legacy)
        {
            await AuthoringStore.TransitionProcessorModeAsync(
                Coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver);
            await AuthoringStore.TransitionProcessorModeAsync(
                Coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked);
        }
    }

    public Task<JiraProcessingSourceTicketRecord> SeedAsync(
        string key,
        DateTimeOffset revision,
        string title = "Title")
        => SourceStore.UpsertAsync(
            new JiraIssueSummaryEntry
            {
                Key = key,
                ProjectKey = "FHIR",
                Title = title,
                Type = "Change Request",
                Status = "Triaged",
                WorkGroup = "FHIR-I",
                Specification = "FHIR Core",
                UpdatedAt = revision,
            },
            "fhir",
            resetProcessingStatus: false,
            CancellationToken.None);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
