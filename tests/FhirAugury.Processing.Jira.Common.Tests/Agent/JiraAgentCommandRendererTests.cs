using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Agent;

public class JiraAgentCommandRendererTests
{
    [Fact]
    public void Render_ReplacesTicketKeyAndDbPath()
    {
        JiraAgentCommand command = Renderer("copilot run --ticket {ticketKey} --db {dbPath}").Render(Context());
        Assert.Equal("copilot", command.FileName);
        Assert.Equal(["run", "--ticket", "FHIR-1", "--db", "db.sqlite"], command.Arguments);
    }

    [Fact]
    public void Render_SupportsExtensionTokens()
    {
        JiraAgentCommand command = Renderer("agent {repoFilters} {ticketKey}").Render(Context(new Dictionary<string, string> { ["repoFilters"] = "--repo HL7/fhir" }));
        Assert.Equal(["--repo", "HL7/fhir", "FHIR-1"], command.Arguments);
    }

    [Fact]
    public void Render_ThrowsForMissingTicketKeyToken()
    {
        Assert.Throws<InvalidOperationException>(() => Renderer("agent --db {dbPath}").Render(Context()));
    }

    [Fact]
    public void Render_ThrowsForUnresolvedToken()
    {
        Assert.Throws<InvalidOperationException>(() => Renderer("agent {ticketKey} {missing}").Render(Context()));
    }

    [Fact]
    public void Render_PreservesQuotedArguments()
    {
        JiraAgentCommand command = Renderer("agent --message \"hello world\" {ticketKey}").Render(Context());
        Assert.Equal(["--message", "hello world", "FHIR-1"], command.Arguments);
    }

    [Fact]
    public void Render_WorkerContextKeepsOperationTokenOffArgvAndToString()
    {
        JiraAgentCommandContext context = WorkerContext();

        JiraAgentCommand command = Renderer(
            "legacy {ticketKey} --db {dbPath}",
            "authoring {ticketKey}").Render(context);

        Assert.Equal("authoring", command.FileName);
        Assert.DoesNotContain(context.OperationToken!, command.Arguments);
        Assert.DoesNotContain(context.DatabasePath, command.Arguments);
        Assert.DoesNotContain(context.OperationToken!, context.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", context.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WorkerRejectsDatabaseTokenInAuthoringTemplate()
    {
        Assert.Throws<InvalidOperationException>(() => Renderer(
            "legacy {ticketKey} --db {dbPath}",
            "authoring {ticketKey} --db {DBPATH}").Render(WorkerContext()));
    }

    [Fact]
    public void Render_RejectsPartialWorkerContext()
    {
        JiraAgentCommandContext context = Context() with
        {
            IsAuthoringWorker = true,
            RunId = "run-1",
        };

        Assert.Throws<InvalidOperationException>(() => Renderer("agent {ticketKey}").Render(context));
    }

    [Fact]
    public async Task CliRunner_RedactsOperationTokenFromCapturedOutput()
    {
        JiraAgentCommand command = OperatingSystem.IsWindows()
            ? new JiraAgentCommand(
                "powershell",
                [
                    "-NoProfile",
                    "-Command",
                    "[Console]::Out.Write($env:FHIR_AUGURY_AUTHORING_OPERATION_TOKEN); [Console]::Error.Write($env:FHIR_AUGURY_AUTHORING_OPERATION_TOKEN)",
                ])
            : new JiraAgentCommand(
                "/bin/sh",
                ["-c", "printf %s \"$FHIR_AUGURY_AUTHORING_OPERATION_TOKEN\"; printf %s \"$FHIR_AUGURY_AUTHORING_OPERATION_TOKEN\" >&2"]);
        JiraAgentCommandContext context = WorkerContext();

        JiraAgentResult result = await new JiraAgentCliRunner().RunAsync(
            command,
            context,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[REDACTED]", result.StdoutTail);
        Assert.Equal("[REDACTED]", result.StderrTail);
    }

    [Fact]
    public async Task CliRunner_RemovesProcessingDatabaseFromWorkerEnvironment()
    {
        JiraAgentCommand command = OperatingSystem.IsWindows()
            ? new JiraAgentCommand(
                "powershell",
                [
                    "-NoProfile",
                    "-Command",
                    "if ($null -eq $env:FHIR_AUGURY_PROCESSING_DB) { [Console]::Out.Write('<absent>') } else { [Console]::Out.Write($env:FHIR_AUGURY_PROCESSING_DB) }",
                ])
            : new JiraAgentCommand(
                "/bin/sh",
                ["-c", "printf %s \"${FHIR_AUGURY_PROCESSING_DB-<absent>}\""]);

        JiraAgentResult result = await new JiraAgentCliRunner().RunAsync(
            command,
            WorkerContext(),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<absent>", result.StdoutTail);
    }

    private static JiraAgentCommandRenderer Renderer(
        string command,
        string? authoringCommand = null) =>
        new(Options.Create(new JiraProcessingOptions
        {
            AgentCliCommand = command,
            AuthoringAgentCliCommand = authoringCommand ?? command,
            JiraSourceAddress = "http://source",
        }));

    private static JiraAgentCommandContext Context(IReadOnlyDictionary<string, string>? extensionTokens = null) => new()
    {
        TicketKey = "FHIR-1",
        SourceTicketId = "row-1",
        DatabasePath = "db.sqlite",
        SourceTicketShape = "fhir",
        ExtensionTokens = extensionTokens ?? new Dictionary<string, string>(),
    };

    private static JiraAgentCommandContext WorkerContext() => Context() with
    {
        IsAuthoringWorker = true,
        RunId = "run-1",
        RunItemId = "item-1",
        CallbackUrl = "http://localhost/callback",
        OperationId = "operation-1",
        OperationToken = "super-secret-token",
        ExpectedSourceRevision = "revision-1",
    };
}
