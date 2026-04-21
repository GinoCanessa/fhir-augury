using System.Collections.Generic;
using System.Net.Http;
using FhirAugury.DevUi.Services.ApiCatalog;

namespace FhirAugury.DevUi.Tests;

public class ApiUrlBuilderTests
{
    private const string Base = "http://localhost:5150";

    [Fact]
    public void Build_simple_get_with_query()
    {
        ApiEndpointDescriptor d = new(
            "x", "X", "G", HttpMethod.Get, "api/v1/things",
            [
                new ApiParameter("a", ApiParameterKind.Query, Required: true),
                new ApiParameter("b", ApiParameterKind.Query, Required: false),
            ]);

        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["a"] = "hello world",
        });

        Assert.Equal("http://localhost:5150/api/v1/things?a=hello%20world", r.Url);
        Assert.Equal(HttpMethod.Get, r.Method);
        Assert.Null(r.JsonBody);
    }

    [Fact]
    public void Build_substitutes_path_tokens()
    {
        ApiEndpointDescriptor d = new(
            "x", "X", "G", HttpMethod.Get, "api/v1/items/{key}/related",
            [new ApiParameter("key", ApiParameterKind.Path, Required: true)]);

        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["key"] = "FHIR-55001",
        });

        Assert.Equal("http://localhost:5150/api/v1/items/FHIR-55001/related", r.Url);
    }

    [Fact]
    public void Build_catchall_preserves_slashes_and_encodes_segments()
    {
        ApiEndpointDescriptor d = new(
            "x", "X", "G", HttpMethod.Get, "api/v1/items/{*key}",
            [new ApiParameter("key", ApiParameterKind.Path, Required: true, IsCatchAll: true)]);

        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["key"] = "owner/repo with space/sub",
        });

        Assert.Equal("http://localhost:5150/api/v1/items/owner/repo%20with%20space/sub", r.Url);
    }

    [Fact]
    public void Build_github_id_encodes_hash_and_keeps_slash()
    {
        ApiEndpointDescriptor d = new(
            "x", "X", "G", HttpMethod.Get, "api/v1/items/{*key}",
            [new ApiParameter("key", ApiParameterKind.Path, Required: true,
                Encoding: ApiEncoding.IdSlashPreserving, IsCatchAll: true)]);

        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["key"] = "HL7/fhir#4006",
        });

        Assert.Equal("http://localhost:5150/api/v1/items/HL7/fhir%234006", r.Url);
    }

    [Fact]
    public void Build_omits_optional_query_when_empty()
    {
        ApiEndpointDescriptor d = new(
            "x", "X", "G", HttpMethod.Get, "api/v1/things",
            [
                new ApiParameter("a", ApiParameterKind.Query, Required: true),
                new ApiParameter("b", ApiParameterKind.Query, Required: false),
            ]);

        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["a"] = "1",
            ["b"] = "",
        });

        Assert.Equal("http://localhost:5150/api/v1/things?a=1", r.Url);
    }

    [Fact]
    public void Build_repeats_repeatable_query_param()
    {
        ApiEndpointDescriptor d = new(
            "x", "X", "G", HttpMethod.Get, "api/v1/content/search",
            [
                new ApiParameter("values", ApiParameterKind.Query, Required: true, Repeatable: true),
            ]);

        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["values"] = "patient,observation,encounter",
        });

        Assert.Equal("http://localhost:5150/api/v1/content/search?values=patient&values=observation&values=encounter", r.Url);
    }

    [Fact]
    public void Build_post_with_json_body()
    {
        ApiEndpointDescriptor d = new(
            "x", "X", "G", HttpMethod.Post, "api/v1/query",
            [new ApiParameter("body", ApiParameterKind.Body, Required: true,
                ValueType: ApiParameterValueType.Json)]);

        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>
        {
            ["body"] = "{\"a\":1}",
        });

        Assert.Equal("http://localhost:5150/api/v1/query", r.Url);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("{\"a\":1}", r.JsonBody);
    }

    [Fact]
    public void Build_post_without_body()
    {
        ApiEndpointDescriptor d = new(
            "x", "X", "G", HttpMethod.Post, "api/v1/rebuild-index",
            [new ApiParameter("type", ApiParameterKind.Query, Required: false, DefaultValue: "all")]);

        ApiBuiltRequest r = ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>());

        Assert.Equal("http://localhost:5150/api/v1/rebuild-index?type=all", r.Url);
        Assert.Null(r.JsonBody);
    }

    [Fact]
    public void Build_throws_when_required_missing()
    {
        ApiEndpointDescriptor d = new(
            "x", "X", "G", HttpMethod.Get, "api/v1/items/{key}",
            [
                new ApiParameter("key", ApiParameterKind.Path, Required: true),
                new ApiParameter("q", ApiParameterKind.Query, Required: true),
            ]);

        ApiInvocationValidationException ex = Assert.Throws<ApiInvocationValidationException>(
            () => ApiUrlBuilder.Build(Base, d, new Dictionary<string, string?>()));

        Assert.Contains("key", ex.MissingParameters);
        Assert.Contains("q", ex.MissingParameters);
    }
}
