namespace FhirAugury.Common.OpenApi.Attributes;

/// <summary>
/// Marks a controller action as streaming (SSE/NDJSON); surfaced as
/// <c>x-augury-streaming: true</c> on the OpenAPI operation.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class AuguryStreamingAttribute : Attribute
{
}
