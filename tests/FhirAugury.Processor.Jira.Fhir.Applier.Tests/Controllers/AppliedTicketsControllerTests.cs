using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Controllers;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Applier.Processing;
using FhirAugury.Processor.Jira.Fhir.Applier.Push;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Controllers;

internal sealed class FakeGitPushService : IGitPushService
{
    public Func<GitPushRequest, GitPushResult> Behaviour { get; set; } =
        _ => new GitPushResult(true, "pushedsha", null);

    public List<GitPushRequest> Calls { get; } = [];

    public Task<GitPushResult> PushAsync(GitPushRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        return Task.FromResult(Behaviour(request));
    }
}

public class AppliedTicketsControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"controller-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly ApplierDatabase _database;
    private readonly AppliedTicketWriteStore _writeStore;
    private readonly RepoLockManager _lockManager = new();

    public AppliedTicketsControllerTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "applier.db");
        _database = new ApplierDatabase(_dbPath, NullLogger<ApplierDatabase>.Instance);
        _database.Initialize();
        _writeStore = new AppliedTicketWriteStore(_database);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AppliedTicketsController NewController(IGitPushService push, IEnumerable<string>? configuredRepoFullNames = null)
    {
        ApplierOptions options = new()
        {
            WorkingDirectory = Path.Combine(_root, "ws"),
            OutputDirectory = Path.Combine(_root, "out"),
            PlannerDatabasePath = "ignored",
            Repos = (configuredRepoFullNames ?? ["HL7/fhir"]).Select(fn =>
            {
                string[] p = fn.Split('/');
                return new ApplierRepoOptions { Owner = p[0], Name = p[1], BuildCommand = "/usr/bin/true", OutputRoots = ["output/**"] };
            }).ToList(),
        };
        return new AppliedTicketsController(_writeStore, push, _lockManager, Options.Create(options));
    }

    private async Task SeedAggregateAsync(string ticketKey)
    {
        await _writeStore.UpsertAppliedTicketAsync(new AppliedTicketRecord
        {
            Key = ticketKey,
            PlannerCompletionId = "planner-cid",
            ApplyCompletionId = "apply-cid",
            Outcome = "Success",
        }, default);
    }

    private async Task<string> SeedRepoRowAsync(string ticketKey, string repoFullName, string outcome, string? commitSha)
    {
        AppliedTicketRepoRecord row = new()
        {
            IssueKey = ticketKey,
            RepoKey = repoFullName,
            BaselineCommitSha = "baseline",
            BranchName = ticketKey,
            CommitSha = commitSha,
            Outcome = outcome,
        };
        await _writeStore.InsertAppliedTicketRepoAsync(row, default);
        return row.Id;
    }

    [Fact]
    public async Task Push_Returns404WhenNoAppliedTicket()
    {
        AppliedTicketsController controller = NewController(new FakeGitPushService());

        ActionResult<AppliedTicketPushResponse> result = await controller.PushAppliedTicket("FHIR-404", default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Push_Returns409WhenNoSuccessfulCommit()
    {
        await SeedAggregateAsync("FHIR-1");
        await SeedRepoRowAsync("FHIR-1", "HL7/fhir", ApplyOutcomes.AgentFailed, commitSha: null);
        AppliedTicketsController controller = NewController(new FakeGitPushService());

        ActionResult<AppliedTicketPushResponse> result = await controller.PushAppliedTicket("FHIR-1", default);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Push_HappyPath_PushesEachSuccessfulRepoAndUpdatesPushState()
    {
        await SeedAggregateAsync("FHIR-2");
        string okRowId = await SeedRepoRowAsync("FHIR-2", "HL7/fhir", ApplyOutcomes.Success, commitSha: "abc");
        FakeGitPushService push = new();
        AppliedTicketsController controller = NewController(push);

        ActionResult<AppliedTicketPushResponse> result = await controller.PushAppliedTicket("FHIR-2", default);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        AppliedTicketPushResponse response = Assert.IsType<AppliedTicketPushResponse>(ok.Value);
        Assert.Equal(1, response.RepoCount);
        Assert.Equal(1, response.PushedCount);
        Assert.Equal(0, response.FailedCount);
        Assert.Equal(0, response.SkippedCount);

        AppliedTicketRepoRecord refreshed = (await _writeStore.ListAppliedTicketReposAsync("FHIR-2", default)).Single(r => r.Id == okRowId);
        Assert.Equal(PushStates.Pushed, refreshed.PushState);
        Assert.Equal("pushedsha", refreshed.PushedCommitSha);
        Assert.NotNull(refreshed.PushedAt);
    }

    [Fact]
    public async Task Push_PushFailureFlagsRowAsPushFailed()
    {
        await SeedAggregateAsync("FHIR-3");
        string rowId = await SeedRepoRowAsync("FHIR-3", "HL7/fhir", ApplyOutcomes.Success, commitSha: "abc");
        FakeGitPushService push = new() { Behaviour = _ => new GitPushResult(false, null, "remote refused") };
        AppliedTicketsController controller = NewController(push);

        ActionResult<AppliedTicketPushResponse> result = await controller.PushAppliedTicket("FHIR-3", default);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        AppliedTicketPushResponse response = Assert.IsType<AppliedTicketPushResponse>(ok.Value);
        Assert.Equal(0, response.PushedCount);
        Assert.Equal(1, response.FailedCount);

        AppliedTicketRepoRecord refreshed = (await _writeStore.ListAppliedTicketReposAsync("FHIR-3", default)).Single(r => r.Id == rowId);
        Assert.Equal(PushStates.PushFailed, refreshed.PushState);
        Assert.Null(refreshed.PushedCommitSha);
    }

    [Fact]
    public async Task Push_SkipsFailedReposAndPushesOnlySuccessful()
    {
        await SeedAggregateAsync("FHIR-4");
        string okId = await SeedRepoRowAsync("FHIR-4", "HL7/fhir", ApplyOutcomes.Success, commitSha: "ok-sha");
        await SeedRepoRowAsync("FHIR-4", "HL7/fhir-other", ApplyOutcomes.BuildFailed, commitSha: "fail-sha");
        FakeGitPushService push = new();
        AppliedTicketsController controller = NewController(push, configuredRepoFullNames: ["HL7/fhir", "HL7/fhir-other"]);

        ActionResult<AppliedTicketPushResponse> result = await controller.PushAppliedTicket("FHIR-4", default);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        AppliedTicketPushResponse response = Assert.IsType<AppliedTicketPushResponse>(ok.Value);
        Assert.Equal(2, response.RepoCount);
        Assert.Equal(1, response.PushedCount);
        Assert.Equal(0, response.FailedCount);
        Assert.Equal(1, response.SkippedCount);
        Assert.Single(push.Calls);
        Assert.Equal("HL7/fhir", push.Calls[0].RepoFullName);
    }
}
