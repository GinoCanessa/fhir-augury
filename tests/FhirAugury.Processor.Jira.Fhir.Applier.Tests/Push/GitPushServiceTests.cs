using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Push;
using FhirAugury.Processor.Jira.Fhir.Applier.Tests.Workspace;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Push;

public class GitPushServiceTests : IDisposable
{
    private readonly string _worktree = Path.Combine(Path.GetTempPath(), $"push-wt-{Guid.NewGuid():N}");

    public GitPushServiceTests()
    {
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        if (Directory.Exists(_worktree)) Directory.Delete(_worktree, recursive: true);
    }

    private (GitPushService Svc, FakeGitProcessRunner Git) NewSut(string? token = null, string remoteName = "origin")
    {
        ApplierAuthOptions auth = new() { Token = token, TokenEnvVar = null };
        ApplierOptions options = new()
        {
            WorkingDirectory = "/tmp",
            OutputDirectory = "/tmp",
            PlannerDatabasePath = "/tmp/x.db",
            Push = new ApplierPushOptions { RemoteName = remoteName },
        };
        FakeGitProcessRunner git = new();
        git.Respond("rev-parse HEAD", new GitProcessResult(0, "abc123\n", string.Empty));
        GitPushService svc = new(git, Options.Create(auth), Options.Create(options), NullLogger<GitPushService>.Instance);
        return (svc, git);
    }

    [Fact]
    public void ResolveRemoteTarget_BuildsAuthenticatedUrlWhenTokenAvailable()
    {
        var (svc, _) = NewSut(token: "ghp_secret");

        string target = svc.ResolveRemoteTarget("HL7/fhir");

        Assert.Equal("https://x-access-token:ghp_secret@github.com/HL7/fhir.git", target);
    }

    [Fact]
    public void ResolveRemoteTarget_FallsBackToRemoteNameWhenNoTokenResolves()
    {
        var (svc, _) = NewSut(token: null, remoteName: "origin");

        string target = svc.ResolveRemoteTarget("HL7/fhir");

        Assert.Equal("origin", target);
    }

    [Fact]
    public async Task PushAsync_RunsGitPushAndReturnsCommitSha()
    {
        var (svc, git) = NewSut();

        GitPushResult result = await svc.PushAsync(
            new GitPushRequest("HL7/fhir", "HL7", "fhir", "FHIR-1", _worktree),
            default);

        Assert.True(result.Success);
        Assert.Equal("abc123", result.PushedCommitSha);
        Assert.Null(result.ErrorMessage);
        Assert.Contains(git.Calls, c => c.Arguments.StartsWith("push ") && c.Arguments.Contains("FHIR-1:FHIR-1"));
    }

    [Fact]
    public async Task PushAsync_ReturnsFailureWhenWorktreeMissing()
    {
        var (svc, _) = NewSut();
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        GitPushResult result = await svc.PushAsync(
            new GitPushRequest("HL7/fhir", "HL7", "fhir", "FHIR-1", missing),
            default);

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.ErrorMessage);
    }

    [Fact]
    public async Task PushAsync_ReturnsFailureWhenGitPushFails()
    {
        var (svc, git) = NewSut();
        git.Respond("push", new GitProcessResult(1, string.Empty, "remote rejected"));

        GitPushResult result = await svc.PushAsync(
            new GitPushRequest("HL7/fhir", "HL7", "fhir", "FHIR-1", _worktree),
            default);

        Assert.False(result.Success);
        Assert.Contains("remote rejected", result.ErrorMessage);
    }

    [Fact]
    public async Task PushAsync_TokenNeverAppearsInResultErrorMessage()
    {
        var (svc, git) = NewSut(token: "ghp_secret");
        git.Respond("push", new GitProcessResult(1, string.Empty, "remote refused"));

        GitPushResult result = await svc.PushAsync(
            new GitPushRequest("HL7/fhir", "HL7", "fhir", "FHIR-1", _worktree),
            default);

        Assert.False(result.Success);
        Assert.DoesNotContain("ghp_secret", result.ErrorMessage);
    }
}
