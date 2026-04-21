using System.Collections.Generic;
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

    private static ApiEndpointDescriptor Find(string id) =>
        OrchestratorCatalog.Build().Single(e => e.Id == id);
}
