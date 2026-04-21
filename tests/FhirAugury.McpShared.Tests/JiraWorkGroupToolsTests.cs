using FhirAugury.McpShared.Tools;

namespace FhirAugury.McpShared.Tests;

public class JiraWorkGroupToolsTests
{
    [Fact]
    public async Task ListJiraWorkGroups_ReturnsJoinedHl7Fields()
    {
        // Mirrors the JiraWorkGroupSummaryEntry shape returned by
        // GET /api/v1/jira/work-groups (camel-cased by ASP.NET Core defaults).
        string json = """
            [
              {
                "name": "Orders & Observations",
                "issueCount": 42,
                "workGroupCode": "oo",
                "workGroupNameClean": "OrdersAndObservations",
                "workGroupDefinition": "Orders and Observations Work Group",
                "workGroupRetired": false
              },
              {
                "name": "FHIR Infrastructure",
                "issueCount": 17,
                "workGroupCode": "fhir",
                "workGroupNameClean": "FHIRInfrastructure",
                "workGroupDefinition": null,
                "workGroupRetired": true
              }
            ]
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraWorkGroupTools.ListJiraWorkGroups(factory);

        // The pass-through formatter must surface every canonical field so
        // downstream callers can resolve work-group slugs without
        // re-implementing Hl7WorkGroupNameCleaner. (FormatJson uses default
        // System.Text.Json options which escape '&' to \u0026.)
        Assert.Contains("Orders \\u0026 Observations", result);
        Assert.Contains("\"workGroupCode\": \"oo\"", result);
        Assert.Contains("\"workGroupNameClean\": \"OrdersAndObservations\"", result);
        Assert.Contains("\"workGroupNameClean\": \"FHIRInfrastructure\"", result);
        Assert.Contains("\"workGroupRetired\": true", result);
        Assert.Contains("\"workGroupDefinition\": \"Orders and Observations Work Group\"", result);
    }

    [Fact]
    public async Task ListJiraWorkGroupIssues_ReturnsFormattedJson()
    {
        string json = """
            [{ "key": "FHIR-100", "title": "Test", "workGroup": "FHIR Infrastructure" }]
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraWorkGroupTools.ListJiraWorkGroupIssues(factory, "fhir");

        Assert.Contains("FHIR-100", result);
        Assert.Contains("FHIR Infrastructure", result);
    }
}
