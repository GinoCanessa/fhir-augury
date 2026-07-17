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

    private static JiraAgentCommandRenderer Renderer(string command) => new(Options.Create(new JiraProcessingOptions { AgentCliCommand = command, JiraSourceAddress = "http://source" }));

    private static JiraAgentCommandContext Context(IReadOnlyDictionary<string, string>? extensionTokens = null) => new()
    {
        TicketKey = "FHIR-1",
        SourceTicketId = "row-1",
        DatabasePath = "db.sqlite",
        SourceTicketShape = "fhir",
        ExtensionTokens = extensionTokens ?? new Dictionary<string, string>(),
    };
}
