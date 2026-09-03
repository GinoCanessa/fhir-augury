using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FhirAugury.Tools.TicketMdToDb.Tests;

public enum FakeJiraHydrationMode
{
    Resolved,
    NotFound,
    MissingSelfMetadata,
    MissingParentSpecification,
    InvalidType,
}

internal sealed class FakeOrchestratorHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _requestCounts =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FakeJiraHydrationMode> _jiraModes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JiraProjection> _jiraProjections =
        new(StringComparer.Ordinal);
    private readonly List<ResponseScript> _scripts = [];
    private readonly object _scriptLock = new();

    public void SetJiraMode(string key, FakeJiraHydrationMode mode) =>
        _jiraModes[key] = mode;

    public void SetJiraProjection(
        string key,
        string workGroup,
        string specification = "FHIR Core",
        string type = "Change Request") =>
        _jiraProjections[key] = new JiraProjection(workGroup, specification, type);

    public void EnqueueStatus(
        string pathAndQueryContains,
        HttpStatusCode status,
        TimeSpan? retryAfter = null)
    {
        lock (_scriptLock)
        {
            ResponseScript? script = _scripts.FirstOrDefault(
                item => string.Equals(
                    item.PathAndQueryContains,
                    pathAndQueryContains,
                    StringComparison.Ordinal));
            if (script is null)
            {
                script = new ResponseScript(pathAndQueryContains);
                _scripts.Add(script);
            }
            script.Responses.Enqueue(() =>
            {
                HttpResponseMessage response = new(status);
                if (retryAfter is not null)
                {
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(
                        retryAfter.Value);
                }
                return response;
            });
        }
    }

    public int CountRequestsContaining(string value) =>
        _requestCounts
            .Where(pair => pair.Key.Contains(value, StringComparison.Ordinal))
            .Sum(pair => pair.Value);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
        _requestCounts.AddOrUpdate(pathAndQuery, 1, (_, current) => current + 1);
        lock (_scriptLock)
        {
            foreach (ResponseScript script in _scripts)
            {
                if (pathAndQuery.Contains(
                        script.PathAndQueryContains,
                        StringComparison.Ordinal)
                    && script.Responses.TryDequeue(out Func<HttpResponseMessage>? response))
                {
                    return Task.FromResult(response());
                }
            }
        }

        if (pathAndQuery.StartsWith("/api/v1/jira/items/", StringComparison.Ordinal))
        {
            return Task.FromResult(CreateJiraResponse(pathAndQuery));
        }
        if (pathAndQuery.StartsWith("/api/v1/zulip/threads", StringComparison.Ordinal))
        {
            return Task.FromResult(Json(new
            {
                streamId = 10,
                stream = "FHIR Infrastructure",
                topic = "ballot",
                url = "https://chat.fhir.org/#narrow/stream/FHIR-Infrastructure/topic/ballot",
                messageCount = 3,
                firstMessageAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                lastMessageAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
                firstMessageExcerpt = "First message",
            }));
        }
        if (pathAndQuery.StartsWith("/api/v1/github/items/", StringComparison.Ordinal))
        {
            return Task.FromResult(Json(new
            {
                id = "github-item",
                title = "GitHub item",
                url = "https://github.com/HL7/fhir/issues/42",
                updatedAt = DateTimeOffset.Parse("2026-01-03T00:00:00Z"),
                metadata = new Dictionary<string, string>
                {
                    ["state"] = "open",
                    ["is_pull_request"] = "false",
                    ["labels"] = "change-request",
                },
            }));
        }
        if (pathAndQuery.StartsWith("/api/v1/github/repos/", StringComparison.Ordinal))
        {
            return Task.FromResult(Json(new
            {
                fullName = "HL7/fhir",
                description = "FHIR specification",
                category = "FhirCore",
                url = "https://github.com/HL7/fhir",
            }));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private HttpResponseMessage CreateJiraResponse(string pathAndQuery)
    {
        string key = Uri.UnescapeDataString(
            pathAndQuery["/api/v1/jira/items/".Length..].Split('?', 2)[0]);
        FakeJiraHydrationMode mode = _jiraModes.GetValueOrDefault(
            key,
            FakeJiraHydrationMode.Resolved);
        JiraProjection projection = _jiraProjections.GetValueOrDefault(
            key,
            new JiraProjection(
                "FHIR Infrastructure",
                "FHIR Core",
                "Change Request"));
        if (mode == FakeJiraHydrationMode.NotFound)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        bool parent = pathAndQuery.Contains(
            "includeContent=true",
            StringComparison.Ordinal);
        Dictionary<string, string> metadata = new()
        {
            ["priority"] = "Medium",
            ["resolution"] = "Persuasive",
            ["resolution_description_plain"] = "Accepted",
            ["specification"] = projection.Specification,
            ["raised_in_version"] = "R5",
            ["selected_ballot"] = "2026 May",
            ["change_category"] = "Clarification",
            ["impact"] = "Non-substantive",
            ["labels"] = "ballot",
            ["comment_count"] = "2",
            ["description_plain"] = "Ticket description",
            ["status"] = "Triaged",
            ["type"] = mode == FakeJiraHydrationMode.InvalidType
                ? "Enhancement"
                : projection.Type,
            ["work_group"] = projection.WorkGroup,
        };
        if (mode == FakeJiraHydrationMode.MissingParentSpecification && parent)
        {
            metadata.Remove("specification");
        }
        if (mode == FakeJiraHydrationMode.MissingSelfMetadata && !parent)
        {
            metadata.Remove("work_group");
            metadata.Remove("specification");
        }

        return Json(new
        {
            id = key,
            title = mode == FakeJiraHydrationMode.MissingSelfMetadata && !parent
                ? null
                : $"Title for {key}",
            url = $"https://jira.hl7.org/browse/{key}",
            updatedAt = DateTimeOffset.Parse("2026-01-04T00:00:00Z"),
            metadata,
        });
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class ResponseScript(string pathAndQueryContains)
    {
        public string PathAndQueryContains { get; } = pathAndQueryContains;
        public ConcurrentQueue<Func<HttpResponseMessage>> Responses { get; } = new();
    }

    private sealed record JiraProjection(
        string WorkGroup,
        string Specification,
        string Type);
}
