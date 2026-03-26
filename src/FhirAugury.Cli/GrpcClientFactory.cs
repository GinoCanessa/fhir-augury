using Fhiraugury;
using Grpc.Net.Client;

namespace FhirAugury.Cli;

/// <summary>
/// Creates gRPC clients from endpoint addresses.
/// </summary>
public sealed class GrpcClientFactory : IDisposable
{
    private readonly GrpcChannel _orchestratorChannel;
    private readonly Lazy<GrpcChannel> _jiraChannel;
    private readonly Lazy<GrpcChannel> _zulipChannel;
    private readonly List<GrpcChannel> _dynamicChannels = [];

    private OrchestratorService.OrchestratorServiceClient? _orchestratorClient;
    private JiraService.JiraServiceClient? _jiraClient;
    private ZulipService.ZulipServiceClient? _zulipClient;
    private SourceService.SourceServiceClient? _jiraSourceClient;
    private SourceService.SourceServiceClient? _zulipSourceClient;

    public GrpcClientFactory(string orchestratorAddr, string? jiraAddr = null, string? zulipAddr = null)
    {
        _orchestratorChannel = GrpcChannel.ForAddress(orchestratorAddr);
        _jiraChannel = new Lazy<GrpcChannel>(() =>
            GrpcChannel.ForAddress(jiraAddr ?? Environment.GetEnvironmentVariable("FHIR_AUGURY_JIRA_GRPC") ?? "http://localhost:5161"));
        _zulipChannel = new Lazy<GrpcChannel>(() =>
            GrpcChannel.ForAddress(zulipAddr ?? Environment.GetEnvironmentVariable("FHIR_AUGURY_ZULIP_GRPC") ?? "http://localhost:5171"));
    }

    public OrchestratorService.OrchestratorServiceClient Orchestrator =>
        _orchestratorClient ??= new(_orchestratorChannel);

    public JiraService.JiraServiceClient Jira =>
        _jiraClient ??= new(_jiraChannel.Value);

    public ZulipService.ZulipServiceClient Zulip =>
        _zulipClient ??= new(_zulipChannel.Value);

    public SourceService.SourceServiceClient JiraSource =>
        _jiraSourceClient ??= new(_jiraChannel.Value);

    public SourceService.SourceServiceClient ZulipSource =>
        _zulipSourceClient ??= new(_zulipChannel.Value);

    /// <summary>
    /// Queries the orchestrator for active service endpoints.
    /// </summary>
    public async Task<Dictionary<string, string>> GetServiceEndpointsAsync(CancellationToken ct = default)
    {
        ServiceEndpointsResponse response = await Orchestrator.GetServiceEndpointsAsync(
            new ServiceEndpointsRequest(), cancellationToken: ct);
        return response.Endpoints
            .Where(e => e.Enabled)
            .ToDictionary(e => e.Name, e => e.GrpcAddress, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a SourceService client for a dynamically resolved address.
    /// </summary>
    public SourceService.SourceServiceClient GetSourceClient(string address)
    {
        GrpcChannel channel = GrpcChannel.ForAddress(address);
        _dynamicChannels.Add(channel);
        return new SourceService.SourceServiceClient(channel);
    }

    public void Dispose()
    {
        _orchestratorChannel.Dispose();
        if (_jiraChannel.IsValueCreated) _jiraChannel.Value.Dispose();
        if (_zulipChannel.IsValueCreated) _zulipChannel.Value.Dispose();
        foreach (GrpcChannel channel in _dynamicChannels)
            channel.Dispose();
    }
}
