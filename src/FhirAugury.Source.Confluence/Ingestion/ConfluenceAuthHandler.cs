using FhirAugury.Common.Http;
using FhirAugury.Source.Confluence.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>Adds Confluence authentication headers based on the configured auth mode.</summary>
public class ConfluenceAuthHandler(IOptions<ConfluenceServiceOptions> optionsAccessor) : AtlassianAuthHandler
{
    private readonly ConfluenceServiceOptions _options = optionsAccessor.Value;

    protected override string AuthMode => _options.AuthMode.ToLowerInvariant();
    protected override string? Email => null;
    protected override string? Username => _options.Username;
    protected override string? ApiToken => _options.ApiToken;
    protected override string? Cookie => _options.Cookie;
}
