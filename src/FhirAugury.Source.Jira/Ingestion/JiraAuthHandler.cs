using System.Net.Http.Headers;
using System.Text;
using FhirAugury.Source.Jira.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>Adds Jira authentication headers based on the configured auth mode.</summary>
public class JiraAuthHandler(IOptions<JiraServiceOptions> optionsAccessor) : DelegatingHandler
{
    private readonly JiraServiceOptions _options = optionsAccessor.Value;
    private readonly string _authMode = optionsAccessor.Value.AuthMode.ToLowerInvariant();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (_authMode)
        {
            case "cookie":
                if (!string.IsNullOrEmpty(_options.Cookie))
                    request.Headers.TryAddWithoutValidation("cookie", _options.Cookie);
                break;

            case "apitoken":
                if (!string.IsNullOrEmpty(_options.Email) && !string.IsNullOrEmpty(_options.ApiToken))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Email}:{_options.ApiToken}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
