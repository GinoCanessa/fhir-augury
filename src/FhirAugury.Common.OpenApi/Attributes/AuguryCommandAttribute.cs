namespace FhirAugury.Common.OpenApi.Attributes;

/// <summary>
/// Marks a controller action as an Augury command with an explicit command name.
/// The resulting OpenAPI operation is assigned <c>operationId = {controller}.{name}</c>
/// and tagged with the <c>x-augury-command</c> vendor extension.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class AuguryCommandAttribute : Attribute
{
    public AuguryCommandAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
