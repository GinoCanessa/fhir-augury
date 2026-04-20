using FhirAugury.Cli.Dispatch.Handlers;
using Microsoft.OpenApi;

namespace FhirAugury.Cli.Tests;

public class CallRequestBuilderTests
{
    [Fact]
    public void Build_SubstitutesPathParameters()
    {
        OpenApiOperation op = new()
        {
            OperationId = "items.get",
            Parameters =
            [
                new OpenApiParameter { Name = "source", In = ParameterLocation.Path, Required = true },
                new OpenApiParameter { Name = "id", In = ParameterLocation.Path, Required = true },
            ],
        };

        Dictionary<string, string> p = new()
        {
            ["source"] = "jira",
            ["id"] = "FHIR-123",
        };

        CallRequestBuilder.BuiltRequest built = CallRequestBuilder.Build(
            op, "/api/v1/items/{source}/{id}", HttpMethod.Get, p);

        Assert.Equal(HttpMethod.Get, built.Method);
        Assert.Equal("/api/v1/items/jira/FHIR-123", built.RelativeUrl);
        Assert.Empty(built.Headers);
    }

    [Fact]
    public void Build_PathParameter_IsUrlEncoded()
    {
        OpenApiOperation op = new()
        {
            OperationId = "items.get",
            Parameters =
            [
                new OpenApiParameter { Name = "id", In = ParameterLocation.Path, Required = true },
            ],
        };

        Dictionary<string, string> p = new() { ["id"] = "a/b c" };

        CallRequestBuilder.BuiltRequest built = CallRequestBuilder.Build(
            op, "/api/v1/items/{id}", HttpMethod.Get, p);

        Assert.Equal("/api/v1/items/a%2Fb%20c", built.RelativeUrl);
    }

    [Fact]
    public void Build_AssemblesQueryString()
    {
        OpenApiOperation op = new()
        {
            OperationId = "items.list",
            Parameters =
            [
                new OpenApiParameter { Name = "limit", In = ParameterLocation.Query },
                new OpenApiParameter { Name = "q", In = ParameterLocation.Query },
                new OpenApiParameter { Name = "sort", In = ParameterLocation.Query },
            ],
        };

        Dictionary<string, string> p = new()
        {
            ["limit"] = "20",
            ["q"] = "hello world",
            // "sort" intentionally omitted
        };

        CallRequestBuilder.BuiltRequest built = CallRequestBuilder.Build(
            op, "/api/v1/items", HttpMethod.Get, p);

        Assert.StartsWith("/api/v1/items?", built.RelativeUrl);
        Assert.Contains("limit=20", built.RelativeUrl);
        Assert.Contains("q=hello%20world", built.RelativeUrl);
        Assert.DoesNotContain("sort=", built.RelativeUrl);
    }

    [Fact]
    public void Build_RoutesHeaderParameters()
    {
        OpenApiOperation op = new()
        {
            OperationId = "items.list",
            Parameters =
            [
                new OpenApiParameter { Name = "X-Augury-Trace", In = ParameterLocation.Header },
            ],
        };

        Dictionary<string, string> p = new() { ["X-Augury-Trace"] = "abc-123" };

        CallRequestBuilder.BuiltRequest built = CallRequestBuilder.Build(
            op, "/api/v1/items", HttpMethod.Get, p);

        Assert.Equal("/api/v1/items", built.RelativeUrl);
        Assert.Single(built.Headers);
        Assert.Equal("X-Augury-Trace", built.Headers[0].Key);
        Assert.Equal("abc-123", built.Headers[0].Value);
    }

    [Fact]
    public void Build_UnknownParameter_Throws()
    {
        OpenApiOperation op = new()
        {
            OperationId = "items.list",
            Parameters =
            [
                new OpenApiParameter { Name = "limit", In = ParameterLocation.Query },
            ],
        };

        Dictionary<string, string> p = new() { ["bogus"] = "x" };

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            CallRequestBuilder.Build(op, "/api/v1/items", HttpMethod.Get, p));

        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void Build_MissingRequiredPathParameter_Throws()
    {
        OpenApiOperation op = new()
        {
            OperationId = "items.get",
            Parameters =
            [
                new OpenApiParameter { Name = "id", In = ParameterLocation.Path, Required = true },
            ],
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            CallRequestBuilder.Build(op, "/api/v1/items/{id}", HttpMethod.Get, null));

        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public void Build_NullParams_NoQueryNoHeaders()
    {
        OpenApiOperation op = new()
        {
            OperationId = "items.list",
            Parameters =
            [
                new OpenApiParameter { Name = "limit", In = ParameterLocation.Query },
            ],
        };

        CallRequestBuilder.BuiltRequest built = CallRequestBuilder.Build(
            op, "/api/v1/items", HttpMethod.Get, null);

        Assert.Equal("/api/v1/items", built.RelativeUrl);
        Assert.Empty(built.Headers);
    }
}
