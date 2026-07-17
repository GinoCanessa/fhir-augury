namespace FhirAugury.Common.OpenApi.Attributes;

/// <summary>
/// Marks the version after which the operation will be removed; surfaced as
/// <c>x-augury-until</c> on the OpenAPI operation.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class AuguryUntilAttribute : Attribute
{
    public AuguryUntilAttribute(string version)
    {
        Version = version;
    }

    public string Version { get; }
}
