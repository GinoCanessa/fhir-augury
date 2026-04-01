using System.Text.Json;
using Fhiraugury;
using Google.Protobuf;
using Grpc.Net.Client;

namespace FhirAugury.DevUi.Services;

public sealed class OrchestratorClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly OrchestratorService.OrchestratorServiceClient _client;

    public string GrpcAddress { get; }

    public OrchestratorClient(IConfiguration configuration)
    {
        GrpcAddress = configuration["DevUi:OrchestratorGrpcAddress"] ?? "http://localhost:5151";
        _channel = GrpcChannel.ForAddress(GrpcAddress);
        _client = new OrchestratorService.OrchestratorServiceClient(_channel);
    }

    public async Task<ServicesStatusResponse> GetServicesStatusAsync(CancellationToken ct = default)
    {
        return await _client.GetServicesStatusAsync(new ServicesStatusRequest(), cancellationToken: ct);
    }

    public async Task<ServiceEndpointsResponse> GetServiceEndpointsAsync(CancellationToken ct = default)
    {
        return await _client.GetServiceEndpointsAsync(new ServiceEndpointsRequest(), cancellationToken: ct);
    }

    public async Task<OrchestratorRebuildIndexResponse> RebuildIndexAsync(
        string source, string indexType = "all", CancellationToken ct = default)
    {
        OrchestratorRebuildIndexRequest request = new() { IndexType = indexType };
        request.Sources.Add(source);
        return await _client.RebuildIndexAsync(request, cancellationToken: ct);
    }

    public async Task<FindRelatedResponse> FindRelatedAsync(string source, string id, int limit = 20, CancellationToken ct = default)
    {
        return await _client.FindRelatedAsync(
            new FindRelatedRequest { Source = source, Id = id, Limit = limit },
            cancellationToken: ct);
    }

    public string FormatAsJson(IMessage message)
    {
        string json = JsonFormatter.Default.Format(message);
        using JsonDocument doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    public void Dispose() => _channel.Dispose();
}
