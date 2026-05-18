using System.Text.Json;

namespace FhirAugury.Tools.PreparerSite.Tests;

public class WorkGroupResolverParseTests
{
    [Fact]
    public void ParsesLegacyBareArrayShape()
    {
        const string Json = """
            [
              { "name": "Orders & Observations", "workGroupCode": "oo", "workGroupNameClean": "OrdersAndObservations" },
              { "name": "Patient Care", "workGroupCode": "pc", "workGroupNameClean": "PatientCare" }
            ]
            """;
        JsonElement element = JsonDocument.Parse(Json).RootElement;

        List<WorkGroupResolver.WorkGroupDto> parsed = WorkGroupResolver.ParseWorkGroups(element);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Orders & Observations", parsed[0].Name);
        Assert.Equal("oo", parsed[0].WorkGroupCode);
        Assert.Equal("OrdersAndObservations", parsed[0].WorkGroupNameClean);
    }

    [Fact]
    public void ParsesNewEnvelopeShape_CamelCase()
    {
        const string Json = """
            {
              "catalogJoinDegraded": false,
              "items": [
                { "name": "Orders & Observations", "workGroupCode": "oo", "workGroupNameClean": "OrdersAndObservations" }
              ]
            }
            """;
        JsonElement element = JsonDocument.Parse(Json).RootElement;

        List<WorkGroupResolver.WorkGroupDto> parsed = WorkGroupResolver.ParseWorkGroups(element);

        Assert.Single(parsed);
        Assert.Equal("oo", parsed[0].WorkGroupCode);
    }

    [Fact]
    public void ParsesNewEnvelopeShape_PascalCase()
    {
        const string Json = """
            {
              "CatalogJoinDegraded": true,
              "Items": [
                { "Name": "Orphan WG", "WorkGroupCode": null, "WorkGroupNameClean": "OrphanWG" }
              ]
            }
            """;
        JsonElement element = JsonDocument.Parse(Json).RootElement;

        List<WorkGroupResolver.WorkGroupDto> parsed = WorkGroupResolver.ParseWorkGroups(element);

        Assert.Single(parsed);
        Assert.Null(parsed[0].WorkGroupCode);
        Assert.Equal("OrphanWG", parsed[0].WorkGroupNameClean);
    }

    [Fact]
    public void UnknownShape_ReturnsEmpty()
    {
        JsonElement element = JsonDocument.Parse("\"not-an-array-or-envelope\"").RootElement;
        List<WorkGroupResolver.WorkGroupDto> parsed = WorkGroupResolver.ParseWorkGroups(element);
        Assert.Empty(parsed);
    }
}
