using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Source.Zulip.Controllers;
using FhirAugury.Source.Zulip.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Zulip.Tests;

/// <summary>
/// Real source MVC serialization and shared hydration, using test-created SQLite
/// and in-memory HTTP. These fixtures do not establish recovery of the reported
/// live JSON-failure corpus.
/// </summary>
public sealed class ZulipReferenceWireContractTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset HydratedAt = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly ZulipReferenceTestDatabase _fixture = new();
    private WebApplication _app = null!;
    private HttpClient _sourceClient = null!;

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_fixture.Database);
        builder.Services.AddSingleton(ZulipReferenceTestDatabase.Options);
        builder.Services.AddSingleton<ZulipReferenceResolver>();
        builder.Services.AddControllers().AddApplicationPart(typeof(ReferencesController).Assembly);
        _app = builder.Build();
        _app.MapControllers();
        await _app.StartAsync();
        _sourceClient = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _sourceClient.Dispose();
        await _app.DisposeAsync();
        _fixture.Dispose();
    }

    [Theory]
    [InlineData("321987", "implementers", "ballot", true)]
    [InlineData("000321987", "implementers", "ballot", true)]
    [InlineData(" 321987 ", "implementers", "ballot", true)]
    [InlineData("implementers:ballot", "implementers", "ballot", false)]
    [InlineData("fhir/infrastructure-wg:entry/request: réponse 100% & 漢字", "fhir/infrastructure-wg", "entry/request: réponse 100% & 漢字", false)]
    [InlineData("fhir:core:literal %2F / 🩺", "fhir:core", "literal %2F / 🩺", false)]
    [InlineData("implementers: topic with surrounding spaces ", "implementers", " topic with surrounding spaces ", false)]
    public async Task SerializedSourceResponse_IsConsumedByHydration(string reference, string stream, string topic, bool isMessage)
    {
        _fixture.AddStream(17, 9876, stream);
        _fixture.AddMessage(321987, 17, stream, topic, "2026-05-01 10:00:00+03:00", "first source body");
        _fixture.AddMessage(321988, 17, stream, topic, "2026-05-02T12:00:00-02:00");
        using GatewayPathHandler gateway = new(_app.GetTestServer().CreateHandler());
        using HttpClient client = NewClient(gateway);
        CapturingLogger logger = new();
        OrchestratorHydrationFetcher fetcher = new(client, logger);

        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", reference, HydratedAt, default);

        Assert.Equal("resolved", row.HydrationStatus);
        Assert.Equal(reference, row.ZulipThreadId);
        Assert.Equal(HydratedAt, row.HydratedAt);
        Assert.Equal(9876, row.StreamId);
        Assert.Equal(stream, row.StreamName);
        Assert.Equal(topic, row.Topic);
        Assert.Equal(2, row.MessageCount);
        Assert.Equal("first source body", row.FirstMessageExcerpt);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 7, 0, 0, TimeSpan.Zero), row.FirstMessageAt);
        Assert.Equal(new DateTimeOffset(2026, 5, 2, 14, 0, 0, TimeSpan.Zero), row.LastMessageAt);
        string expectedUrl = $"https://chat.example.com/#narrow/stream/{Uri.EscapeDataString(stream)}/topic/{Uri.EscapeDataString(topic)}";
        Assert.Equal(isMessage ? expectedUrl + "/near/321987" : expectedUrl, row.Url);
        Assert.Equal(
            $"/api/v1/zulip/references/resolve?reference={Uri.EscapeDataString(reference)}",
            Assert.Single(gateway.Requests));
        ZulipReferenceHydrationOutcome outcome = ReadOutcome(row);
        Assert.Equal(ZulipReferenceBacking.TypedResolver, outcome.Backing);
        Assert.Equal(ZulipReferenceLookupOutcome.Resolved, outcome.LatestOutcome);
        Assert.Empty(outcome.Diagnostics);
        Assert.Empty(logger.Messages);
    }

    [Theory]
    [InlineData("2026-05-01 10:00:00+03:00", "2026-05-01T07:00:00+00:00")]
    [InlineData("2026-05-01T10:00:00-02:00", "2026-05-01T12:00:00+00:00")]
    [InlineData("2026-05-01 10:00:00", "2026-05-01T10:00:00+00:00")]
    public async Task SerializedThreadResponse_UsesCanonicalDates(string timestamp, string expected)
    {
        _fixture.AddStream(17, 9876, "implementers");
        _fixture.AddMessage(321987, 17, "implementers", "ballot", timestamp, "body stays unchanged");

        using HttpResponseMessage response = await _sourceClient.GetAsync("/api/v1/threads?streamName=implementers&topic=ballot");
        JsonElement root = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, root.GetProperty("firstMessageAt").GetString());
        Assert.Equal(expected, root.GetProperty("lastMessageAt").GetString());
        Assert.Equal(expected, root.GetProperty("messages")[0].GetProperty("timestamp").GetString());
        Assert.Equal("body stays unchanged", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("<p>body stays unchanged</p>", root.GetProperty("messages")[0].GetProperty("contentHtml").GetString());
        Assert.Equal(1, root.GetProperty("messageCount").GetInt32());
        Assert.Equal(1, root.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task SerializedSourceResponse_MixedOffsetsAreOrderedByInstant()
    {
        _fixture.AddStream(17, 9876, "implementers");
        _fixture.AddMessage(321987, 17, "implementers", "ballot", "2026-05-01T00:30:00+02:00");
        _fixture.AddMessage(321988, 17, "implementers", "ballot", "2026-04-30 23:00:00-02:00");
        using HttpClient client = NewClient(new GatewayPathHandler(_app.GetTestServer().CreateHandler()));
        OrchestratorHydrationFetcher fetcher = new(client, new CapturingLogger());

        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", "321987", HydratedAt, default);

        Assert.Equal("resolved", row.HydrationStatus);
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 22, 30, 0, TimeSpan.Zero), row.FirstMessageAt);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 1, 0, 0, TimeSpan.Zero), row.LastMessageAt);
    }

    [Fact]
    public async Task SerializedSourceResponse_MalformedOptionalTimeRetainsBackedLink()
    {
        _fixture.AddStream(17, 9876, "implementers");
        _fixture.AddMessage(321987, 17, "implementers", "ballot", "unreadable source time");
        _fixture.AddMessage(321988, 17, "implementers", "ballot", "2026-05-01T10:00:00Z");
        using HttpClient client = NewClient(new GatewayPathHandler(_app.GetTestServer().CreateHandler()));
        CapturingLogger logger = new();
        OrchestratorHydrationFetcher fetcher = new(client, logger);

        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", "321987", HydratedAt, default);

        Assert.Equal("resolved", row.HydrationStatus);
        Assert.EndsWith("/near/321987", row.Url);
        Assert.Null(row.FirstMessageAt);
        Assert.Null(row.LastMessageAt);
        Assert.Equal(2, row.MessageCount);
        Assert.True(ZulipReferenceHydrationReason.Read(row.HydrationReason).HasSourceBacking);
        Assert.Equal([ZulipReferenceDiagnosticCode.InvalidTimestamp], ReadOutcome(row).Diagnostics);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("unreadable source time", StringComparison.Ordinal));

        using HttpResponseMessage response = await _sourceClient.GetAsync("/api/v1/threads?streamName=implementers&topic=ballot");
        JsonElement root = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("firstMessageAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastMessageAt").ValueKind);
        JsonElement invalid = Assert.Single(root.GetProperty("messages").EnumerateArray(), message => message.GetProperty("id").GetInt32() == 321987);
        Assert.Equal(JsonValueKind.Null, invalid.GetProperty("timestamp").ValueKind);
        Assert.Equal("invalid-timestamp", invalid.GetProperty("diagnostics")[0].GetString());
        JsonElement valid = Assert.Single(root.GetProperty("messages").EnumerateArray(), message => message.GetProperty("id").GetInt32() == 321988);
        Assert.Equal(TimeSpan.Zero, valid.GetProperty("timestamp").GetDateTimeOffset().Offset);
    }

    [Theory]
    [InlineData("999", HttpStatusCode.NotFound, ZulipReferenceLookupOutcome.NotFound)]
    [InlineData("implementers:unindexed", HttpStatusCode.NotFound, ZulipReferenceLookupOutcome.NotFound)]
    [InlineData("0", HttpStatusCode.BadRequest, ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData("unsupported", HttpStatusCode.BadRequest, ZulipReferenceLookupOutcome.UnsupportedReference)]
    [InlineData("a:b:c", HttpStatusCode.Conflict, ZulipReferenceLookupOutcome.AmbiguousReference)]
    public async Task SerializedSourceFailures_RemainExplicit(string reference, HttpStatusCode status, ZulipReferenceLookupOutcome expected)
    {
        _fixture.AddStream(1, 41, "a");
        _fixture.AddStream(2, 42, "a:b");
        _fixture.AddMessage(100, 1, "a", "b:c", "2026-05-01T10:00:00Z");
        _fixture.AddMessage(101, 2, "a:b", "c", "2026-05-01T10:00:00Z");
        using HttpResponseMessage source = await _sourceClient.GetAsync($"/api/v1/references/resolve?reference={Uri.EscapeDataString(reference)}");
        Assert.Equal(status, source.StatusCode);

        using HttpClient client = NewClient(new GatewayPathHandler(_app.GetTestServer().CreateHandler()));
        OrchestratorHydrationFetcher fetcher = new(client, new CapturingLogger());
        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", reference, HydratedAt, default);

        Assert.Equal(reference, row.ZulipThreadId);
        Assert.Equal("unresolved", row.HydrationStatus);
        Assert.Null(row.Url);
        Assert.Equal(expected, ReadOutcome(row).LatestOutcome);
        Assert.False(ZulipReferenceHydrationReason.Read(row.HydrationReason).HasSourceBacking);
    }

    [Fact]
    public async Task SerializedThreadResponse_UnindexedThreadStillHasLegacyZeroResult()
    {
        using HttpResponseMessage response = await _sourceClient.GetAsync("/api/v1/threads?streamName=unknown&topic=unindexed");
        JsonElement root = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, root.GetProperty("messageCount").GetInt32());
        Assert.Equal(0, root.GetProperty("total").GetInt32());
        Assert.Equal(0, root.GetProperty("messages").GetArrayLength());
        Assert.Equal("https://chat.example.com/#narrow/stream/unknown/topic/unindexed", root.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("firstMessageAt").ValueKind);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ZulipReferenceLookupOutcome.NotFound)]
    [InlineData(HttpStatusCode.Gone, ZulipReferenceLookupOutcome.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized, ZulipReferenceLookupOutcome.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, ZulipReferenceLookupOutcome.AuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, ZulipReferenceLookupOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ZulipReferenceLookupOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, ZulipReferenceLookupOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.BadGateway, ZulipReferenceLookupOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.BadRequest, ZulipReferenceLookupOutcome.InvalidReference)]
    [InlineData(HttpStatusCode.Conflict, ZulipReferenceLookupOutcome.HttpFailure)]
    public async Task ResolverFailures_RemainDistinctInHydration(HttpStatusCode status, ZulipReferenceLookupOutcome expected)
    {
        using ScriptedHandler handler = new((_, _) => JsonResponse(status, "<html>PRIVATE_RESPONSE_BODY</html>"));
        using HttpClient client = NewClient(handler);
        CapturingLogger logger = new();
        OrchestratorHydrationFetcher fetcher = new(client, logger);

        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", "private-stream:PRIVATE_TOPIC", HydratedAt, default);

        Assert.Equal("private-stream:PRIVATE_TOPIC", row.ZulipThreadId);
        Assert.Equal("unresolved", row.HydrationStatus);
        Assert.Equal(expected, ReadOutcome(row).LatestOutcome);
        Assert.Equal(ZulipReferenceBacking.None, ReadOutcome(row).Backing);
        Assert.Equal(HttpRetryHelper.IsTransient(status) ? HttpRetryHelper.DefaultMaxRetries + 1 : 1, handler.Attempts);
        Assert.Null(row.Url);
        Assert.Null(row.StreamName);
        Assert.Single(logger.Messages);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("PRIVATE_", StringComparison.Ordinal));
        Assert.DoesNotContain("PRIVATE_", row.HydrationReason);
    }

    [Fact]
    public async Task ResolverSourceUnavailable_PreservesTypedFailureAfterRetries()
    {
        string json = JsonSerializer.Serialize(new ZulipReferenceResolutionResponse
        {
            Reference = "321987",
            Outcome = ZulipReferenceLookupOutcome.SourceUnavailable,
        }, JsonOptions);
        using ScriptedHandler handler = new((_, _) => JsonResponse(HttpStatusCode.ServiceUnavailable, json));
        using HttpClient client = NewClient(handler);
        OrchestratorHydrationFetcher fetcher = new(client, new CapturingLogger());

        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", "321987", HydratedAt, default);

        Assert.Equal(ZulipReferenceLookupOutcome.SourceUnavailable, ReadOutcome(row).LatestOutcome);
        Assert.Equal(HttpRetryHelper.DefaultMaxRetries + 1, handler.Attempts);
    }

    [Fact]
    public async Task ResolverTransientFailure_RetriesThenAcceptsValidResolution()
    {
        using ScriptedHandler handler = new((attempt, _) => attempt < 3
            ? JsonResponse(HttpStatusCode.ServiceUnavailable, "{}")
            : JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(ValidResolution(), JsonOptions)));
        using HttpClient client = NewClient(handler);
        OrchestratorHydrationFetcher fetcher = new(client, new CapturingLogger());

        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", "321987", HydratedAt, default);

        Assert.Equal("resolved", row.HydrationStatus);
        Assert.Equal(3, handler.Attempts);
        Assert.True(ZulipReferenceHydrationReason.Read(row.HydrationReason).HasSourceBacking);
    }

    public static IEnumerable<object[]> InvalidEnvelopes()
    {
        yield return ["null", "null"];
        yield return ["empty", "{}"];
        yield return ["no body", ""];
        ZulipReferenceResolutionResponse valid = ValidResolution();
        (string Label, ZulipReferenceResolutionResponse Response)[] cases =
        [
            ("missing outcome", valid with { Outcome = null }),
            ("mismatched reference", valid with { Reference = "000321987" }),
            ("missing reference", valid with { Reference = null }),
            ("wrong kind", valid with { Kind = ZulipReferenceKind.Thread }),
            ("missing kind", valid with { Kind = null }),
            ("wrong message", valid with { MessageId = 123 }),
            ("missing message", valid with { MessageId = null }),
            ("missing stream", valid with { StreamName = null }),
            ("missing topic", valid with { Topic = null }),
            ("invalid stream ID", valid with { StreamId = 0 }),
            ("zero indexed messages", valid with { MessageCount = 0 }),
            ("negative count", valid with { MessageCount = -1 }),
            ("reversed times", valid with { FirstMessageAt = HydratedAt.AddDays(1), LastMessageAt = HydratedAt }),
            ("unsafe URL", valid with { Url = "javascript:alert(1)" }),
            ("credentials URL", valid with { Url = valid.Url!.Replace("https://", "https://user:secret@", StringComparison.Ordinal) }),
            ("wrong URL context", valid with { Url = valid.Url!.Replace("/ballot/", "/different/", StringComparison.Ordinal) }),
            ("wrong near ID", valid with { Url = valid.Url!.Replace("/near/321987", "/near/1", StringComparison.Ordinal) }),
            ("missing URL", valid with { Url = null }),
            ("failure on success status", valid with { Outcome = ZulipReferenceLookupOutcome.NotFound }),
            ("incoherent diagnostics", valid with { Diagnostics = [ZulipReferenceDiagnosticCode.MissingStreamContext] }),
        ];
        foreach ((string label, ZulipReferenceResolutionResponse response) in cases)
            yield return [label, JsonSerializer.Serialize(response, JsonOptions)];
        yield return ["null diagnostics", JsonSerializer.Serialize(valid, JsonOptions).Replace("\"diagnostics\":[]", "\"diagnostics\":null", StringComparison.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(InvalidEnvelopes))]
    public async Task ResolverInvalidEnvelope_IsNotSuccess(string label, string json)
    {
        _ = label;
        using ScriptedHandler handler = new((_, _) => JsonResponse(HttpStatusCode.OK, json));
        using HttpClient client = NewClient(handler);
        OrchestratorHydrationFetcher fetcher = new(client, new CapturingLogger());

        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", "321987", HydratedAt, default);

        Assert.Equal("321987", row.ZulipThreadId);
        Assert.Equal("unresolved", row.HydrationStatus);
        Assert.Equal(ZulipReferenceLookupOutcome.InvalidEnvelope, ReadOutcome(row).LatestOutcome);
        Assert.False(ZulipReferenceHydrationReason.Read(row.HydrationReason).HasSourceBacking);
        Assert.Null(row.Url);
    }

    [Theory]
    [InlineData("{ PRIVATE_RESPONSE_BODY")]
    [InlineData("""{"reference":"321987","kind":"unknown"}""")]
    [InlineData("""{"reference":"321987","firstMessageAt":"2026-05-01 10:00:00+00:00"}""")]
    public async Task ResolverInvalidJson_IsDifferentFromInvalidEnvelope(string json)
    {
        using ScriptedHandler handler = new((_, _) => JsonResponse(HttpStatusCode.OK, json));
        using HttpClient client = NewClient(handler);
        CapturingLogger logger = new();
        OrchestratorHydrationFetcher fetcher = new(client, logger);

        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", "321987", HydratedAt, default);

        Assert.Equal("unresolved", row.HydrationStatus);
        Assert.Equal(ZulipReferenceLookupOutcome.InvalidJson, ReadOutcome(row).LatestOutcome);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(json, StringComparison.Ordinal));
        Assert.DoesNotContain("PRIVATE_", row.HydrationReason);
    }

    [Theory]
    [InlineData(false, ZulipReferenceLookupOutcome.SourceUnavailable)]
    [InlineData(true, ZulipReferenceLookupOutcome.Timeout)]
    public async Task ResolverOperationalExceptions_AreSpecificAndSafe(bool timeout, ZulipReferenceLookupOutcome expected)
    {
        using ScriptedHandler handler = new((_, _) =>
        {
            if (timeout) throw new TaskCanceledException("PRIVATE_TIMEOUT");
            throw new HttpRequestException("PRIVATE_NETWORK_FAILURE");
        });
        using HttpClient client = NewClient(handler);
        CapturingLogger logger = new();
        OrchestratorHydrationFetcher fetcher = new(client, logger);

        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", "321987", HydratedAt, default);

        Assert.Equal("unresolved", row.HydrationStatus);
        Assert.Equal(expected, ReadOutcome(row).LatestOutcome);
        Assert.Equal(HttpRetryHelper.DefaultMaxRetries + 1, handler.Attempts);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolverCallerCancellation_PropagatesBeforeAndDuringRetry(bool duringRequest)
    {
        using CancellationTokenSource cancellation = new();
        if (!duringRequest) cancellation.Cancel();
        using ScriptedHandler handler = new((_, _) =>
        {
            cancellation.Cancel();
            return JsonResponse(HttpStatusCode.ServiceUnavailable, "{}");
        });
        using HttpClient client = NewClient(handler);
        CapturingLogger logger = new();
        OrchestratorHydrationFetcher fetcher = new(client, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fetcher.FetchZulipAsync("FHIR-100", "321987", HydratedAt, cancellation.Token));

        Assert.Equal(duringRequest ? 1 : 0, handler.Attempts);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task ResolverProgrammingErrors_AreNotUnavailableSourceFallbacks()
    {
        using ScriptedHandler handler = new((_, _) => throw new InvalidOperationException("test programming failure"));
        using HttpClient client = NewClient(handler);
        CapturingLogger logger = new();
        OrchestratorHydrationFetcher fetcher = new(client, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.FetchZulipAsync("FHIR-100", "321987", HydratedAt, default));

        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task HydrationReason_SuccessiveFailuresPreserveBackingWithoutAggregates()
    {
        ZulipReferenceResolutionResponse response = ValidResolution() with
        {
            StreamId = null,
            MessageCount = null,
            FirstMessageAt = null,
            LastMessageAt = null,
            FirstMessageExcerpt = null,
        };
        using ScriptedHandler handler = new((_, _) => JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(response, JsonOptions)));
        using HttpClient client = NewClient(handler);
        OrchestratorHydrationFetcher fetcher = new(client, new CapturingLogger());
        HydrationZulipRow row = await fetcher.FetchZulipAsync("FHIR-100", "321987", HydratedAt, default);

        Assert.Equal("resolved", row.HydrationStatus);
        Assert.Null(row.MessageCount);
        Assert.StartsWith(ZulipReferenceHydrationReason.Prefix, row.HydrationReason);
        ZulipReferenceHydrationOutcome metadata = ReadOutcome(row);
        foreach (ZulipReferenceLookupOutcome failure in new[]
        {
            ZulipReferenceLookupOutcome.NotFound,
            ZulipReferenceLookupOutcome.Timeout,
            ZulipReferenceLookupOutcome.InvalidJson,
        })
        {
            // The later enrichment stage can replace the outcome without
            // deriving backing again from optional aggregates or status text.
            metadata = metadata with { LatestOutcome = failure, Diagnostics = [] };
            ZulipReferenceHydrationReasonReadResult read =
                ZulipReferenceHydrationReason.Read(ZulipReferenceHydrationReason.Serialize(metadata));
            Assert.True(read.HasSourceBacking);
            Assert.Equal(ZulipReferenceBacking.TypedResolver, read.Metadata!.Backing);
            Assert.Equal(failure, read.Metadata.LatestOutcome);
            metadata = read.Metadata;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed thread id")]
    [InlineData("orchestrator 404")]
    [InlineData("resolved")]
    public void HydrationReason_LegacyTextRemainsReadableButDoesNotClaimBacking(string? reason)
    {
        ZulipReferenceHydrationReasonReadResult read = ZulipReferenceHydrationReason.Read(reason);

        Assert.Equal(reason, read.LegacyReason);
        Assert.Null(read.Metadata);
        Assert.Null(read.MetadataFailure);
        Assert.False(read.HasSourceBacking);
    }

    [Theory]
    [InlineData("zulip-reference-v2:{}", ZulipReferenceDiagnosticCode.UnknownOutcomeMetadataVersion)]
    [InlineData("zulip-reference-v1", ZulipReferenceDiagnosticCode.UnknownOutcomeMetadataVersion)]
    [InlineData("zulip-reference-v1:", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("zulip-reference-v1:{", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("zulip-reference-v1:null", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("zulip-reference-v1:{}", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("""zulip-reference-v1:{"backing":"typed-resolver"}""", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("""zulip-reference-v1:{"backing":"typed-resolver","latestOutcome":"resolved","diagnostics":null}""", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("""zulip-reference-v1:{"backing":"future","latestOutcome":"resolved","diagnostics":[]}""", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("""zulip-reference-v1:{"backing":"typed-resolver","latestOutcome":"future","diagnostics":[]}""", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("""zulip-reference-v1:{"backing":"typed-resolver","latestOutcome":"resolved","diagnostics":["future"]}""", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("""zulip-reference-v1:{"backing":"typed-resolver","latestOutcome":0,"diagnostics":[]}""", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("""zulip-reference-v1:{"backing":"none","backing":"typed-resolver","latestOutcome":"resolved","diagnostics":[]}""", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("""zulip-reference-v1:{"backing":"typed-resolver","latestOutcome":"resolved","diagnostics":[],"unknown":true}""", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    [InlineData("""zulip-reference-v1:{"backing":"none","latestOutcome":"resolved","diagnostics":[]}""", ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata)]
    public void HydrationReason_InvalidOrUnknownTaggedMetadataCannotClaimBacking(string reason, ZulipReferenceDiagnosticCode expected)
    {
        ZulipReferenceHydrationReasonReadResult read = ZulipReferenceHydrationReason.Read(reason);

        Assert.Null(read.Metadata);
        Assert.Null(read.LegacyReason);
        Assert.Equal(expected, read.MetadataFailure);
        Assert.False(read.HasSourceBacking);
    }

    private static ZulipReferenceResolutionResponse ValidResolution() => new()
    {
        Reference = "321987",
        Outcome = ZulipReferenceLookupOutcome.Resolved,
        Kind = ZulipReferenceKind.Message,
        MessageId = 321987,
        StreamId = 42,
        StreamName = "implementers",
        Topic = "ballot",
        Url = "https://chat.example.com/#narrow/stream/implementers/topic/ballot/near/321987",
        MessageCount = 3,
        FirstMessageAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        LastMessageAt = new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
        FirstMessageExcerpt = "body",
    };

    private static ZulipReferenceHydrationOutcome ReadOutcome(HydrationZulipRow row) =>
        Assert.IsType<ZulipReferenceHydrationOutcome>(ZulipReferenceHydrationReason.Read(row.HydrationReason).Metadata);

    private static HttpClient NewClient(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://orchestrator/") };

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
    {
        HttpResponseMessage response = new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed class GatewayPathHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = Assert.IsType<Uri>(request.RequestUri);
            Requests.Add(uri.PathAndQuery);
            Assert.StartsWith("/api/v1/zulip/references/resolve?", uri.PathAndQuery);
            request.RequestUri = new Uri(uri.GetLeftPart(UriPartial.Authority)
                + uri.PathAndQuery.Replace("/api/v1/zulip/", "/api/v1/", StringComparison.Ordinal));
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ScriptedHandler(Func<int, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(++Attempts, cancellationToken));
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
