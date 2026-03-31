using Fhiraugury;
using FhirAugury.Common;
using FhirAugury.Orchestrator.Configuration;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Routing;

/// <summary>
/// Routes proxied calls to source services based on source name.
/// Manages gRPC client connections to all configured source services.
/// </summary>
public class SourceRouter : IDisposable
{
    private readonly Dictionary<string, Grpc.Net.Client.GrpcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceService.SourceServiceClient> _sourceClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SourceRouter> _logger;

    private JiraService.JiraServiceClient? _jiraClient;

    public SourceRouter(IOptions<OrchestratorOptions> options, ILogger<SourceRouter> logger)
    {
        _options = options.Value;
        _logger = logger;

        foreach ((string? name, SourceServiceConfig? config) in _options.Services)
        {
            if (!config.Enabled || string.IsNullOrEmpty(config.GrpcAddress))
                continue;

            try
            {
                GrpcChannel channel = Grpc.Net.Client.GrpcChannel.ForAddress(config.GrpcAddress);
                _channels[name] = channel;
                _sourceClients[name] = new SourceService.SourceServiceClient(channel);

                if (name.Equals(SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
                {
                    _jiraClient = new JiraService.JiraServiceClient(channel);
                }

                _logger.LogInformation("Configured gRPC client for source {Source} at {Address}", name, config.GrpcAddress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure gRPC client for source {Source}", name);
            }
        }
    }

    /// <summary>
    /// Gets the SourceService client for the named source, or null if not configured/disabled.
    /// </summary>
    public SourceService.SourceServiceClient? GetSourceClient(string sourceName)
    {
        return _sourceClients.GetValueOrDefault(sourceName);
    }

    /// <summary>
    /// Gets the Jira-specific gRPC client, or null if Jira is not configured.
    /// </summary>
    public JiraService.JiraServiceClient? GetJiraClient() => _jiraClient;

    /// <summary>
    /// Returns all configured source names that are enabled.
    /// </summary>
    public IReadOnlyList<string> GetEnabledSources()
    {
        return _sourceClients.Keys.ToList();
    }

    /// <summary>
    /// Returns the configuration for a specific source, if it exists.
    /// </summary>
    public SourceServiceConfig? GetSourceConfig(string sourceName)
    {
        return _options.Services.GetValueOrDefault(sourceName);
    }

    public void Dispose()
    {
        foreach (GrpcChannel channel in _channels.Values)
        {
            channel.Dispose();
        }
        _channels.Clear();
        _sourceClients.Clear();
        GC.SuppressFinalize(this);
    }
}
