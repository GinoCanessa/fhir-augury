using System.Net.Http.Headers;
using System.Text;

namespace FhirAugury.Sources.Zulip;

/// <summary>Adds Zulip API authentication headers (HTTP Basic with email + API key).</summary>
public class ZulipAuthHandler(ZulipSourceOptions options) : DelegatingHandler
{
    /// <summary>Creates an HttpClient configured with Zulip authentication.</summary>
    public static HttpClient CreateHttpClient(ZulipSourceOptions options)
    {
        var resolved = ResolveCredentials(options);
        var handler = new ZulipAuthHandler(resolved) { InnerHandler = new HttpClientHandler() };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        ConfigureHttpClient(client, resolved);
        return client;
    }

    /// <summary>Configures an existing HttpClient with Zulip default headers and auth.</summary>
    public static void ConfigureHttpClient(HttpClient client, ZulipSourceOptions options)
    {
        var resolved = ResolveCredentials(options);
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/1.0");

        if (!string.IsNullOrEmpty(resolved.Email) && !string.IsNullOrEmpty(resolved.ApiKey))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{resolved.Email}:{resolved.ApiKey}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(options.Email) && !string.IsNullOrEmpty(options.ApiKey))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Email}:{options.ApiKey}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>Resolves credentials from a .zuliprc file if CredentialFile is specified.</summary>
    internal static ZulipSourceOptions ResolveCredentials(ZulipSourceOptions options)
    {
        if (string.IsNullOrEmpty(options.CredentialFile) || !File.Exists(options.CredentialFile))
        {
            return options;
        }

        string? email = options.Email;
        string? apiKey = options.ApiKey;
        string? baseUrl = null;

        foreach (var line in File.ReadLines(options.CredentialFile))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') || trimmed.StartsWith('#') || !trimmed.Contains('='))
                continue;

            var parts = trimmed.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            switch (key)
            {
                case "email" when string.IsNullOrEmpty(email):
                    email = value;
                    break;
                case "key" when string.IsNullOrEmpty(apiKey):
                    apiKey = value;
                    break;
                case "site":
                    baseUrl = value;
                    break;
            }
        }

        return options with
        {
            Email = email,
            ApiKey = apiKey,
            BaseUrl = baseUrl ?? options.BaseUrl,
        };
    }
}
