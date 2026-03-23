namespace FhirAugury.Common.Configuration;

/// <summary>
/// Shared port configuration for source services.
/// </summary>
public class PortConfiguration
{
    public int Http { get; set; }
    public int Grpc { get; set; }
}
