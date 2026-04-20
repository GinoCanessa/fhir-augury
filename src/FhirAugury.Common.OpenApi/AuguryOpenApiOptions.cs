namespace FhirAugury.Common.OpenApi;

/// <summary>
/// Options that control how <c>AddAuguryOpenApi</c> configures ASP.NET OpenAPI
/// for a FHIR Augury service.
/// </summary>
public sealed class AuguryOpenApiOptions
{
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public bool IncludeDocsUi { get; set; }

    public string DefaultTag { get; set; } = string.Empty;

    public string VendorExtensionPrefix { get; set; } = "x-augury-";
}
