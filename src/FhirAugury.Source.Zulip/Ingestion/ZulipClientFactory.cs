using zulip_cs_lib;
using FhirAugury.Source.Zulip.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Ingestion;

/// <summary>
/// Builds a <see cref="ZulipClient"/> from configuration and the DI-managed HttpClient.
/// The HttpClient carries our ZulipRateLimiter and Polly resilience handlers.
/// </summary>
public class ZulipClientFactory(
    IHttpClientFactory httpClientFactory,
    IOptions<ZulipServiceOptions> optionsAccessor)
{
    private readonly ZulipServiceOptions _options = optionsAccessor.Value;

    /// <summary>Creates a ZulipClient using the DI-managed "zulip" HttpClient.</summary>
    public ZulipClient Create()
    {
        HttpClient httpClient = httpClientFactory.CreateClient("zulip");
        (string site, string email, string apiKey) = ResolveCredentials();
        return new ZulipClient(site, email, apiKey, httpClient);
    }

    /// <summary>Resolves credentials from config or a .zuliprc file.</summary>
    internal (string Site, string Email, string ApiKey) ResolveCredentials()
    {
        string? credFile = _options.CredentialFile;

        if (credFile is not null && credFile.StartsWith('~'))
            credFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                credFile[2..]);

        string? email = _options.Email;
        string? apiKey = _options.ApiKey;
        string? baseUrl = _options.BaseUrl;

        if (!string.IsNullOrEmpty(credFile) && File.Exists(credFile))
        {
            foreach (string line in File.ReadLines(credFile))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith('[') || trimmed.StartsWith('#') || !trimmed.Contains('='))
                    continue;

                string[] parts = trimmed.Split('=', 2);
                if (parts.Length != 2) continue;

                string key = parts[0].Trim();
                string value = parts[1].Trim();

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
        }

        return (baseUrl, email ?? "", apiKey ?? "");
    }
}
