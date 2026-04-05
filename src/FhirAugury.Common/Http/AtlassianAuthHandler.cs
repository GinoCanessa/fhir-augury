using System.Net.Http.Headers;
using System.Text;

namespace FhirAugury.Common.Http;

/// <summary>
/// Shared base delegating handler for Atlassian-style authentication (Basic with email/apitoken, or cookie).
/// Used by Jira and Confluence auth handlers to avoid duplication.
/// </summary>
public abstract class AtlassianAuthHandler : DelegatingHandler
{
    protected abstract string AuthMode { get; }
    protected abstract string? Email { get; }
    protected abstract string? Username { get; }
    protected abstract string? ApiToken { get; }
    protected abstract string? Cookie { get; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (AuthMode)
        {
            case "cookie":
                if (!string.IsNullOrEmpty(Cookie))
                    request.Headers.TryAddWithoutValidation("cookie", Cookie);
                break;

            case "basic":
            case "apitoken":
                string? user = !string.IsNullOrEmpty(Email) ? Email : Username;
                if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(ApiToken))
                {
                    string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{ApiToken}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
