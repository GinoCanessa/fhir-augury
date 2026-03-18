using System.Net.Http.Headers;
using System.Text;

namespace FhirAugury.Sources.Confluence;

/// <summary>Adds Confluence authentication headers based on the configured auth mode.</summary>
public class ConfluenceAuthHandler(ConfluenceSourceOptions options) : DelegatingHandler
{
    /// <summary>Creates an HttpClient configured with Confluence authentication.</summary>
    public static HttpClient CreateHttpClient(ConfluenceSourceOptions options)
    {
        var handler = new ConfluenceAuthHandler(options) { InnerHandler = new HttpClientHandler() };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        ConfigureHttpClient(client, options);
        return client;
    }

    /// <summary>Configures an existing HttpClient with Confluence default headers and auth.</summary>
    public static void ConfigureHttpClient(HttpClient client, ConfluenceSourceOptions options)
    {
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/1.0");

        switch (options.AuthMode)
        {
            case ConfluenceAuthMode.Basic:
                if (!string.IsNullOrEmpty(options.Username) && !string.IsNullOrEmpty(options.ApiToken))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.ApiToken}"));
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;

            case ConfluenceAuthMode.Cookie:
                if (!string.IsNullOrEmpty(options.Cookie))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("cookie", options.Cookie);
                }
                break;
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (options.AuthMode)
        {
            case ConfluenceAuthMode.Basic:
                if (!string.IsNullOrEmpty(options.Username) && !string.IsNullOrEmpty(options.ApiToken))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.ApiToken}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;

            case ConfluenceAuthMode.Cookie:
                if (!string.IsNullOrEmpty(options.Cookie))
                {
                    request.Headers.Add("cookie", options.Cookie);
                }
                break;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
