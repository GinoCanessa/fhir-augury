using FhirAugury.Common.Api;
using FhirAugury.DevUi.Models;
using FhirAugury.DevUi.Services;
using System.Net;
using System.Text;

namespace FhirAugury.DevUi.Tests;

public sealed class ReadinessEvaluatorTests
{
    private readonly TicketWorkflowDefinition _planner =
        new TicketWorkflowCatalog().Get("plan");

    [Fact]
    public void HealthyProcessorAndDependenciesAreReady()
    {
        DateTimeOffset checkedAt =
            new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        ServicesStatusResponse response = new(
        [
            Service(
                "Orchestrator",
                "orchestrator",
                "healthy",
                checkedAt),
            Service(
                "Planner",
                "processing",
                "healthy",
                checkedAt,
                ["Jira", "GitHub"]),
            Service("Jira", "source", "ok", checkedAt),
            Service("GitHub", "source", "healthy", checkedAt),
        ],
        checkedAt);

        TicketWorkflowReadiness readiness =
            new ReadinessEvaluator().Evaluate(
                response,
                _planner);

        Assert.True(readiness.CanStart);
        Assert.Empty(readiness.Blockers);
        Assert.Equal(checkedAt, readiness.CheckedAt);
        Assert.All(
            readiness.RequiredServices,
            observation =>
                Assert.Equal(
                    ServiceReadinessState.Ready,
                    observation.State));
    }

    [Fact]
    public void NeverObservedDependencyUsesEvidenceBasedWording()
    {
        DateTimeOffset checkedAt =
            new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        ServicesStatusResponse response = new(
        [
            Service(
                "Orchestrator",
                "orchestrator",
                "healthy",
                checkedAt),
            Service(
                "Planner",
                "processing",
                "healthy",
                checkedAt,
                ["Jira", "GitHub"]),
            Service("Jira", "source", "healthy", checkedAt),
            new ServiceHealthInfo
            {
                Name = "GitHub",
                ServiceKind = "source",
                Status = "unobserved",
                Enabled = true,
                Configured = true,
            },
        ],
        checkedAt);

        TicketWorkflowReadiness readiness =
            new ReadinessEvaluator().Evaluate(
                response,
                _planner);

        ServiceReadinessObservation blocker =
            Assert.Single(
                readiness.Blockers,
                observation =>
                    observation.Name == "GitHub");
        Assert.Equal(
            ServiceReadinessState.NotObserved,
            blocker.State);
        Assert.Contains(
            "not been observed",
            blocker.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "not started",
            blocker.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisabledConfiguredDependencyIsAnAuthoritativeBlocker()
    {
        DateTimeOffset checkedAt =
            new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        ServicesStatusResponse response = new(
        [
            Service(
                "Orchestrator",
                "orchestrator",
                "healthy",
                checkedAt),
            Service(
                "Planner",
                "processing",
                "healthy",
                checkedAt,
                ["Jira", "GitHub"]),
            Service("Jira", "source", "healthy", checkedAt),
            new ServiceHealthInfo
            {
                Name = "GitHub",
                ServiceKind = "source",
                Status = "not_configured",
                Enabled = false,
                Configured = true,
                CheckedAt = checkedAt,
            },
        ],
        checkedAt);

        TicketWorkflowReadiness readiness =
            new ReadinessEvaluator().Evaluate(
                response,
                _planner);

        Assert.False(readiness.CanStart);
        ServiceReadinessObservation blocker =
            Assert.Single(
                readiness.Blockers,
                observation =>
                    observation.Name == "GitHub");
        Assert.Equal(
            ServiceReadinessState.Disabled,
            blocker.State);
    }

    [Fact]
    public void MissingProcessorAndOrchestratorAreNotObserved()
    {
        ServicesStatusResponse response = new([]);

        TicketWorkflowReadiness readiness =
            new ReadinessEvaluator().Evaluate(
                response,
                _planner);

        Assert.Equal(
            ServiceReadinessState.NotObserved,
            readiness.Orchestrator.State);
        Assert.Equal(
            ServiceReadinessState.NotObserved,
            readiness.Processor.State);
        Assert.Contains(
            readiness.Blockers,
            blocker => blocker.Name == "Jira");
        Assert.Contains(
            readiness.Blockers,
            blocker => blocker.Name == "GitHub");
    }

    [Theory]
    [InlineData("degraded", ServiceReadinessState.Degraded)]
    [InlineData("timeout", ServiceReadinessState.Unavailable)]
    [InlineData("unavailable", ServiceReadinessState.Unavailable)]
    public void NonhealthyProcessorStatusBlocksWorkflow(
        string status,
        ServiceReadinessState expected)
    {
        DateTimeOffset checkedAt =
            new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        ServicesStatusResponse response = new(
        [
            Service(
                "Orchestrator",
                "orchestrator",
                "healthy",
                checkedAt),
            Service(
                "Planner",
                "processing",
                status,
                checkedAt,
                ["Jira", "GitHub"]),
            Service("Jira", "source", "healthy", checkedAt),
            Service("GitHub", "source", "healthy", checkedAt),
        ],
        checkedAt);

        TicketWorkflowReadiness readiness =
            new ReadinessEvaluator().Evaluate(
                response,
                _planner);

        Assert.Equal(expected, readiness.Processor.State);
        Assert.False(readiness.CanStart);
    }

    [Fact]
    public void HealthyButPausedProcessorBlocksStartingWork()
    {
        DateTimeOffset checkedAt =
            new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        ServiceHealthInfo planner = Service(
            "Planner",
            "processing",
            "healthy",
            checkedAt,
            ["Jira", "GitHub"]) with
        {
            ProcessingStatus = "paused",
            ProcessingIsRunning = false,
        };
        ServicesStatusResponse response = new(
        [
            Service(
                "Orchestrator",
                "orchestrator",
                "healthy",
                checkedAt),
            planner,
            Service("Jira", "source", "healthy", checkedAt),
            Service("GitHub", "source", "healthy", checkedAt),
        ],
        checkedAt);

        TicketWorkflowReadiness readiness =
            new ReadinessEvaluator().Evaluate(
                response,
                _planner);

        Assert.False(readiness.CanStart);
        Assert.Equal(
            ServiceReadinessState.Degraded,
            readiness.Processor.State);
        Assert.Contains(
            "paused",
            readiness.Processor.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadinessClientUsesTypedGetAndRefreshRoutes()
    {
        List<(HttpMethod Method, string Path)> requests = [];
        DateTimeOffset checkedAt =
            new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        string json =
            $$"""
            {
              "services": [
                {
                  "name": "Orchestrator",
                  "status": "healthy",
                  "serviceKind": "orchestrator",
                  "enabled": true,
                  "configured": true,
                  "checkedAt": "{{checkedAt:O}}"
                }
              ],
              "lastCheckedAt": "{{checkedAt:O}}"
            }
            """;
        using HttpClient httpClient = new(
            new DelegateHandler(request =>
            {
                requests.Add((
                    request.Method,
                    request.RequestUri!.AbsolutePath));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"),
                };
            }))
        {
            BaseAddress = new Uri("http://orchestrator/"),
        };
        OrchestratorReadinessClient client =
            new(httpClient);

        ServicesStatusResponse current =
            await client.GetAsync();
        ServicesStatusResponse refreshed =
            await client.RefreshAsync();

        Assert.Equal(checkedAt, current.LastCheckedAt);
        Assert.Equal(checkedAt, refreshed.LastCheckedAt);
        Assert.Equal(
        [
            (HttpMethod.Get, "/api/v1/services"),
            (HttpMethod.Post, "/api/v1/services/refresh"),
        ],
        requests);
    }

    private static ServiceHealthInfo Service(
        string name,
        string kind,
        string status,
        DateTimeOffset checkedAt,
        List<string>? requiredServices = null) => new()
        {
            Name = name,
            ServiceKind = kind,
            Status = status,
            Enabled = true,
            Configured = true,
            CheckedAt = checkedAt,
            ProcessingIsRunning =
                kind == "processing" ? true : null,
            RequiredServices = requiredServices ?? [],
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
