using System.Net.Http.Headers;
using System.Text;
using FhirAugury.Source.Confluence.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>Adds Confluence authentication headers based on the configured auth mode.</summary>
public class ConfluenceAuthHandler(IOptions<ConfluenceServiceOptions> optionsAccessor) : DelegatingHandler
{
    private readonly ConfluenceServiceOptions _options = optionsAccessor.Value;
    private readonly string _authMode = optionsAccessor.Value.AuthMode.ToLowerInvariant();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (_authMode)
        {
            case "basic":
                if (!string.IsNullOrEmpty(_options.Username) && !string.IsNullOrEmpty(_options.ApiToken))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.ApiToken}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;

            case "cookie":
                if (!string.IsNullOrEmpty(_options.Cookie))
                    request.Headers.TryAddWithoutValidation("cookie", _options.Cookie);
                break;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
