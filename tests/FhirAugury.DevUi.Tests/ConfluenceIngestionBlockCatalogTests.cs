using System.Collections.Generic;
using System.Net.Http;
using FhirAugury.DevUi.Services.ApiCatalog;
using FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

namespace FhirAugury.DevUi.Tests;

/// <summary>
/// Pins the descriptor metadata for the Confluence-only ingestion-block routes
/// that <see cref="CatalogCoverageTests"/> cannot see: that test compares method
/// and path only, so a typo in an id, group, or parameter name would render a
/// dead form field in the API tester without failing anything.
/// </summary>
public class ConfluenceIngestionBlockCatalogTests
{
    private const string Base = "http://confluence:5180";

    [Fact]
    public void Confluence_catalog_declares_ingestion_block_state()
    {
        ApiEndpointDescriptor d = FindConfluence("ingestion.block");

        Assert.Equal("Ingestion", d.Group);
        Assert.Equal(HttpMethod.Get, d.Method);
        Assert.Equal("api/v1/ingestion-block", d.PathTemplate);
        Assert.Empty(d.Parameters);
        Assert.False(d.Destructive);
    }

    [Fact]
    public void Confluence_catalog_declares_ingestion_block_clear()
    {
        ApiEndpointDescriptor d = FindConfluence("ingestion.block-clear");

        Assert.Equal(HttpMethod.Post, d.Method);
        Assert.Equal("api/v1/ingestion-block/clear", d.PathTemplate);
        Assert.False(d.Destructive);

        ApiParameter p = Assert.Single(d.Parameters);
        Assert.Equal("clearedBy", p.Name);
        Assert.Equal(ApiParameterKind.Query, p.Kind);
        Assert.False(p.Required);
    }

    [Fact]
    public void Clear_url_carries_clearedBy_query_parameter()
    {
        ApiEndpointDescriptor d = FindConfluence("ingestion.block-clear");

        ApiBuiltRequest withOperator = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["clearedBy"] = "gino",
        });

        Assert.Equal($"{Base}/api/v1/ingestion-block/clear?clearedBy=gino", withOperator.Url);

        // clearedBy is optional and carries no default, so omitting it must not
        // leave a dangling '?' on the URL.
        ApiBuiltRequest withoutOperator = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>());

        Assert.Equal($"{Base}/api/v1/ingestion-block/clear", withoutOperator.Url);
    }

    [Fact]
    public void Orchestrator_catalog_projects_both_ingestion_block_routes()
    {
        ApiEndpointDescriptor state = FindOrchestrator("confluence.ingestion.block");
        ApiEndpointDescriptor clear = FindOrchestrator("confluence.ingestion.block-clear");

        Assert.Equal("api/v1/confluence/ingestion-block", state.PathTemplate);
        Assert.Equal("api/v1/confluence/ingestion-block/clear", clear.PathTemplate);
        Assert.Equal("Confluence / Ingestion", state.Group);
        Assert.Equal("Confluence / Ingestion", clear.Group);
    }

    private static ApiEndpointDescriptor FindConfluence(string id) =>
        ConfluenceCatalog.Build().Single(e => e.Id == id);

    private static ApiEndpointDescriptor FindOrchestrator(string id) =>
        OrchestratorCatalog.Build().Single(e => e.Id == id);
}
