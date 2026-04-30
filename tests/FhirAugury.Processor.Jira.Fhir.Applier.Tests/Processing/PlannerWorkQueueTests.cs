using System.IO;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Processing;
using FhirAugury.Processor.Jira.Fhir.Applier.Tests.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Processing;

public class PlannerWorkQueueTests : IDisposable
{
    private readonly string _plannerPath = PlannerFixture.CreateTempPath();
    private readonly string _applierPath = Path.Combine(Path.GetTempPath(), $"applier-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_plannerPath)) File.Delete(_plannerPath);
        if (File.Exists(_applierPath)) File.Delete(_applierPath);
    }

    private PlannerWorkQueue NewQueue(IReadOnlyCollection<string>? typeFilter = null)
    {
        PlannerReadOnlyDatabase planner = new(_plannerPath, NullLogger<PlannerReadOnlyDatabase>.Instance);
        AppliedTicketQueueItemStore store = new(_applierPath);
        ProcessingLifecycleService lifecycle = new(Options.Create(new ProcessingServiceOptions { StartProcessingOnStartup = true }));
        return new PlannerWorkQueue(
            planner,
            store,
            lifecycle,
            NullLogger<PlannerWorkQueue>.Instance,
            Options.Create(new ProcessingServiceOptions()),
            Options.Create(new JiraProcessingOptions
            {
                SourceTicketShape = "fhir",
                TicketTypesToProcess = typeFilter?.ToList(),
            }),
            Options.Create(new ApplierOptions { PlannerDatabasePath = _plannerPath }));
    }

    [Fact]
    public async Task PollOnce_InsertsFreshTickets()
    {
        PlannerFixture.CreateSchema(_plannerPath);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PlannerFixture.InsertJiraTicket(_plannerPath, "FHIR-1", "Change Request", "complete", "cid-1", now);
        PlannerFixture.InsertPlannedTicket(_plannerPath, "FHIR-1");
        PlannerFixture.InsertJiraTicket(_plannerPath, "FHIR-2", "Change Request", "complete", "cid-2", now);
        PlannerFixture.InsertPlannedTicket(_plannerPath, "FHIR-2");

        PlannerWorkQueue queue = NewQueue(["Change Request"]);
        PlannerWorkQueue.PollSummary summary = await queue.PollOnceAsync(default);

        Assert.Equal(2, summary.Inserted);
    }

    [Fact]
    public async Task PollOnce_NoOpsOnUnchangedRows()
    {
        PlannerFixture.CreateSchema(_plannerPath);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PlannerFixture.InsertJiraTicket(_plannerPath, "FHIR-1", "Change Request", "complete", "cid-1", now);
        PlannerFixture.InsertPlannedTicket(_plannerPath, "FHIR-1");

        PlannerWorkQueue queue = NewQueue(["Change Request"]);
        await queue.PollOnceAsync(default);
        PlannerWorkQueue.PollSummary second = await queue.PollOnceAsync(default);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.Unchanged);
    }

    [Fact]
    public async Task PollOnce_MarksStaleWhenPlannerCompletionIdChanges()
    {
        PlannerFixture.CreateSchema(_plannerPath);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PlannerFixture.InsertJiraTicket(_plannerPath, "FHIR-1", "Change Request", "complete", "cid-1", now);
        PlannerFixture.InsertPlannedTicket(_plannerPath, "FHIR-1");

        PlannerWorkQueue queue = NewQueue(["Change Request"]);
        await queue.PollOnceAsync(default);

        AppliedTicketQueueItemStore store = new(_applierPath);
        var row = (await store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.True(await store.ClaimItemAsync(row, now, default));
        await store.MarkCompleteAsync(row, now.AddMinutes(1), default);

        BumpPlannerCompletionId(_plannerPath, "FHIR-1", "cid-2");

        PlannerWorkQueue.PollSummary second = await queue.PollOnceAsync(default);
        Assert.Equal(1, second.MarkedStale);

        var refreshed = (await store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.Equal(ProcessingStatusValues.Stale, refreshed.ProcessingStatus);
    }

    [Fact]
    public async Task PollOnce_SkipsTicketsWithoutPlannedRow()
    {
        PlannerFixture.CreateSchema(_plannerPath);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PlannerFixture.InsertJiraTicket(_plannerPath, "FHIR-1", "Change Request", "complete", "cid-1", now);
        // no planned_tickets row inserted

        PlannerWorkQueue queue = NewQueue(["Change Request"]);
        PlannerWorkQueue.PollSummary summary = await queue.PollOnceAsync(default);
        Assert.Equal(0, summary.Inserted);
    }

    [Fact]
    public async Task PollOnce_AppliesTicketTypeFilter()
    {
        PlannerFixture.CreateSchema(_plannerPath);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PlannerFixture.InsertJiraTicket(_plannerPath, "FHIR-1", "Change Request", "complete", "cid-1", now);
        PlannerFixture.InsertPlannedTicket(_plannerPath, "FHIR-1");
        PlannerFixture.InsertJiraTicket(_plannerPath, "FHIR-2", "Question", "complete", "cid-2", now);
        PlannerFixture.InsertPlannedTicket(_plannerPath, "FHIR-2");

        PlannerWorkQueue queue = NewQueue(["Change Request"]);
        PlannerWorkQueue.PollSummary summary = await queue.PollOnceAsync(default);
        Assert.Equal(1, summary.Inserted);

        AppliedTicketQueueItemStore store = new(_applierPath);
        Assert.NotNull(await store.GetByKeyAsync("FHIR-1", "fhir", default));
        Assert.Null(await store.GetByKeyAsync("FHIR-2", "fhir", default));
    }

    private static void BumpPlannerCompletionId(string dbPath, string key, string newCompletionId)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE jira_processing_source_tickets SET CompletionId = @cid WHERE Key = @key";
        command.Parameters.AddWithValue("@cid", newCompletionId);
        command.Parameters.AddWithValue("@key", key);
        command.ExecuteNonQuery();
    }
}
