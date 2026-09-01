using FhirAugury.Cli.Dispatch;
using FhirAugury.Cli.Dispatch.Handlers;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Tests;

public class JiraReadModelDispatchTests
{
    [Theory]
    [InlineData("jira-baldef")]
    [InlineData("jira-ballot")]
    [InlineData("jira-pss")]
    public void KnownCommands_IncludesJiraReadModelCommand(string command)
    {
        Assert.Contains(command, CommandDispatcher.KnownCommands);
    }

    // The following exercise parse → dispatch → handler: the "requires a key"
    // error is produced by BuildUrl before any HTTP call, proving the command
    // deserialized to the right request type and routed to the right handler.

    [Theory]
    [InlineData("jira-baldef")]
    [InlineData("jira-ballot")]
    [InlineData("jira-pss")]
    public async Task ReadModelGet_MissingKey_RoutesToHandlerAndErrors(string command)
    {
        OutputEnvelope env = await CommandDispatcher.ExecuteAsync(
            $$"""{"command":"{{command}}","action":"get"}""");

        Assert.False(env.Success);
        Assert.Contains("key", env.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BalDef_BuildUrl_ListWithFilters()
    {
        string url = JiraBalDefHandler.BuildUrl(new JiraBalDefRequest
        {
            Action = "list",
            Cycle = "2025-Sep",
            Level = "Normative",
        });

        Assert.Equal("/api/v1/jira/baldef?cycle=2025-Sep&level=Normative", url);
    }

    [Fact]
    public void BalDef_BuildUrl_GetEscapesKey()
    {
        string url = JiraBalDefHandler.BuildUrl(new JiraBalDefRequest { Action = "get", Key = "BALDEF-1" });
        Assert.Equal("/api/v1/jira/baldef/BALDEF-1", url);
    }

    [Fact]
    public void Ballot_BuildUrl_ListWithFilters()
    {
        string url = JiraBallotHandler.BuildUrl(new JiraBallotRequest
        {
            Action = "list",
            Cycle = "2025-Sep",
            Specification = "fhir-core",
            Disposition = "open",
        });

        Assert.Equal("/api/v1/jira/ballot?cycle=2025-Sep&specification=fhir-core&disposition=open", url);
    }

    [Fact]
    public void Ballot_BuildUrl_GetByKey()
    {
        string url = JiraBallotHandler.BuildUrl(new JiraBallotRequest { Action = "get", Key = "BALLOT-1" });
        Assert.Equal("/api/v1/jira/ballot/BALLOT-1", url);
    }

    [Fact]
    public void Pss_BuildUrl_ListWithFilters()
    {
        string url = JiraPssHandler.BuildUrl(new JiraPssRequest
        {
            Action = "list",
            WorkGroup = "fhir-i",
            Status = "Approved",
            Limit = 10,
        });

        Assert.Equal("/api/v1/jira/pss?workGroup=fhir-i&status=Approved&limit=10", url);
    }

    [Fact]
    public void Pss_BuildUrl_GetByKey()
    {
        string url = JiraPssHandler.BuildUrl(new JiraPssRequest { Action = "get", Key = "PSS-1" });
        Assert.Equal("/api/v1/jira/pss/PSS-1", url);
    }

    [Fact]
    public void BuildUrl_UnknownAction_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            JiraBalDefHandler.BuildUrl(new JiraBalDefRequest { Action = "frobnicate" }));
    }
}
