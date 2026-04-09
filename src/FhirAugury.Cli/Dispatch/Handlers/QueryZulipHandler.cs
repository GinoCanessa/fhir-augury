using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class QueryZulipHandler
{
    public static async Task<object> HandleAsync(QueryZulipRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);

        object queryBody = new
        {
            query = request.Query ?? "",
            streams = request.Streams,
            topic = request.Topic ?? "",
            topicKeyword = request.TopicKeyword ?? "",
            senders = request.Senders,
            sortBy = request.SortBy,
            sortOrder = request.SortOrder,
            limit = request.Limit,
            after = request.After,
            before = request.Before,
        };

        JsonElement response = await client.QueryZulipViaOrchestratorAsync(queryBody, ct);

        List<object> results = [];
        if (response.TryGetProperty("results", out JsonElement resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement msg in resultsEl.EnumerateArray())
            {
                results.Add(new
                {
                    id = msg.GetStringOrNull("id"),
                    streamName = msg.GetStringOrNull("streamName"),
                    topic = msg.GetStringOrNull("topic"),
                    senderName = msg.GetStringOrNull("senderName"),
                    snippet = msg.GetStringOrNull("snippet"),
                    timestamp = msg.GetStringOrNull("timestamp"),
                });
            }
        }

        return new { results };
    }
}
