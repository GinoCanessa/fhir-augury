using System.Net;
using System.Net.Http.Json;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerServiceSmokeTests(
    PlannerControllerTests.Fixture fixture)
    : IClassFixture<PlannerControllerTests.Fixture>
{
    [Fact]
    public async Task StagedAuthoringResultRouteIsMapped()
    {
        HttpResponseMessage response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/processing/authoring/runs/run/items/item/result",
            new { });

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
