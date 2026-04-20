namespace FhirAugury.Common.OpenApi.Attributes;

public enum AuguryVisibility
{
    Public,
    Internal,
}

/// <summary>
/// Sets the Augury visibility of a controller or action; surfaced as
/// <c>x-augury-visibility</c> on the OpenAPI operation. Used by the merger to
/// filter operations when <c>includeInternal == false</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class AuguryVisibilityAttribute : Attribute
{
    public AuguryVisibilityAttribute(AuguryVisibility visibility)
    {
        Visibility = visibility;
    }

    public AuguryVisibility Visibility { get; }
}
