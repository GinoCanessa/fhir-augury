using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Workspace;

public class RepoWorkspaceManagerTests : IDisposable
{
    private readonly string _workingDir = Path.Combine(Path.GetTempPath(), $"applier-ws-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"applier-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (Directory.Exists(_workingDir)) Directory.Delete(_workingDir, true);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private (RepoWorkspaceManager Manager, FakeGitProcessRunner Git, RepoBaselineStore Baselines, ApplierRepoOptions Repo) NewSut(
        string buildCommand)
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
            Repos = { repo },
        };

        FakeGitProcessRunner git = new();
        BuildCommandRunner buildRunner = new(NullLogger<BuildCommandRunner>.Instance);
        RepoWorkspaceManager manager = new(
            git,
            buildRunner,
            baselines,
            Options.Create(options),
            Options.Create(new ApplierAuthOptions { Token = null, TokenEnvVar = null }),
            NullLogger<RepoWorkspaceManager>.Instance);

        return (manager, git, baselines, repo);
    }

    [Fact]
    public async Task EnsureCloneAsync_ClonesWhenMissing()
    {
        var (manager, git, _, repo) = NewSut("/bin/true");
        string clonePath = await manager.EnsureCloneAsync(repo, default);
        Assert.True(Directory.Exists(clonePath));
        Assert.Contains(git.Calls, c => c.Arguments.StartsWith("clone --branch main"));
    }

    [Fact]
    public async Task EnsureCloneAsync_FetchAndResetWhenPresent()
    {
        var (manager, git, _, repo) = NewSut("/bin/true");
        string clonePath = manager.PrimaryClonePath(repo);
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        await manager.EnsureCloneAsync(repo, default);

        Assert.Contains(git.Calls, c => c.Arguments == "fetch --all --prune");
        Assert.Contains(git.Calls, c => c.Arguments == "checkout main");
        Assert.Contains(git.Calls, c => c.Arguments == "reset --hard origin/main");
    }

    [Fact]
    public async Task RebuildBaselineAsync_SnapshotsOnlyOutputRoots()
    {
        // shell command writes to output/ but also writes a sibling source file the
        // baseline must NOT include
        string buildCmd = "/bin/sh -c \"mkdir -p output && echo hi > output/sample.txt && echo other > sibling.txt\"";
        var (manager, git, baselines, repo) = NewSut(buildCmd);

        string clonePath = manager.PrimaryClonePath(repo);
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));
        git.Respond("rev-parse HEAD", new GitProcessResult(0, "abc123\n", string.Empty));

        BaselineRebuildResult result = await manager.RebuildBaselineAsync(repo, default);

        Assert.Equal("abc123", result.CommitSha);
        Assert.Equal(1, result.FilesSnapshotted);

        string baseline = manager.BaselinePath(repo);
        Assert.True(File.Exists(Path.Combine(baseline, "output", "sample.txt")));
        Assert.False(File.Exists(Path.Combine(baseline, "sibling.txt")));

        RepoBaselineRecord? row = await baselines.GetAsync(repo.FullName, default);
        Assert.NotNull(row);
        Assert.Equal("abc123", row!.BaselineCommitSha);
    }

    [Fact]
    public async Task RebuildBaselineAsync_WipesPriorBaseline()
    {
        string buildCmd = "/bin/sh -c \"mkdir -p output && echo new > output/new.txt\"";
        var (manager, git, _, repo) = NewSut(buildCmd);

        string clonePath = manager.PrimaryClonePath(repo);
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));
        git.Respond("rev-parse HEAD", new GitProcessResult(0, "abc123", string.Empty));

        string baseline = manager.BaselinePath(repo);
        Directory.CreateDirectory(baseline);
        File.WriteAllText(Path.Combine(baseline, "stale.txt"), "old");

        await manager.RebuildBaselineAsync(repo, default);

        Assert.False(File.Exists(Path.Combine(baseline, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(baseline, "output", "new.txt")));
    }

    [Fact]
    public async Task RebuildBaselineAsync_PropagatesBuildFailure()
    {
        var (manager, git, _, repo) = NewSut("/bin/sh -c \"echo nope >&2 && exit 2\"");
        string clonePath = manager.PrimaryClonePath(repo);
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));
        git.Respond("rev-parse HEAD", new GitProcessResult(0, "abc", string.Empty));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await manager.RebuildBaselineAsync(repo, default));
        Assert.Contains("Baseline build failed", ex.Message);
    }

    [Fact]
    public async Task EnsureWorktreeAsync_RequiresPriorBaseline()
    {
        var (manager, _, _, repo) = NewSut("/bin/true");
        string clonePath = manager.PrimaryClonePath(repo);
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await manager.EnsureWorktreeAsync(repo, "FHIR-1", default));
        Assert.Contains("No baseline recorded", ex.Message);
    }

    [Fact]
    public async Task EnsureWorktreeAsync_CleansStaleDirAndCopiesBaseline()
    {
        var (manager, git, baselines, repo) = NewSut("/bin/true");
        string clonePath = manager.PrimaryClonePath(repo);
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        string baseline = manager.BaselinePath(repo);
        Directory.CreateDirectory(Path.Combine(baseline, "output"));
        File.WriteAllText(Path.Combine(baseline, "output", "snap.txt"), "snap");
        await baselines.UpsertAsync(repo.FullName, "deadbeef", DateTimeOffset.UtcNow, default);

        // Stale leftover worktree dir
        string worktree = manager.WorktreePath(repo, "FHIR-1");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, "leftover.txt"), "old");

        // git worktree add does NOT actually create the dir for the test, so simulate
        // it being created by the worktree-add call.
        git.Respond("worktree add", () =>
        {
            Directory.CreateDirectory(worktree);
            return new GitProcessResult(0, string.Empty, string.Empty);
        });
        // Worktree-remove is also fine to leave defaulting to success; we manually
        // delete the dir so the manager's "if Directory.Exists" guard runs.
        git.Respond("worktree remove", () =>
        {
            if (Directory.Exists(worktree)) Directory.Delete(worktree, true);
            return new GitProcessResult(0, string.Empty, string.Empty);
        });

        string sha = await manager.EnsureWorktreeAsync(repo, "FHIR-1", default);

        Assert.Equal("deadbeef", sha);
        Assert.True(File.Exists(Path.Combine(worktree, "output", "snap.txt")));
        Assert.False(File.Exists(Path.Combine(worktree, "leftover.txt")));
        Assert.Contains(git.Calls, c => c.Arguments.StartsWith("worktree remove --force"));
        Assert.Contains(git.Calls, c => c.Arguments.StartsWith("worktree add -B FHIR-1"));
    }
}
