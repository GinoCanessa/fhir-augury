using System.Net.Http.Headers;
using System.Text;
using FhirAugury.Source.Confluence.Configuration;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>Adds Confluence authentication headers based on the configured auth mode.</summary>
public class ConfluenceAuthHandler(ConfluenceServiceOptions options) : DelegatingHandler
{
    /// <summary>Configures default headers and auth on an HttpClient.</summary>
    public static void ConfigureHttpClient(HttpClient client, ConfluenceServiceOptions options)
    {
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/2.0");

        switch (options.AuthMode.ToLowerInvariant())
        {
            case "basic":
                if (!string.IsNullOrEmpty(options.Username) && !string.IsNullOrEmpty(options.ApiToken))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.ApiToken}"));
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;

            case "cookie":
                if (!string.IsNullOrEmpty(options.Cookie))
                    client.DefaultRequestHeaders.TryAddWithoutValidation("cookie", options.Cookie);
                break;
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (options.AuthMode.ToLowerInvariant())
        {
            case "basic":
                if (!string.IsNullOrEmpty(options.Username) && !string.IsNullOrEmpty(options.ApiToken))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.ApiToken}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;

            case "cookie":
                if (!string.IsNullOrEmpty(options.Cookie))
                    request.Headers.TryAddWithoutValidation("cookie", options.Cookie);
                break;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
