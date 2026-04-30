namespace FhirAugury.Processor.Jira.Fhir.Applier.Configuration;

/// <summary>
/// Mirrors <c>FhirAugury.Source.GitHub.Configuration.AuthConfiguration</c>: holds either
/// a direct token or the env-var name from which to resolve one. Used by
/// <c>GitPushService</c> when constructing an authenticated push URL.
/// </summary>
public sealed class ApplierAuthOptions
{
    public const string SectionName = "Processing:Applier:Auth";

    public string? Token { get; set; }
    public string? TokenEnvVar { get; set; } = "GITHUB_TOKEN";

    public string? ResolveToken()
    {
        if (!string.IsNullOrEmpty(Token))
        {
            return Token;
        }

        if (!string.IsNullOrEmpty(TokenEnvVar))
        {
            return Environment.GetEnvironmentVariable(TokenEnvVar);
        }

        return null;
    }
}
