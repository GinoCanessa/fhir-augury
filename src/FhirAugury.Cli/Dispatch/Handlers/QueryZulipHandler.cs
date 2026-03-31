using Fhiraugury;
using FhirAugury.Cli.Models;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class QueryZulipHandler
{
    public static async Task<object> HandleAsync(QueryZulipRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        ZulipQueryRequest grpcRequest = new()
        {
            Topic = request.Topic ?? "",
            TopicKeyword = request.TopicKeyword ?? "",
            Query = request.Query ?? "",
            SortBy = request.SortBy,
            SortOrder = request.SortOrder,
            Limit = request.Limit,
        };

        if (request.Streams is { Length: > 0 })
        {
            foreach (string stream in request.Streams)
                grpcRequest.StreamNames.Add(stream);
        }

        if (request.Senders is { Length: > 0 })
        {
            foreach (string sender in request.Senders)
                grpcRequest.SenderNames.Add(sender);
        }

        if (!string.IsNullOrEmpty(request.After) && DateTimeOffset.TryParse(request.After, out DateTimeOffset after))
            grpcRequest.After = Timestamp.FromDateTimeOffset(after);

        if (!string.IsNullOrEmpty(request.Before) && DateTimeOffset.TryParse(request.Before, out DateTimeOffset before))
            grpcRequest.Before = Timestamp.FromDateTimeOffset(before);

        using AsyncServerStreamingCall<ZulipMessageSummary> call = clients.Zulip.QueryMessages(grpcRequest, cancellationToken: ct);

        List<object> results = [];
        await foreach (ZulipMessageSummary msg in call.ResponseStream.ReadAllAsync(ct))
        {
            results.Add(new
            {
                id = msg.Id,
                streamName = msg.StreamName,
                topic = msg.Topic,
                senderName = msg.SenderName,
                snippet = msg.Snippet,
                timestamp = msg.Timestamp?.ToDateTimeOffset().ToString("o"),
            });
        }

        return new { results };
    }
}
