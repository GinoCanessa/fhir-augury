using Fhiraugury;
using FhirAugury.Common;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Testing;

namespace FhirAugury.McpShared.Tests;

/// <summary>
/// Helper for creating mock gRPC responses for MCP tool testing.
/// </summary>
internal static class McpTestHelper
{
    internal static SearchResponse CreateSearchResponse(params (string Source, string Id, string Title, double Score)[] items)
    {
        SearchResponse response = new SearchResponse { TotalResults = items.Length };
        foreach ((string? source, string? id, string? title, double score) in items)
        {
            response.Results.Add(new SearchResultItem
            {
                Source = source,
                Id = id,
                Title = title,
                Score = score,
                Url = $"https://example.com/{source}/{id}",
                UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
        }
        return response;
    }

    internal static FindRelatedResponse CreateRelatedResponse(
        string seedSource, string seedId, string seedTitle,
        params (string Source, string Id, string Title, double Score, string Relationship)[] items)
    {
        FindRelatedResponse response = new FindRelatedResponse
        {
            SeedSource = seedSource,
            SeedId = seedId,
            SeedTitle = seedTitle,
        };
        foreach ((string? source, string? id, string? title, double score, string? rel) in items)
        {
            response.Items.Add(new RelatedItem
            {
                Source = source,
                Id = id,
                Title = title,
                RelevanceScore = score,
                Relationship = rel,
                Url = $"https://example.com/{source}/{id}",
            });
        }
        return response;
    }

    internal static GetXRefResponse CreateXRefResponse(
        params (string SourceType, string SourceId, string TargetType, string TargetId, string LinkType)[] refs)
    {
        GetXRefResponse response = new GetXRefResponse();
        foreach ((string? st, string? si, string? tt, string? ti, string? lt) in refs)
        {
            response.References.Add(new CrossReference
            {
                SourceType = st,
                SourceId = si,
                TargetType = tt,
                TargetId = ti,
                LinkType = lt,
            });
        }
        return response;
    }

    internal static ServicesStatusResponse CreateServicesStatus()
    {
        ServicesStatusResponse response = new ServicesStatusResponse();
        response.Services.Add(new ServiceHealth
        {
            Name = SourceSystems.Jira,
            Status = "healthy",
            GrpcAddress = "http://localhost:5161",
            ItemCount = 1000,
            DbSizeBytes = 10_000_000,
        });
        response.Services.Add(new ServiceHealth
        {
            Name = SourceSystems.Zulip,
            Status = "healthy",
            GrpcAddress = "http://localhost:5171",
            ItemCount = 5000,
            DbSizeBytes = 50_000_000,
        });
        return response;
    }

    internal static ItemResponse CreateItemResponse(string source, string id, string title)
    {
        ItemResponse response = new ItemResponse
        {
            Source = source,
            Id = id,
            Title = title,
            Content = "Test content",
            Url = $"https://example.com/{source}/{id}",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-30)),
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        response.Metadata.Add("status", "Open");
        response.Metadata.Add("type", "Bug");
        return response;
    }

    internal static AsyncServerStreamingCall<T> CreateStreamingCall<T>(params T[] items)
        where T : class
    {
        TestAsyncStreamReader<T> reader = new TestAsyncStreamReader<T>(items);
        return new AsyncServerStreamingCall<T>(
            reader,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });
    }
}

internal class TestAsyncStreamReader<T> : IAsyncStreamReader<T>
{
    private readonly IEnumerator<T> _enumerator;

    public TestAsyncStreamReader(IEnumerable<T> items) =>
        _enumerator = items.GetEnumerator();

    public T Current => _enumerator.Current;

    public Task<bool> MoveNext(CancellationToken cancellationToken) =>
        Task.FromResult(_enumerator.MoveNext());
}
