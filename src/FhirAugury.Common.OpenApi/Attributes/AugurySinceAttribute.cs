namespace FhirAugury.Common.OpenApi.Attributes;

/// <summary>
/// Marks the version in which the operation first became available; surfaced
/// as <c>x-augury-since</c> on the OpenAPI operation.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class AugurySinceAttribute : Attribute
{
    public AugurySinceAttribute(string version)
    {
        Version = version;
    }

    public string Version { get; }
}
