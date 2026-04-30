using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Applier.Processing;
using FhirAugury.Processor.Jira.Fhir.Applier.Tests.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Tests.Workspace;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Processing;

/// <summary>
/// In-memory <see cref="IJiraAgentCliRunner"/>: invokes a side-effect callback (typically
/// to write files into the worktree) and returns a configurable result.
/// </summary>
internal sealed class FakeAgentRunner : IJiraAgentCliRunner
{
    public Func<JiraAgentCommandContext, JiraAgentResult> Behaviour { get; set; } =
        _ => new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.Zero, false);

    public List<JiraAgentCommandContext> Calls { get; } = [];

    public Task<JiraAgentResult> RunAsync(JiraAgentCommand command, JiraAgentCommandContext context, CancellationToken ct)
    {
        Calls.Add(context);
        return Task.FromResult(Behaviour(context));
    }
}

public class ApplierTicketHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"applier-handler-{Guid.NewGuid():N}");
    private readonly string _applierDb;
    private readonly string _plannerDb;

    public ApplierTicketHandlerTests()
    {
        Directory.CreateDirectory(_root);
        _applierDb = Path.Combine(_root, "applier.db");
        _plannerDb = Path.Combine(_root, "planner.db");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record SutBundle(
        ApplierTicketHandler Handler,
        AppliedTicketQueueItemStore QueueStore,
        AppliedTicketWriteStore WriteStore,
        FakeAgentRunner Agent,
        FakeGitProcessRunner Git,
        ApplierRepoOptions Repo,
        string WorkingDir);

    private SutBundle NewSut(
        string ticketKey,
        IEnumerable<string>? configuredRepoFullNames = null,
        string buildCommand = "/usr/bin/true")
    {
        ApplierDatabase database = new(_applierDb, NullLogger<ApplierDatabase>.Instance);
        database.Initialize();

        ApplierRepoOptions repo = new()
        {
            Owner = "HL7",
            Name = "fhir",
            BuildCommand = buildCommand,
            OutputRoots = ["output/**"],
            PrimaryBranch = "master",
        };
        List<ApplierRepoOptions> reposList = (configuredRepoFullNames ?? ["HL7/fhir"])
            .Select(fullName =>
            {
                string[] parts = fullName.Split('/');
                return new ApplierRepoOptions
                {
                    Owner = parts[0],
                    Name = parts[1],
                    BuildCommand = buildCommand,
                    OutputRoots = ["output/**"],
                    PrimaryBranch = "master",
                };
            })
            .ToList();

        string workingDir = Path.Combine(_root, "ws");
        ApplierOptions options = new()
        {
            WorkingDirectory = workingDir,
            OutputDirectory = Path.Combine(_root, "out"),
            PlannerDatabasePath = _plannerDb,
            Repos = reposList,
        };
        IOptions<ApplierOptions> applierOpts = Options.Create(options);

        FakeGitProcessRunner git = new();
        // git rev-parse HEAD is needed for commit and possibly EnsureClone (we won't call EnsureClone here)
        git.Respond("rev-parse HEAD", new GitProcessResult(0, "deadbeef\n", string.Empty));

        BuildCommandRunner buildRunner = new(NullLogger<BuildCommandRunner>.Instance);
        RepoBaselineStore baselineStore = new(database);
        RepoWorkspaceManager workspaceManager = new(
            git,
            buildRunner,
            baselineStore,
            applierOpts,
            Options.Create(new ApplierAuthOptions { Token = null, TokenEnvVar = null }),
            NullLogger<RepoWorkspaceManager>.Instance);

        OutputDiffer outputDiffer = new(applierOpts, NullLogger<OutputDiffer>.Instance);
        GitWorktreeCommitService commitService = new(git, applierOpts, NullLogger<GitWorktreeCommitService>.Instance);

        // Insert baselines for each configured repo & create the baseline directory
        foreach (ApplierRepoOptions r in reposList)
        {
            baselineStore.UpsertAsync(r.FullName, "baseline-sha", DateTimeOffset.UtcNow, default).GetAwaiter().GetResult();
            string baselineDir = RepoWorkspaceLayout.BaselinePath(workingDir, r.Owner, r.Name);
            Directory.CreateDirectory(Path.Combine(baselineDir, "output"));
        }

        AppliedTicketQueueItemStore queueStore = new(_applierDb);
        AppliedTicketWriteStore writeStore = new(database);
        RepoLockManager lockManager = new();

        JiraProcessingOptions jiraOpts = new()
        {
            AgentCliCommand = "echo {ticketKey}",
            JiraSourceAddress = "http://x",
            SourceTicketShape = "fhir",
        };
        JiraAgentCommandRenderer renderer = new(Options.Create(jiraOpts));

        FakeAgentRunner agent = new();

        ApplierTicketHandler handler = new(
            new PlannerReadOnlyDatabase(_plannerDb, NullLogger<PlannerReadOnlyDatabase>.Instance),
            queueStore,
            writeStore,
            lockManager,
            workspaceManager,
            buildRunner,
            outputDiffer,
            commitService,
            renderer,
            agent,
            applierOpts,
            Options.Create(jiraOpts),
            Options.Create(new ProcessingServiceOptions { DatabasePath = _applierDb }),
            NullLogger<ApplierTicketHandler>.Instance);

        // Insert queue item via planner-discovery upsert.
        queueStore.UpsertFromPlannerAsync(ticketKey, "fhir", "planner-cid-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, default)
            .GetAwaiter().GetResult();

        return new SutBundle(handler, queueStore, writeStore, agent, git, repo, workingDir);
    }

    private void SeedPlanner(string ticketKey, IEnumerable<(string RepoKey, string FilePath)> changes)
    {
        PlannerFixture.CreateSchema(_plannerDb);
        PlannerFixture.InsertJiraTicket(_plannerDb, ticketKey, "Change Request", "complete", "planner-cid-1", DateTimeOffset.UtcNow);
        PlannerFixture.InsertPlannedTicket(_plannerDb, ticketKey);
        foreach ((string repoKey, IEnumerable<(string RepoKey, string FilePath)> grp) in changes
            .GroupBy(c => c.RepoKey)
            .Select(g => (g.Key, (IEnumerable<(string, string)>)g)))
        {
            string repoId = PlannerFixture.InsertPlannedRepo(_plannerDb, ticketKey, repoKey);
            int seq = 1;
            foreach ((_, string filePath) in grp)
            {
                PlannerFixture.InsertPlannedRepoChange(_plannerDb, ticketKey, repoId, repoKey, seq++, filePath);
            }
        }
    }

    [Fact]
    public async Task HappyPath_PersistsAggregateSuccess_AndCommitsWorktree()
    {
        SutBundle sut = NewSut("FHIR-1");
        SeedPlanner("FHIR-1", [("HL7/fhir", "output/added.txt")]);

        sut.Agent.Behaviour = ctx =>
        {
            // Agent writes a file into the worktree under output/.
            string outDir = Path.Combine(ctx.ExtensionTokens["worktreePath"], "output");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "added.txt"), "agent-output");
            return new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.Zero, false);
        };

        AppliedTicketQueueItemRecord item = await GetQueueItem("FHIR-1");
        await sut.Handler.ProcessAsync(item, default);

        AppliedTicketRecord? aggregate = await sut.WriteStore.GetAppliedTicketAsync("FHIR-1", default);
        Assert.NotNull(aggregate);
        Assert.Equal("Success", aggregate!.Outcome);
        Assert.Null(aggregate.ErrorSummary);

        Assert.Equal(1, await sut.WriteStore.CountAppliedTicketReposAsync("FHIR-1", default));
        Assert.Equal(1, await sut.WriteStore.CountAppliedOutputFilesAsync("FHIR-1", default));

        Assert.Single(sut.Agent.Calls);
        Assert.Equal("FHIR-1", sut.Agent.Calls[0].TicketKey);

        // queue item outcome reflected
        AppliedTicketQueueItemRecord refreshed = await GetQueueItem("FHIR-1");
        Assert.Equal("Success", refreshed.Outcome);
        Assert.Null(refreshed.ErrorSummary);
    }

    [Fact]
    public async Task AgentFailure_PersistsAggregateFailed_QueueHandlerReturnsNormally()
    {
        SutBundle sut = NewSut("FHIR-2");
        SeedPlanner("FHIR-2", [("HL7/fhir", "output/x.txt")]);

        sut.Agent.Behaviour = _ => new JiraAgentResult(1, string.Empty, "agent boom", TimeSpan.Zero, false);

        AppliedTicketQueueItemRecord item = await GetQueueItem("FHIR-2");
        await sut.Handler.ProcessAsync(item, default); // must NOT throw

        AppliedTicketRecord? aggregate = await sut.WriteStore.GetAppliedTicketAsync("FHIR-2", default);
        Assert.NotNull(aggregate);
        Assert.Equal("Failed", aggregate!.Outcome);
        Assert.Contains("AgentFailed", aggregate.ErrorSummary);

        AppliedTicketQueueItemRecord refreshed = await GetQueueItem("FHIR-2");
        Assert.Equal("Failed", refreshed.Outcome);
    }

    [Fact]
    public async Task BuildFailure_PersistsAggregateFailed_WithBuildFailedOutcome()
    {
        SutBundle sut = NewSut("FHIR-3", buildCommand: "/bin/sh -c \"exit 1\"");
        SeedPlanner("FHIR-3", [("HL7/fhir", "output/x.txt")]);

        sut.Agent.Behaviour = _ => new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.Zero, false);

        AppliedTicketQueueItemRecord item = await GetQueueItem("FHIR-3");
        await sut.Handler.ProcessAsync(item, default);

        AppliedTicketRecord? aggregate = await sut.WriteStore.GetAppliedTicketAsync("FHIR-3", default);
        Assert.Equal("Failed", aggregate!.Outcome);
        Assert.Contains("BuildFailed", aggregate.ErrorSummary);
    }

    [Fact]
    public async Task RepoNotConfigured_RecordsRepoNotConfiguredOutcome()
    {
        // Plan has a repo that isn't in Processing:Applier:Repos.
        SutBundle sut = NewSut("FHIR-4", configuredRepoFullNames: []);
        SeedPlanner("FHIR-4", [("HL7/fhir-notconfigured", "output/x.txt")]);

        AppliedTicketQueueItemRecord item = await GetQueueItem("FHIR-4");
        await sut.Handler.ProcessAsync(item, default);

        AppliedTicketRecord? aggregate = await sut.WriteStore.GetAppliedTicketAsync("FHIR-4", default);
        Assert.Equal("Failed", aggregate!.Outcome);
        Assert.Contains("not configured", aggregate.ErrorSummary);
        // applied_ticket_repos still gets a row with the RepoNotConfigured outcome
        Assert.Equal(1, await sut.WriteStore.CountAppliedTicketReposAsync("FHIR-4", default));
        // No agent call because we never got past the configuration check.
        Assert.Empty(sut.Agent.Calls);
    }

    [Fact]
    public async Task NoPlannedRepos_PersistsAggregateSuccess_NoApply()
    {
        SutBundle sut = NewSut("FHIR-5");
        // Schema present but no planned repos.
        PlannerFixture.CreateSchema(_plannerDb);
        PlannerFixture.InsertJiraTicket(_plannerDb, "FHIR-5", "Change Request", "complete", "planner-cid-1", DateTimeOffset.UtcNow);
        PlannerFixture.InsertPlannedTicket(_plannerDb, "FHIR-5");

        AppliedTicketQueueItemRecord item = await GetQueueItem("FHIR-5");
        await sut.Handler.ProcessAsync(item, default);

        AppliedTicketRecord? aggregate = await sut.WriteStore.GetAppliedTicketAsync("FHIR-5", default);
        Assert.Equal("Success", aggregate!.Outcome);
        Assert.Equal(0, await sut.WriteStore.CountAppliedTicketReposAsync("FHIR-5", default));
        Assert.Empty(sut.Agent.Calls);
    }

    [Fact]
    public async Task ReApply_ReplacesPriorAppliedRows()
    {
        SutBundle sut = NewSut("FHIR-6");
        SeedPlanner("FHIR-6", [("HL7/fhir", "output/added.txt")]);

        sut.Agent.Behaviour = ctx =>
        {
            string outDir = Path.Combine(ctx.ExtensionTokens["worktreePath"], "output");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "added.txt"), "v1");
            return new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.Zero, false);
        };

        AppliedTicketQueueItemRecord item = await GetQueueItem("FHIR-6");
        await sut.Handler.ProcessAsync(item, default);
        Assert.Equal(1, await sut.WriteStore.CountAppliedTicketReposAsync("FHIR-6", default));

        // Second pass also produces only one applied row (cleared first).
        sut.Agent.Behaviour = ctx =>
        {
            string outDir = Path.Combine(ctx.ExtensionTokens["worktreePath"], "output");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "added.txt"), "v2");
            return new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.Zero, false);
        };
        await sut.Handler.ProcessAsync(item, default);
        Assert.Equal(1, await sut.WriteStore.CountAppliedTicketReposAsync("FHIR-6", default));
    }

    private async Task<AppliedTicketQueueItemRecord> GetQueueItem(string ticketKey)
    {
        AppliedTicketQueueItemStore store = new(_applierDb);
        AppliedTicketQueueItemRecord? item = await store.GetByKeyAsync(ticketKey, "fhir", default);
        return item ?? throw new InvalidOperationException($"Queue item {ticketKey} not found.");
    }
}
