using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class GetHandler
{
    public static async Task<object> HandleAsync(GetRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        JsonElement response = await client.ContentGetItemAsync(
            request.Source, request.Id,
            request.IncludeContent, request.IncludeComments, request.IncludeSnapshot, ct);

        List<object> comments = [];
        if (response.TryGetProperty("comments", out JsonElement commentsEl) && commentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in commentsEl.EnumerateArray())
            {
                comments.Add(new
                {
                    id = c.GetStringOrNull("id"),
                    author = c.GetStringOrNull("author"),
                    body = c.GetStringOrNull("body"),
                    createdAt = c.GetStringOrNull("createdAt"),
                    url = c.GetStringOrNull("url"),
                });
            }
        }

        return new
        {
            source = response.GetStringOrNull("source"),
            contentType = response.GetStringOrNull("contentType"),
            id = response.GetStringOrNull("id"),
            title = response.GetStringOrNull("title"),
            content = response.GetStringOrNull("content"),
            url = response.GetStringOrNull("url"),
            createdAt = response.GetStringOrNull("createdAt"),
            updatedAt = response.GetStringOrNull("updatedAt"),
            metadata = response.GetStringDictionary("metadata"),
            comments = comments.ToArray(),
            snapshot = response.GetStringOrNull("snapshot"),
        };
    }
}
