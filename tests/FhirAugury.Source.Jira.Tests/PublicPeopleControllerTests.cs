using System.Net;
using System.Text.Json;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace FhirAugury.Source.Jira.Tests;

public class PublicPeopleControllerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PreviewAndApply_SerializeSafeTypedContracts()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("private-account", "Raw Public Name"));
        PublicPeopleController controller = new(fixture.Service);
        JiraPublicPeoplePreviewRequest request = JsonSerializer.Deserialize<JiraPublicPeoplePreviewRequest>(
            """{"keys":["FHIR-1"],"evidenceMode":"upstream"}""", JsonOptions)!;
        ObjectResult previewResult = Assert.IsType<ObjectResult>(await controller.Preview(request, default));
        Assert.Equal(200, previewResult.StatusCode);
        JiraPublicPeoplePreviewResponse preview = Assert.IsType<JiraPublicPeoplePreviewResponse>(previewResult.Value);
        string previewJson = JsonSerializer.Serialize(preview, JsonOptions);
        Assert.DoesNotContain("private-account", previewJson);
        Assert.DoesNotContain("Raw Public Name", previewJson);
        Assert.DoesNotContain(fixture.Root, previewJson);
        Assert.Contains("missing-identity-binding", previewJson);
        Assert.Contains("available-public-name", previewJson);
        Assert.Contains("expiresAt", previewJson);
        Assert.Contains("affectedTickets", previewJson);
        Assert.NotNull(JsonSerializer.Deserialize<JiraPublicPeoplePreviewResponse>(previewJson, JsonOptions));

        JiraPublicPeopleApplyRequest apply = JsonSerializer.Deserialize<JiraPublicPeopleApplyRequest>(
            $$"""{"previewToken":"{{preview.PreviewToken}}","acknowledgeSharedUserImpact":false}""", JsonOptions)!;
        ObjectResult applyResult = Assert.IsType<ObjectResult>(await controller.Apply(apply, default));
        Assert.Equal(200, applyResult.StatusCode);
        JiraPublicPeopleApplyResponse response = Assert.IsType<JiraPublicPeopleApplyResponse>(applyResult.Value);
        Assert.Equal(JiraPublicPeopleCodes.Applied, response.Code);
        Assert.Equal(1, response.Changes!.IssueRowsUpdated);
        string resultJson = JsonSerializer.Serialize(response, JsonOptions);
        Assert.DoesNotContain("private-account", resultJson);
        Assert.DoesNotContain("Raw Public Name", resultJson);
        Assert.DoesNotContain(preview.PreviewToken!, resultJson);
    }

    [Fact]
    public async Task Preview_CacheOnlyNeverUsesUpstream()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        await fixture.SeedCache(JiraPeopleTestFixture.Json("FHIR-1",
            reporter: JiraPeopleTestFixture.Person("private-account", "Raw Public Name")), trusted: false);
        fixture.Http.BeforeResponse = _ => throw new InvalidOperationException("Must not be called");
        string before = fixture.Snapshot();
        PublicPeopleController controller = new(fixture.Service);

        ObjectResult result = Assert.IsType<ObjectResult>(await controller.Preview(new() { Keys = ["FHIR-1"] }, default));

        Assert.Equal(409, result.StatusCode);
        JiraPublicPeoplePreviewResponse preview = Assert.IsType<JiraPublicPeoplePreviewResponse>(result.Value);
        Assert.Equal(JiraPublicPeopleCodes.UnknownCacheOrigin, preview.Code);
        Assert.False(preview.CanApply);
        Assert.Equal(0, fixture.Http.RequestCount);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Apply_ConflictPreservesHttpStatus()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("private-account", "Raw Public Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        fixture.LocalProcessing.SetProcessed(new() { Key = "FHIR-1", ProcessedLocally = true });
        PublicPeopleController controller = new(fixture.Service);

        ObjectResult result = Assert.IsType<ObjectResult>(await controller.Apply(
            new() { PreviewToken = preview.PreviewToken! }, default));

        Assert.Equal(409, result.StatusCode);
        Assert.Equal(JiraPublicPeopleCodes.StalePreview, Assert.IsType<JiraPublicPeopleApplyResponse>(result.Value).Code);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Preview_UpstreamFailuresUse503WithoutRawBodiesOrIdentityDiagnostics(int status)
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.Http.StatusCode = (HttpStatusCode)status;
        fixture.Http.ResponseBody = "private-account Raw Name C:\\private\\cache upstream-body";
        PublicPeopleController controller = new(fixture.Service);

        ObjectResult result = Assert.IsType<ObjectResult>(await controller.Preview(
            new() { Keys = ["FHIR-1"], EvidenceMode = "upstream" }, default));

        Assert.Equal(503, result.StatusCode);
        string json = JsonSerializer.Serialize(result.Value, JsonOptions);
        Assert.Contains(JiraPublicPeopleCodes.UpstreamUnavailable, json);
        Assert.DoesNotContain("private-account", json);
        Assert.DoesNotContain("upstream-body", json);
        Assert.DoesNotContain("Raw Name", string.Join("\n", fixture.Logs));
    }

    [Theory]
    [InlineData("http", "upstream-http-failure")]
    [InlineData("task-timeout", "upstream-timeout")]
    [InlineData("timeout", "upstream-timeout")]
    [InlineData("timeout-rejected", "upstream-timeout")]
    [InlineData("circuit", "upstream-circuit-open")]
    [InlineData("isolated-circuit", "upstream-circuit-open")]
    [InlineData("rate-limit", "upstream-rate-limited")]
    [InlineData("io", "upstream-io-failure")]
    public async Task Preview_ExhaustedOperationalFailuresUse503AndSafeClassifications(string kind, string classification)
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        InvalidOperationException inner = new(JiraPeopleTestFixture.FailureDetails + " " + fixture.Root);
        Exception failure = kind switch
        {
            "http" => new HttpRequestException(JiraPeopleTestFixture.FailureDetails, inner),
            "task-timeout" => new TaskCanceledException(JiraPeopleTestFixture.FailureDetails, inner),
            "timeout" => new TimeoutException(JiraPeopleTestFixture.FailureDetails, inner),
            "timeout-rejected" => new TimeoutRejectedException(JiraPeopleTestFixture.FailureDetails, inner),
            "circuit" => new BrokenCircuitException(JiraPeopleTestFixture.FailureDetails, inner),
            "isolated-circuit" => new IsolatedCircuitException(JiraPeopleTestFixture.FailureDetails, inner),
            "rate-limit" => new RateLimiterRejectedException(JiraPeopleTestFixture.FailureDetails, inner),
            _ => new IOException(JiraPeopleTestFixture.FailureDetails, inner),
        };
        fixture.Http.BeforeResponse = _ => throw failure;
        string before = fixture.Snapshot();
        PublicPeopleController controller = new(fixture.Service);

        ObjectResult result = Assert.IsType<ObjectResult>(await controller.Preview(
            new() { Keys = ["FHIR-1"], EvidenceMode = "upstream" }, default));

        Assert.Equal(503, result.StatusCode);
        JiraPublicPeoplePreviewResponse preview = Assert.IsType<JiraPublicPeoplePreviewResponse>(result.Value);
        Assert.Equal(JiraPublicPeopleCodes.UpstreamUnavailable, preview.Code);
        Assert.False(preview.CanApply);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Contains(fixture.Logs, line => line.Contains($"failure: {classification}", StringComparison.Ordinal));
        fixture.AssertSafeDiagnostics(preview);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("apply")]
    public async Task DatabaseErrorsUse500InsteadOfAStaleOrBusyRefusal(string operation)
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        JiraPublicPeoplePreviewResponse? preview = operation == "apply" ? await fixture.Preview() : null;
        fixture.Execute("DROP TABLE jira_users;");
        string before = fixture.Snapshot();
        PublicPeopleController controller = new(fixture.Service);

        ObjectResult result = Assert.IsType<ObjectResult>(operation == "preview"
            ? await controller.Preview(new() { Keys = ["FHIR-1"], EvidenceMode = "upstream" }, default)
            : await controller.Apply(new() { PreviewToken = preview!.PreviewToken! }, default));

        Assert.Equal(500, result.StatusCode);
        Assert.Contains(JiraPublicPeopleCodes.InternalError, JsonSerializer.Serialize(result.Value, JsonOptions));
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(0, fixture.Revision);
        Assert.False(fixture.Pipeline.IsRunning);
        Assert.Contains(fixture.Logs, line => line.Contains("SQLite error code: 1", StringComparison.Ordinal));
        Assert.DoesNotContain("jira_users", string.Join("\n", fixture.Logs));
        fixture.AssertSafeDiagnostics(result.Value);
    }

    [Theory]
    [InlineData("preview", 5)]
    [InlineData("preview", 6)]
    [InlineData("apply", 5)]
    [InlineData("apply", 6)]
    public async Task SqliteBusyRemains409AndDoesNotBecomeAnUpstreamFailure(string operation, int errorCode)
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        JiraPublicPeoplePreviewResponse? preview = operation == "apply" ? await fixture.Preview() : null;
        SqliteException failure = new(JiraPeopleTestFixture.FailureDetails, errorCode);
        // Inject provider failures without holding real file locks or waiting
        // for SQLite's busy timeout.
        if (operation == "preview") fixture.Http.BeforeResponse = _ => throw failure;
        else fixture.Builder.AfterRebuild = _ => throw failure;
        string before = fixture.Snapshot();
        PublicPeopleController controller = new(fixture.Service);

        ObjectResult result = Assert.IsType<ObjectResult>(operation == "preview"
            ? await controller.Preview(new() { Keys = ["FHIR-1"], EvidenceMode = "upstream" }, default)
            : await controller.Apply(new() { PreviewToken = preview!.PreviewToken! }, default));

        Assert.Equal(409, result.StatusCode);
        Assert.Contains(JiraPublicPeopleCodes.SourceBusy, JsonSerializer.Serialize(result.Value, JsonOptions));
        Assert.DoesNotContain(JiraPublicPeopleCodes.UpstreamUnavailable, string.Join("\n", fixture.Logs));
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(fixture.Pipeline.IsRunning);
        fixture.AssertSafeDiagnostics(result.Value);
    }

    [Fact]
    public async Task Preview_RequiresExistingAuthenticationAndClassifiesMalformedUpstream()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.Options.Value.ApiToken = null;
        Assert.Equal(JiraPublicPeopleCodes.UpstreamUnavailable, (await fixture.Preview()).Code);
        Assert.Equal(0, fixture.Http.RequestCount);
        fixture.Options.Value.ApiToken = "fixture-token";
        fixture.Http.ResponseBody = "<html>private login body</html>";
        Assert.Equal(JiraPublicPeopleCodes.UpstreamUnavailable, (await fixture.Preview()).Code);
        Assert.DoesNotContain("private login body", string.Join("\n", fixture.Logs));
    }

    [Fact]
    public async Task Preview_XmlModeUsesTheRegisteredAuthenticatedReadClientWithoutIngestion()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.Options.Value.AuthMode = "cookie";
        fixture.Options.Value.Cookie = "fixture-cookie";
        fixture.Http.ResponseBody = """
            <rss><channel><item><key>FHIR-1</key>
            <updated>Tue, 15 Sep 2026 12:00:00 -0500</updated>
            <reporter username="exact-account">Public Name</reporter>
            <assignee username="-1">Unassigned</assignee>
            <customfields><customfield id="customfield_11000"><customfieldvalues>
            <customfieldvalue>private-requester</customfieldvalue>
            </customfieldvalues></customfield></customfields>
            </item></channel></rss>
            """;
        string before = fixture.Snapshot();
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        Assert.True(preview.CanApply);
        Assert.Equal("jira-xml", fixture.Http.LastClient);
        Assert.Contains(JiraPublicPeopleCodes.MissingExplicitNameEvidence, preview.Tickets[0].Roles[2].Reasons);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Empty(fixture.Cache.EnumerateKeys("jira"));
        Assert.Equal(0, fixture.Revision);
    }

    [Fact]
    public async Task InvalidAndExpiredRequestsUse400And410()
    {
        using JiraPeopleTestFixture fixture = new();
        PublicPeopleController controller = new(fixture.Service);
        Assert.Equal(400, Assert.IsType<ObjectResult>(await controller.Preview(new(), default)).StatusCode);
        Assert.Equal(400, Assert.IsType<ObjectResult>(await controller.Apply(new(), default)).StatusCode);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.Apply(
            new() { PreviewToken = new string('A', 64) }, default)).StatusCode);
    }

    [Theory]
    [InlineData("names")]
    [InlineData("identities")]
    [InlineData("path")]
    [InlineData("url")]
    [InlineData("reporterUserId")]
    public void RequestsRejectCallerSuppliedReplacementEvidence(string property)
    {
        string preview = $$"""{"keys":["FHIR-1"],"{{property}}":"private-value"}""";
        string apply = $$"""{"previewToken":"opaque","{{property}}":"private-value"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JiraPublicPeoplePreviewRequest>(preview, JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JiraPublicPeopleApplyRequest>(apply, JsonOptions));
    }
}
