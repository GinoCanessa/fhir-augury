using System.Text.Json;
using FhirAugury.Source.Zulip.Indexing;

namespace FhirAugury.Source.Zulip.Tests;

public class ZulipQueryRequestJsonTests
{
    [Fact]
    public void Deserialize_AbsentStringLists_RemainNull()
    {
        ZulipQueryRequest request = JsonSerializer.Deserialize<ZulipQueryRequest>("{}")!;

        Assert.Null(request.StreamNames);
        Assert.Null(request.SenderNames);
    }

    [Fact]
    public void Deserialize_NullStringLists_RemainNull()
    {
        ZulipQueryRequest request = JsonSerializer.Deserialize<ZulipQueryRequest>("{\"streamNames\":null,\"senderNames\":null}", JsonOptions())!;

        Assert.Null(request.StreamNames);
        Assert.Null(request.SenderNames);
    }

    [Fact]
    public void Deserialize_EmptyStringLists_RemainEmpty()
    {
        ZulipQueryRequest request = JsonSerializer.Deserialize<ZulipQueryRequest>("{\"streamNames\":[],\"senderNames\":[]}", JsonOptions())!;

        Assert.Empty(request.StreamNames!);
        Assert.Empty(request.SenderNames!);
    }

    [Fact]
    public void Deserialize_NonEmptyStringLists_PreserveValues()
    {
        ZulipQueryRequest request = JsonSerializer.Deserialize<ZulipQueryRequest>("{\"streamNames\":[\"implementers\"],\"senderNames\":[\"Alice\",\"Bob\"]}", JsonOptions())!;

        Assert.Equal(["implementers"], request.StreamNames);
        Assert.Equal(["Alice", "Bob"], request.SenderNames);
    }

    private static JsonSerializerOptions JsonOptions() => new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
}
