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

    public void Dispose()
    {
        _orchestratorChannel.Dispose();
        if (_jiraChannel.IsValueCreated) _jiraChannel.Value.Dispose();
        if (_zulipChannel.IsValueCreated) _zulipChannel.Value.Dispose();
    }
}
