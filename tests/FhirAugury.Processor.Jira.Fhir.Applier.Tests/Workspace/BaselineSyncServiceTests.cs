using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Processing;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Workspace;

public class BaselineSyncServiceTests : IDisposable
{
    private readonly string _workingDir = Path.Combine(Path.GetTempPath(), $"applier-ws-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"applier-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (Directory.Exists(_workingDir)) Directory.Delete(_workingDir, true);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private (BaselineSyncService Svc, RepoBaselineStore Baselines, RepoWorkspaceManager Manager, FakeGitProcessRunner Git, ApplierRepoOptions Repo, ApplierOptions Options) NewSut(
        string buildCommand,
        string baselineMinSyncAge = "00:00:00")
    {
        ApplierDatabase database = new(_dbPath, NullLogger<ApplierDatabase>.Instance);
        database.Initialize();
        RepoBaselineStore baselines = new(database);

        ApplierRepoOptions repo = new()
        {
            Owner = "HL7",
            Name = "fhir",
            BuildCommand = buildCommand,
            OutputRoots = ["output/**"],
        };
        ApplierOptions options = new()
        {
            WorkingDirectory = _workingDir,
            OutputDirectory = Path.Combine(_workingDir, "out"),
            PlannerDatabasePath = "ignored",
            BaselineSyncSchedule = "00:00:30",
            BaselineMinSyncAge = baselineMinSyncAge,
            BaselineRefreshOnStartup = true,
            Repos = { repo },
        };

        FakeGitProcessRunner git = new();
        git.Respond("rev-parse HEAD", new GitProcessResult(0, "abc123", string.Empty));
        BuildCommandRunner buildRunner = new(NullLogger<BuildCommandRunner>.Instance);
        RepoWorkspaceManager manager = new(
            git,
            buildRunner,
            baselines,
            Options.Create(options),
            Options.Create(new ApplierAuthOptions { Token = null, TokenEnvVar = null }),
            NullLogger<RepoWorkspaceManager>.Instance);

        RepoLockManager lockMgr = new();
        ProcessingLifecycleService lifecycle = new(Options.Create(new ProcessingServiceOptions { StartProcessingOnStartup = true }));
        BaselineSyncService svc = new(
            manager,
            lockMgr,
            baselines,
            lifecycle,
            Options.Create(options),
            NullLogger<BaselineSyncService>.Instance);

        return (svc, baselines, manager, git, repo, options);
    }

    [Fact]
    public async Task SyncOnce_RebuildsAllRepos()
    {
        var (svc, baselines, _, _, repo, _) = NewSut("/bin/sh -c \"mkdir -p output && echo hi > output/x.txt\"");
        string clonePath = Path.Combine(_workingDir, "clones", "HL7_fhir");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        int rebuilt = await svc.SyncOnceAsync(TimeSpan.Zero, default);

        Assert.Equal(1, rebuilt);
        Assert.NotNull(await baselines.GetAsync(repo.FullName, default));
    }

    [Fact]
    public async Task SyncOnce_SkipsWhenNewerThanMinAge()
    {
        var (svc, baselines, _, _, repo, _) = NewSut("/bin/sh -c \"mkdir -p output && echo hi > output/x.txt\"");
        string clonePath = Path.Combine(_workingDir, "clones", "HL7_fhir");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        await baselines.UpsertAsync(repo.FullName, "fresh", DateTimeOffset.UtcNow, default);

        int rebuilt = await svc.SyncOnceAsync(TimeSpan.FromHours(1), default);

        Assert.Equal(0, rebuilt);
        var stored = await baselines.GetAsync(repo.FullName, default);
        Assert.Equal("fresh", stored!.BaselineCommitSha);
    }

    [Fact]
    public async Task SyncOnce_RebuildsWhenOlderThanMinAge()
    {
        var (svc, baselines, _, _, repo, _) = NewSut("/bin/sh -c \"mkdir -p output && echo hi > output/x.txt\"");
        string clonePath = Path.Combine(_workingDir, "clones", "HL7_fhir");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        await baselines.UpsertAsync(repo.FullName, "stale", DateTimeOffset.UtcNow - TimeSpan.FromDays(1), default);

        int rebuilt = await svc.SyncOnceAsync(TimeSpan.FromHours(1), default);
        Assert.Equal(1, rebuilt);
        var stored = await baselines.GetAsync(repo.FullName, default);
        Assert.Equal("abc123", stored!.BaselineCommitSha);
    }

    [Fact]
    public async Task SyncOnce_SwallowsPerRepoFailures()
    {
        var (svc, _, _, _, _, options) = NewSut("/bin/sh -c \"exit 1\"");
        string clonePath = Path.Combine(_workingDir, "clones", "HL7_fhir");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        // Should not throw; failure is logged and the loop continues.
        int rebuilt = await svc.SyncOnceAsync(TimeSpan.Zero, default);
        Assert.Equal(0, rebuilt);
    }

    [Fact]
    public async Task SyncOnce_BlocksWhileRepoLockHeld()
    {
        var (_, baselines, manager, git, repo, options) = NewSut("/bin/sh -c \"mkdir -p output && echo hi > output/x.txt\"");
        string clonePath = Path.Combine(_workingDir, "clones", "HL7_fhir");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        RepoLockManager lockMgr = new();
        ProcessingLifecycleService lifecycle = new(Options.Create(new ProcessingServiceOptions { StartProcessingOnStartup = true }));
        BaselineSyncService svc = new(
            manager,
            lockMgr,
            baselines,
            lifecycle,
            Options.Create(options),
            NullLogger<BaselineSyncService>.Instance);

        IDisposable held = await lockMgr.AcquireAsync(repo.FullName, default);

        Task<int> sync = Task.Run(async () => await svc.SyncOnceAsync(TimeSpan.Zero, default));
        await Task.Delay(150);
        Assert.False(sync.IsCompleted, "sync should block while repo lock is held");

        held.Dispose();
        int rebuilt = await sync.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rebuilt);
    }
}
