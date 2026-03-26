using System.Net.Http.Headers;
using System.Text;
using FhirAugury.Source.Zulip.Configuration;

namespace FhirAugury.Source.Zulip.Ingestion;

/// <summary>Adds Zulip API authentication headers (HTTP Basic with email + API key).</summary>
public static class ZulipAuthHandler
{
    /// <summary>Configures an existing HttpClient with Zulip default headers and auth.</summary>
    public static void ConfigureHttpClient(HttpClient client, ZulipServiceOptions options)
    {
        ZulipServiceOptions resolved = ResolveCredentials(options);
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/2.0");

        if (!string.IsNullOrEmpty(resolved.Email) && !string.IsNullOrEmpty(resolved.ApiKey))
        {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{resolved.Email}:{resolved.ApiKey}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    /// <summary>Resolves credentials from a .zuliprc file if CredentialFile is specified.</summary>
    internal static ZulipServiceOptions ResolveCredentials(ZulipServiceOptions options)
    {
        string? credFile = options.CredentialFile;

        // Expand ~ to home directory
        if (credFile is not null && credFile.StartsWith('~'))
            credFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), credFile[2..]);

        if (string.IsNullOrEmpty(credFile) || !File.Exists(credFile))
            return options;

        string? email = options.Email;
        string? apiKey = options.ApiKey;
        string? baseUrl = null;

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

        return new ZulipServiceOptions
        {
            Email = email,
            ApiKey = apiKey,
            BaseUrl = baseUrl ?? options.BaseUrl,
            CredentialFile = options.CredentialFile,
            CachePath = options.CachePath,
            DatabasePath = options.DatabasePath,
            SyncSchedule = options.SyncSchedule,
            OnlyWebPublic = options.OnlyWebPublic,
            BatchSize = options.BatchSize,
            Ports = options.Ports,
            RateLimiting = options.RateLimiting,
        };
    }
}
