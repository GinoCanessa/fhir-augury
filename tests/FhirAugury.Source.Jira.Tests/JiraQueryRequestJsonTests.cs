using System.Text.Json;
using FhirAugury.Source.Jira.Api;

namespace FhirAugury.Source.Jira.Tests;

public class JiraQueryRequestJsonTests
{
    [Fact]
    public void Deserialize_AbsentList_RemainsNull()
    {
        JiraQueryRequest request = JsonSerializer.Deserialize<JiraQueryRequest>("{}")!;

        Assert.Null(request.Statuses);
    }

    [Fact]
    public void Deserialize_NullList_RemainsNull()
    {
        JiraQueryRequest request = JsonSerializer.Deserialize<JiraQueryRequest>("{\"statuses\":null}", JsonOptions())!;

        Assert.Null(request.Statuses);
    }

    [Fact]
    public void Deserialize_EmptyList_RemainsEmpty()
    {
        JiraQueryRequest request = JsonSerializer.Deserialize<JiraQueryRequest>("{\"statuses\":[]}", JsonOptions())!;

        Assert.NotNull(request.Statuses);
        Assert.Empty(request.Statuses);
    }

    [Fact]
    public void Deserialize_NonEmptyList_PreservesValues()
    {
        JiraQueryRequest request = JsonSerializer.Deserialize<JiraQueryRequest>("{\"statuses\":[\"Open\",\"Closed\"]}", JsonOptions())!;

        Assert.Equal(["Open", "Closed"], request.Statuses);
    }

    private static JsonSerializerOptions JsonOptions() => new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
}
