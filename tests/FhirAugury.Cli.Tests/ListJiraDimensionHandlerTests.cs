using System.Text.Json;
using FhirAugury.Cli.Dispatch.Handlers;

namespace FhirAugury.Cli.Tests;

public class ListJiraDimensionHandlerTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ProjectItems_Workgroups_SurfacesCanonicalHl7Fields()
    {
        // Mirrors the JiraWorkGroupSummaryEntry shape returned by
        // GET /api/v1/jira/work-groups (camel-cased by ASP.NET Core defaults).
        JsonElement response = Parse("""
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
            """);

        List<object> items = ListJiraDimensionHandler.ProjectItems("workgroups", response, limit: null);

        Assert.Equal(2, items.Count);

        // Round-trip through JSON to assert the anonymous-object shape.
        string serialized = JsonSerializer.Serialize(items);
        JsonElement re = Parse(serialized);

        JsonElement first = re[0];
        Assert.Equal("Orders & Observations", first.GetProperty("name").GetString());
        Assert.Equal("oo", first.GetProperty("code").GetString());
        Assert.Equal("OrdersAndObservations", first.GetProperty("nameClean").GetString());
        Assert.Equal("Orders and Observations Work Group", first.GetProperty("definition").GetString());
        Assert.False(first.GetProperty("retired").GetBoolean());
        Assert.Equal(42, first.GetProperty("issueCount").GetInt32());

        JsonElement second = re[1];
        Assert.Equal("FHIRInfrastructure", second.GetProperty("nameClean").GetString());
        Assert.True(second.GetProperty("retired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("definition").ValueKind);
    }

    [Fact]
    public void ProjectItems_Workgroups_DimensionMatchIsCaseInsensitive()
    {
        JsonElement response = Parse("""
            [{ "name": "X", "issueCount": 1, "workGroupNameClean": "X" }]
            """);

        List<object> items = ListJiraDimensionHandler.ProjectItems("WorkGroups", response, limit: null);

        string serialized = JsonSerializer.Serialize(items);
        JsonElement re = Parse(serialized);
        Assert.Equal("X", re[0].GetProperty("nameClean").GetString());
    }

    [Fact]
    public void ProjectItems_Workgroups_MissingHl7Fields_ProducesNullsAndFalseRetired()
    {
        // Free-text Jira workgroup with no HL7 catalog match.
        JsonElement response = Parse("""
            [{ "name": "Ad Hoc Group", "issueCount": 3 }]
            """);

        List<object> items = ListJiraDimensionHandler.ProjectItems("workgroups", response, limit: null);

        string serialized = JsonSerializer.Serialize(items);
        JsonElement re = Parse(serialized);
        JsonElement only = re[0];
        Assert.Equal("Ad Hoc Group", only.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, only.GetProperty("code").ValueKind);
        Assert.Equal(JsonValueKind.Null, only.GetProperty("nameClean").ValueKind);
        Assert.Equal(JsonValueKind.Null, only.GetProperty("definition").ValueKind);
        Assert.False(only.GetProperty("retired").GetBoolean());
        Assert.Equal(3, only.GetProperty("issueCount").GetInt32());
    }

    [Fact]
    public void ProjectItems_NonWorkgroups_KeepsNarrowShape()
    {
        // Other dimensions (specifications/labels/statuses) don't have
        // code/nameClean — the projection must NOT add those fields.
        JsonElement response = Parse("""
            [
              { "name": "FHIR Core", "issueCount": 100 },
              { "name": "US Core",   "issueCount": 50  }
            ]
            """);

        foreach (string dim in new[] { "specifications", "labels", "statuses" })
        {
            List<object> items = ListJiraDimensionHandler.ProjectItems(dim, response, limit: null);
            string serialized = JsonSerializer.Serialize(items);
            JsonElement re = Parse(serialized);

            Assert.Equal(2, re.GetArrayLength());
            Assert.Equal("FHIR Core", re[0].GetProperty("name").GetString());
            Assert.Equal(100, re[0].GetProperty("issueCount").GetInt32());
            Assert.False(re[0].TryGetProperty("code", out _));
            Assert.False(re[0].TryGetProperty("nameClean", out _));
            Assert.False(re[0].TryGetProperty("retired", out _));
        }
    }

    [Fact]
    public void ProjectItems_Limit_TruncatesResults()
    {
        JsonElement response = Parse("""
            [
              { "name": "a", "issueCount": 1 },
              { "name": "b", "issueCount": 2 },
              { "name": "c", "issueCount": 3 }
            ]
            """);

        List<object> items = ListJiraDimensionHandler.ProjectItems("statuses", response, limit: 2);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void ProjectItems_LimitZeroOrNegative_ReturnsAll()
    {
        JsonElement response = Parse("""
            [
              { "name": "a", "issueCount": 1 },
              { "name": "b", "issueCount": 2 }
            ]
            """);

        Assert.Equal(2, ListJiraDimensionHandler.ProjectItems("statuses", response, limit: 0).Count);
        Assert.Equal(2, ListJiraDimensionHandler.ProjectItems("statuses", response, limit: -1).Count);
        Assert.Equal(2, ListJiraDimensionHandler.ProjectItems("statuses", response, limit: null).Count);
    }

    [Fact]
    public void ProjectItems_NonArrayResponse_ReturnsEmpty()
    {
        JsonElement response = Parse("""{ "error": "boom" }""");

        Assert.Empty(ListJiraDimensionHandler.ProjectItems("workgroups", response, limit: null));
    }
}
