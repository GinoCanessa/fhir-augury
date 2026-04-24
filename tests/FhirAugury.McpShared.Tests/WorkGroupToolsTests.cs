using FhirAugury.McpShared.Tools;

namespace FhirAugury.McpShared.Tests;

public class WorkGroupToolsTests
{
    [Fact]
    public async Task GitHubWorkGroupForPath_FormatsResolveResponse()
    {
        // Mirrors WorkGroupResolveResponse (camel-cased by ASP.NET defaults).
        string json = """
            {
              "repoFullName": "HL7/fhir",
              "path": "source/patient/patient-introduction.md",
              "workGroup": "fhir-i",
              "workGroupRaw": null,
              "matchedStage": "exact-file"
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await WorkGroupTools.GitHubWorkGroupForPath(
            factory, "HL7/fhir", "source/patient/patient-introduction.md");

        Assert.Contains("\"workGroup\": \"fhir-i\"", result);
        Assert.Contains("\"matchedStage\": \"exact-file\"", result);
        Assert.Contains("\"repoFullName\": \"HL7/fhir\"", result);
    }
}
