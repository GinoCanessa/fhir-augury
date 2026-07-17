using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Workspace;

public class GitWorktreeCommitServiceTests
{
    [Fact]
    public void RenderMessage_SubstitutesAllTokens()
    {
        CommitContext ctx = new(
            TicketKey: "FHIR-1",
            ApplyCompletionId: "apply-cid",
            PlannerCompletionId: "planner-cid",
            PlannerCompletedAt: DateTimeOffset.Parse("2026-04-29T00:00:00Z"),
            FailureKind: "AgentFailed",
            FailureSummary: "agent died");

        string template = "{ticketKey} {applyCompletionId} {plannerCompletionId} {plannerCompletedAt} {failureKind} {failureSummary}";
        string actual = GitWorktreeCommitService.RenderMessage(template, ctx);

        Assert.Equal("FHIR-1 apply-cid planner-cid 2026-04-29T00:00:00.0000000+00:00 AgentFailed agent died", actual);
    }

    [Fact]
    public void RenderMessage_NullsBecomeEmptyStrings()
    {
        CommitContext ctx = new(
            TicketKey: "FHIR-1",
            ApplyCompletionId: "apply-cid",
            PlannerCompletionId: "planner-cid",
            PlannerCompletedAt: null,
            FailureKind: null,
            FailureSummary: null);

        string actual = GitWorktreeCommitService.RenderMessage("[{plannerCompletedAt}][{failureKind}][{failureSummary}]", ctx);
        Assert.Equal("[][][]", actual);
    }

    [Fact]
    public async Task CommitAsync_RunsAddCommitRevParse_ReturnsSha()
    {
        ApplierOptions options = new()
        {
            WorkingDirectory = "/tmp",
            OutputDirectory = "/tmp",
            PlannerDatabasePath = "/tmp/x.db",
            Commit = new ApplierCommitOptions
            {
                AuthorName = "T",
                AuthorEmail = "t@example.com",
                MessageTemplate = "OK {ticketKey}",
                FailureMessageTemplate = "FAIL {ticketKey} {failureSummary}",
            },
        };
        FakeGitProcessRunner git = new();
        git.Respond("rev-parse HEAD", new GitProcessResult(0, "deadbeef\n", string.Empty));
        GitWorktreeCommitService svc = new(git, Options.Create(options), NullLogger<GitWorktreeCommitService>.Instance);

        CommitContext ctx = new("FHIR-1", "apply", "planner", null, null, null);
        CommitResult result = await svc.CommitAsync("/tmp/wt", ctx, success: true, default);

        Assert.Equal("deadbeef", result.CommitSha);
        Assert.Equal("OK FHIR-1", result.RenderedMessage);
        Assert.Contains(git.Calls, c => c.Arguments == "add -A");
        Assert.Contains(git.Calls, c => c.Arguments.Contains("commit --allow-empty"));
        Assert.Contains(git.Calls, c => c.Arguments == "rev-parse HEAD");
    }

    [Fact]
    public async Task CommitAsync_ThrowsWhenAddFails()
    {
        ApplierOptions options = new()
        {
            WorkingDirectory = "/tmp", OutputDirectory = "/tmp", PlannerDatabasePath = "/tmp/x.db",
        };
        FakeGitProcessRunner git = new();
        git.Respond("add -A", new GitProcessResult(1, string.Empty, "no repo"));
        GitWorktreeCommitService svc = new(git, Options.Create(options), NullLogger<GitWorktreeCommitService>.Instance);

        CommitContext ctx = new("FHIR-1", "apply", "planner", null, null, null);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await svc.CommitAsync("/tmp/wt", ctx, success: true, default));
        Assert.Contains("git add -A failed", ex.Message);
    }

    [Fact]
    public async Task CommitAsync_FailureTemplateUsedWhenSuccessFalse()
    {
        ApplierOptions options = new()
        {
            WorkingDirectory = "/tmp", OutputDirectory = "/tmp", PlannerDatabasePath = "/tmp/x.db",
            Commit = new ApplierCommitOptions
            {
                MessageTemplate = "OK {ticketKey}",
                FailureMessageTemplate = "FAIL {ticketKey}",
            },
        };
        FakeGitProcessRunner git = new();
        git.Respond("rev-parse HEAD", new GitProcessResult(0, "abc", string.Empty));
        GitWorktreeCommitService svc = new(git, Options.Create(options), NullLogger<GitWorktreeCommitService>.Instance);

        CommitContext ctx = new("FHIR-1", "apply", "planner", null, "Build", "boom");
        CommitResult r = await svc.CommitAsync("/tmp/wt", ctx, success: false, default);
        Assert.Equal("FAIL FHIR-1", r.RenderedMessage);
    }
}
