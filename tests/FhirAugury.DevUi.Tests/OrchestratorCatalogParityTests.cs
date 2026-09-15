using System.Collections.Generic;
using System.Text.Json;
using FhirAugury.DevUi.Services;
using FhirAugury.DevUi.Services.ApiCatalog;
using FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

namespace FhirAugury.DevUi.Tests;

/// <summary>
/// URL parity: confirm <see cref="ApiUrlBuilder"/> output for the existing
/// orchestrator endpoints matches the hand-built URL strings used by
/// <c>OrchestratorClient</c> today, byte-for-byte. Protects against
/// behavioural regressions for callers other than <c>ApiTest.razor</c>.
/// </summary>
public class OrchestratorCatalogParityTests
{
    private const string Base = "http://orchestrator:5150";

    [Fact]
    public void Search_url_matches_legacy()
    {
        ApiEndpointDescriptor d = Find("content.search");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["values"] = "patient resource, FHIR-50783",
            ["limit"] = "20",
        });

        // Legacy: $"{Address}/api/v1/content/search?{valuesQuery}&limit={limit}"
        // where values are repeated query params, EscapeDataString.
        Assert.Equal(
            $"{Base}/api/v1/content/search?values=patient%20resource&values=FHIR-50783&limit=20",
            r.Url);
    }

    [Fact]
    public void RefersTo_url_matches_legacy()
    {
        ApiEndpointDescriptor d = Find("content.refers-to");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["value"] = "FHIR-50783",
            ["sourceType"] = "jira",
            ["limit"] = "20",
        });

        Assert.Equal($"{Base}/api/v1/content/refers-to?value=FHIR-50783&sourceType=jira&limit=20", r.Url);
    }

    [Fact]
    public void GetItem_url_matches_legacy()
    {
        ApiEndpointDescriptor d = Find("content.get-item");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["source"] = "jira",
            ["id"] = "FHIR-55001",
            ["includeContent"] = "true",
            ["includeComments"] = "false",
            ["includeSnapshot"] = "false",
        });

        Assert.Equal(
            $"{Base}/api/v1/content/item/jira/FHIR-55001?includeContent=true&includeComments=false&includeSnapshot=false",
            r.Url);
    }

    [Fact]
    public void RebuildIndex_url_matches_legacy()
    {
        ApiEndpointDescriptor d = Find("ingestion.rebuild-index");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["type"] = "all",
        });
        Assert.Equal($"{Base}/api/v1/rebuild-index?type=all", r.Url);
    }

    [Fact]
    public void TriggerSync_url_matches_legacy()
    {
        ApiEndpointDescriptor d = Find("ingestion.trigger");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["type"] = "incremental",
        });
        Assert.Equal($"{Base}/api/v1/ingest/trigger?type=incremental", r.Url);
    }

    [Fact]
    public void Lifecycle_health_url_matches_orchestrator_route()
    {
        ApiEndpointDescriptor d = Find("lifecycle.health");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>());

        Assert.Equal("Lifecycle", d.Group);
        Assert.Equal($"{Base}/api/v1/health", r.Url);
    }

    [Fact]
    public void Lifecycle_status_url_matches_orchestrator_route()
    {
        ApiEndpointDescriptor d = Find("lifecycle.status");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>());

        Assert.Equal("Lifecycle", d.Group);
        Assert.Equal($"{Base}/api/v1/status", r.Url);
    }

    [Fact]
    public void Source_search_fixes_selected_source_on_native_gateway_route()
    {
        ApiEndpointDescriptor d = FindSource("jira", "content.search");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["values"] = "FHIR-55001",
            ["limit"] = "20",
        });

        Assert.DoesNotContain(d.Parameters, parameter => parameter.Name == "sources");
        Assert.Equal(
            $"{Base}/api/v1/content/search?sources=jira&values=FHIR-55001&limit=20",
            r.Url);
    }

    [Fact]
    public void Source_item_fixes_selected_source_on_native_gateway_route()
    {
        ApiEndpointDescriptor d = FindSource("github", "content.get-item");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["id"] = "HL7/fhir#4006",
            ["includeContent"] = "true",
            ["includeComments"] = "false",
            ["includeSnapshot"] = "false",
        });

        Assert.DoesNotContain(d.Parameters, parameter => parameter.Name == "source");
        Assert.Equal(
            $"{Base}/api/v1/content/item/github/HL7/fhir%234006?includeContent=true&includeComments=false&includeSnapshot=false",
            r.Url);
    }

    [Fact]
    public void Source_reference_query_uses_typed_proxy_without_overwriting_source_type()
    {
        ApiEndpointDescriptor d = FindSource("jira", "content.refers-to");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["value"] = "FHIR-55001",
            ["sourceType"] = "github",
            ["limit"] = "20",
        });

        Assert.Equal(
            $"{Base}/api/v1/jira/content/refers-to?value=FHIR-55001&sourceType=github&limit=20",
            r.Url);
    }

    [Fact]
    public void Source_related_by_keyword_uses_typed_proxy_with_fixed_source()
    {
        ApiEndpointDescriptor d = FindSource("github", "content.related-by-keyword");
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["id"] = "HL7/fhir#4006",
            ["minScore"] = "0.1",
            ["limit"] = "20",
        });

        Assert.DoesNotContain(d.Parameters, parameter => parameter.Name == "source");
        Assert.Equal(
            $"{Base}/api/v1/github/content/related-by-keyword/github/HL7/fhir%234006?minScore=0.1&limit=20",
            r.Url);
    }

    [Theory]
    [InlineData("lifecycle.health", "api/v1/services")]
    [InlineData("lifecycle.status", "api/v1/services")]
    [InlineData("lifecycle.stats", "api/v1/stats")]
    public void Source_lifecycle_uses_typed_gateway_replacement(string id, string expectedPath)
    {
        ApiEndpointDescriptor d = FindSource("zulip", id);
        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>());

        Assert.Equal($"{Base}/{expectedPath}", r.Url);
    }

    [Fact]
    public void Service_refresh_and_authoring_list_routes_are_catalogued()
    {
        ApiBuiltRequest refresh = ApiUrlBuilder.Build(
            Base,
            Find("services.refresh"),
            new Dictionary<string, string?>());
        ApiBuiltRequest runs = ApiUrlBuilder.Build(
            Base,
            Find("processing.authoring.list"),
            new Dictionary<string, string?>
            {
                ["name"] = "Preparer",
                ["limit"] = "20",
            });

        Assert.Equal(HttpMethod.Post, refresh.Method);
        Assert.Equal($"{Base}/api/v1/services/refresh", refresh.Url);
        Assert.Equal(HttpMethod.Get, runs.Method);
        Assert.Equal(
            $"{Base}/api/v1/processing-services/Preparer/authoring/runs?limit=20",
            runs.Url);
    }

    [Fact]
    public void OpenApi_lookup_ignores_fixed_query_on_gateway_descriptor()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "paths": {
                "/api/v1/content/search": {
                  "get": {
                    "operationId": "content-search"
                  }
                },
                "/api/v1/content/item/{source}/{id}": {
                  "get": {
                    "operationId": "content-item"
                  }
                }
              }
            }
            """);

        OpenApiOperationInfo? operation = OpenApiCatalogClient.FindOperation(
            document,
            FindSource("jira", "content.search").PathTemplate,
            HttpMethod.Get);

        Assert.NotNull(operation);
        Assert.Equal("content-search", operation!.OperationId);

        OpenApiOperationInfo? itemOperation = OpenApiCatalogClient.FindOperation(
            document,
            FindSource("jira", "content.get-item").PathTemplate,
            HttpMethod.Get);

        Assert.NotNull(itemOperation);
        Assert.Equal("content-item", itemOperation!.OperationId);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("apply")]
    public void Jira_public_people_maintenance_uses_gateway_and_documents_deliberate_prerequisites(string operation)
    {
        ApiEndpointDescriptor descriptor = FindSource("jira", $"public-people.{operation}");
        string body = operation == "preview"
            ? """{"keys":["FHIR-1"],"evidenceMode":"cache-only"}"""
            : """{"previewToken":"opaque-token","acknowledgeSharedUserImpact":true}""";
        ApiBuiltRequest request = ApiUrlBuilder.Build(Base, descriptor, new Dictionary<string, string?> { ["body"] = body });
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{Base}/api/v1/jira/public-people/{operation}", request.Url);
        Assert.Equal(operation == "apply", descriptor.Destructive);
        Assert.Contains(operation == "preview" ? "authentication" : "fresh preview", descriptor.Description!);
        Assert.Single(descriptor.Parameters);
        Assert.Equal(ApiParameterKind.Body, descriptor.Parameters[0].Kind);
        if (operation == "preview")
        {
            Assert.Contains("cache-only", descriptor.Parameters[0].DefaultValue);
            Assert.Contains("never fetches", descriptor.Parameters[0].HelpText);
        }
    }

    private static ApiEndpointDescriptor Find(string id) =>
        OrchestratorCatalog.Build().Single(e => e.Id == id);

    private static ApiEndpointDescriptor FindSource(string source, string id) =>
        SourceApiCatalog.GetCatalog(source).Single(endpoint => endpoint.Id == id);
}
