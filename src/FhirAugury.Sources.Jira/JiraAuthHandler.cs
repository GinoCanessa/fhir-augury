using System.Net.Http.Headers;
using System.Text;

namespace FhirAugury.Sources.Jira;

/// <summary>Adds Jira authentication headers based on the configured auth mode.</summary>
public class JiraAuthHandler(JiraSourceOptions options) : DelegatingHandler
{
    /// <summary>Creates an HttpClient configured with Jira authentication.</summary>
    public static HttpClient CreateHttpClient(JiraSourceOptions options)
    {
        var handler = new JiraAuthHandler(options) { InnerHandler = new HttpClientHandler() };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        ConfigureHttpClient(client, options);
        return client;
    }

    /// <summary>Configures an existing HttpClient with Jira default headers and auth.</summary>
    public static void ConfigureHttpClient(HttpClient client, JiraSourceOptions options)
    {
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/1.0");

        switch (options.AuthMode)
        {
            case JiraAuthMode.Cookie:
                if (!string.IsNullOrEmpty(options.Cookie))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("cookie", options.Cookie);
                }
                break;

            case JiraAuthMode.ApiToken:
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
        switch (options.AuthMode)
        {
            case JiraAuthMode.Cookie:
                if (!string.IsNullOrEmpty(options.Cookie))
                {
                    request.Headers.Add("cookie", options.Cookie);
                }
                break;

            case JiraAuthMode.ApiToken:
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
