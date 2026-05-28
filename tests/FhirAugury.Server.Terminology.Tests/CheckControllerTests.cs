using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FhirAugury.Server.Terminology;
using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Database;
using FhirAugury.Server.Terminology.Database.Records;
using FhirAugury.Server.Terminology.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FhirAugury.Server.Terminology.Tests;

/// <summary>
/// End-to-end tests for <c>POST /api/v1/terminology/check</c>. Uses a
/// real <see cref="TerminologyDatabase"/> seeded with a tiny THO-shaped
/// fixture so the lexical matcher has something to score against.
/// </summary>
public class CheckControllerTests : IClassFixture<CheckTestFactory>
{
    private readonly CheckTestFactory _factory;

    public CheckControllerTests(CheckTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Check_ReturnsRankedCandidates_ForOverlappingCodeSystem()
    {
        using HttpClient client = _factory.CreateClient();

        string fhir = """
            {
              "resourceType": "CodeSystem",
              "url": "http://example.org/cs/marital",
              "title": "My Marital Status",
              "concept": [
                {"code": "M", "display": "Married"},
                {"code": "S", "display": "Single"}
              ]
            }
            """;

        HttpResponseMessage response = await PostFhir(client,
            "/api/v1/terminology/check?limit=5&minScore=0.0", fhir);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        JsonElement candidates = body.GetProperty("candidates");
        Assert.True(candidates.GetArrayLength() > 0);
        Assert.Equal(
            "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus",
            candidates[0].GetProperty("canonicalUrl").GetString());

        JsonElement summary = body.GetProperty("summary");
        Assert.Equal("CodeSystem", summary.GetProperty("submissionKind").GetString());
        Assert.Equal("lexical", summary.GetProperty("mode").GetString());
        Assert.Equal(5, summary.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Check_AppliesDefaults_WhenQueryParamsOmitted()
    {
        using HttpClient client = _factory.CreateClient();

        string fhir = """
            {"resourceType": "CodeSystem", "url": "http://example.org/cs/empty"}
            """;

        HttpResponseMessage response = await PostFhir(client, "/api/v1/terminology/check", fhir);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement summary = body.GetProperty("summary");
        Assert.Equal("lexical", summary.GetProperty("mode").GetString());
        Assert.Equal(10, summary.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Check_Returns415_ForNonFhirContentType()
    {
        using HttpClient client = _factory.CreateClient();
        StringContent content = new("not fhir", Encoding.UTF8, "text/plain");
        HttpResponseMessage response = await client.PostAsync("/api/v1/terminology/check", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Check_Returns400_ForInvalidFhirResource()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await PostFhir(client, "/api/v1/terminology/check",
            """{"resourceType": "Patient", "id": "x"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_fhir_resource", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Check_Returns400_ForEmptyBody()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await PostFhir(client, "/api/v1/terminology/check", "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("empty_body", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Check_Returns400_WhenEmbeddingsRequested_AndDisabled()
    {
        using HttpClient client = _factory.CreateClient();
        string fhir = """{"resourceType": "CodeSystem", "url": "http://example.org/cs/anything"}""";

        HttpResponseMessage response = await PostFhir(client,
            "/api/v1/terminology/check?mode=embeddings", fhir);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mode_unavailable", body.GetProperty("error").GetString());
        Assert.Equal("embeddings", body.GetProperty("requested").GetString());

        string[] enabled = body.GetProperty("enabled_modes")
            .EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToArray();
        Assert.Single(enabled);
        Assert.Equal("lexical", enabled[0]);
    }

    [Fact]
    public async Task Check_Returns413_WhenSubmissionExceedsCap()
    {
        using HttpClient client = _factory.CreateClient();

        // Factory pins MaxSubmissionConcepts to 5.
        StringBuilder concepts = new();
        for (int i = 0; i < 50; i++)
        {
            if (i > 0) concepts.Append(',');
            concepts.Append($"{{\"code\":\"c{i}\",\"display\":\"d{i}\"}}");
        }
        string fhir = $$"""
            {"resourceType":"CodeSystem","url":"http://example.org/cs/big","concept":[{{concepts}}]}
            """;

        HttpResponseMessage response = await PostFhir(client, "/api/v1/terminology/check", fhir);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("too_many_concepts", body.GetProperty("error").GetString());
        Assert.Equal(5, body.GetProperty("cap").GetInt32());
        Assert.True(body.GetProperty("submitted").GetInt32() > 5);
    }

    private static Task<HttpResponseMessage> PostFhir(HttpClient client, string url, string json)
    {
        StringContent content = new(json, Encoding.UTF8, "application/fhir+json");
        return client.PostAsync(url, content);
    }
}

/// <summary>
/// Factory for the Check endpoint tests: temp DB + cache + tiny THO-
/// shaped seed inserted post-startup; THO download hosted service
/// removed; <c>MaxSubmissionConcepts</c> pinned low to exercise 413.
/// </summary>
public sealed class CheckTestFactory : WebApplicationFactory<Program>
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "augury-terminology-check-tests-" + Guid.NewGuid().ToString("N"));

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Terminology:DatabasePath"] = Path.Combine(_tempRoot, "terminology.sqlite"),
                ["Terminology:CachePath"] = Path.Combine(_tempRoot, "fhir-cache"),
                ["Terminology:Ports:Http"] = "5300",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.PostConfigure<TerminologyServiceOptions>(o =>
            {
                o.Packages = [new PackageOptions
                {
                    PackageId = "hl7.terminology.r4",
                    FhirVersion = "R4",
                    VersionTag = "latest",
                }];
                o.MaxSubmissionConcepts = 5;
            });

            // Drop the THO-downloading hosted service so we never reach the wire.
            ServiceDescriptor[] hostedToRemove = services
                .Where(sd => sd.ServiceType == typeof(IHostedService)
                    && sd.ImplementationFactory is not null
                    && sd.ImplementationFactory.Method.ReturnType == typeof(TerminologyStartupRebuildService))
                .ToArray();
            foreach (ServiceDescriptor sd in hostedToRemove) services.Remove(sd);
        });

        IHost host = base.CreateHost(builder);
        SeedFixtures(host);
        return host;
    }

    private static void SeedFixtures(IHost host)
    {
        TerminologyDatabase db = host.Services.GetRequiredService<TerminologyDatabase>();
        using SqliteConnection conn = db.OpenConnection();

        TerminologyPackageRecord.Insert(conn, new TerminologyPackageRecord
        {
            Id = TerminologyPackageRecord.GetIndex(),
            PackageId = "hl7.terminology.r4",
            RequestedVersionTag = "latest",
            ResolvedVersion = "5.4.0",
            FhirVersion = "R4",
            IngestedAt = DateTimeOffset.UtcNow,
            ArtifactCount = 1,
            ConceptCount = 4,
        }, insertPrimaryKey: true);

        TerminologyArtifactRecord marital = new()
        {
            Id = TerminologyArtifactRecord.GetIndex(),
            Kind = "CodeSystem",
            CanonicalUrl = "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus",
            CanonicalUrlNormalized = TerminologyTextNormalizer.NormalizeCanonicalUrl(
                "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus"),
            Version = "5.4.0",
            FhirVersion = "R4",
            Title = "V3 Marital Status",
            Name = "MaritalStatus",
            Status = "Active",
            Experimental = false,
            Publisher = "HL7",
            Description = "Standardized marital status codes.",
            Purpose = null,
            Keywords = null,
            PackageId = "hl7.terminology.r4",
            PackageVersion = "5.4.0",
            Json = "{}",
        };
        TerminologyArtifactRecord.Insert(conn, marital, insertPrimaryKey: true);

        List<TerminologyConceptRecord> concepts =
        [
            NewConcept(marital.Id, "M", "Married"),
            NewConcept(marital.Id, "S", "Single"),
            NewConcept(marital.Id, "D", "Divorced"),
            NewConcept(marital.Id, "W", "Widowed"),
        ];
        concepts.Insert(conn, ignoreDuplicates: false, insertPrimaryKey: true);
    }

    private static TerminologyConceptRecord NewConcept(int artifactId, string code, string display)
    {
        return new TerminologyConceptRecord
        {
            Id = TerminologyConceptRecord.GetIndex(),
            ArtifactId = artifactId,
            SystemUrl = "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus",
            Code = code,
            Display = display,
            DisplayNormalized = TerminologyTextNormalizer.NormalizeDisplay(display),
            Definition = null,
            DesignationsJson = "[]",
            IsRetired = false,
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
