using FhirAugury.Common.Grpc;
using FhirAugury.Source.Jira.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>Adds Jira authentication headers based on the configured auth mode.</summary>
public class JiraAuthHandler(IOptions<JiraServiceOptions> optionsAccessor) : AtlassianAuthHandler
{
    private readonly JiraServiceOptions _options = optionsAccessor.Value;

    protected override string AuthMode => _options.AuthMode.ToLowerInvariant();
    protected override string? Email => _options.Email;
    protected override string? Username => null;
    protected override string? ApiToken => _options.ApiToken;
    protected override string? Cookie => _options.Cookie;
}
