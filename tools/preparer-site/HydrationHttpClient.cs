namespace FhirAugury.Tools.PreparerSite;

internal static class HydrationHttpClient
{
    public const string DefaultOrchestratorAddress = "http://localhost:5150";

    public static HttpClient Create(string? orchestratorAddress)
    {
        string baseAddress = string.IsNullOrWhiteSpace(orchestratorAddress)
            ? DefaultOrchestratorAddress
            : orchestratorAddress;

        if (!baseAddress.EndsWith('/'))
        {
            baseAddress += "/";
        }

        return new HttpClient
        {
            BaseAddress = new Uri(baseAddress, UriKind.Absolute),
        };
    }
}
