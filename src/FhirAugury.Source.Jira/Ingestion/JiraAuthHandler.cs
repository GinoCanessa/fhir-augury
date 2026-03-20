using System.Net.Http.Headers;
using System.Text;
using FhirAugury.Source.Jira.Configuration;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>Adds Jira authentication headers based on the configured auth mode.</summary>
public class JiraAuthHandler(JiraServiceOptions options) : DelegatingHandler
{
    /// <summary>Configures default headers and auth on an HttpClient.</summary>
    public static void ConfigureHttpClient(HttpClient client, JiraServiceOptions options)
    {
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/2.0");

        switch (options.AuthMode.ToLowerInvariant())
        {
            case "cookie":
                if (!string.IsNullOrEmpty(options.Cookie))
                    client.DefaultRequestHeaders.TryAddWithoutValidation("cookie", options.Cookie);
                break;

            case "apitoken":
                if (!string.IsNullOrEmpty(options.Email) && !string.IsNullOrEmpty(options.ApiToken))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Email}:{options.ApiToken}"));
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (options.AuthMode.ToLowerInvariant())
        {
            case "cookie":
                if (!string.IsNullOrEmpty(options.Cookie))
                    request.Headers.TryAddWithoutValidation("cookie", options.Cookie);
                break;

            case "apitoken":
                if (!string.IsNullOrEmpty(options.Email) && !string.IsNullOrEmpty(options.ApiToken))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Email}:{options.ApiToken}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
