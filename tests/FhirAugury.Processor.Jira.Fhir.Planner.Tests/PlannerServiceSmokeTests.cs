using System.Net;
using System.Net.Http.Json;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Authoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    [Fact]
    public void Host_UsesSharedAuthoringRunScheduler()
    {
        _ = fixture.Factory.CreateClient();

        AuthoringRunScheduler<JiraAuthoringWorkItem> scheduler =
            fixture.Factory.Services.GetRequiredService<
                AuthoringRunScheduler<JiraAuthoringWorkItem>>();
        Assert.Contains(
            fixture.Factory.Services.GetServices<IHostedService>(),
            service => ReferenceEquals(service, scheduler));
    }
}
