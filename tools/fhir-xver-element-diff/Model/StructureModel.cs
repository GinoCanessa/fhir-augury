namespace FhirAugury.Tools.FhirXverElementDiff.Model;

/// <summary>
/// One in-scope structure (primitive-type, complex-type, or resource — including
/// abstract base types) with its full snapshot element list. Loaded read-only from
/// the spec DBs by <see cref="Readers.ReleaseReader"/>.
/// </summary>
internal sealed record StructureModel(
    string Name,
    string Kind,
    string? Derivation,
    bool IsAbstract,
    string? BaseDefinition,
    string? FhirType,
    string? WorkGroup,
    int SnapshotCount,
    IReadOnlyList<ElementModel> Elements)
{
    /// <summary>
    /// Bucketed structure group per the report shape: primitive types, complex types,
    /// resources. Abstract base types sort into complex/resource by their <c>Kind</c>.
    /// </summary>
    public StructureGroup Group => Kind switch
    {
        "primitive-type" => StructureGroup.PrimitiveType,
        "resource" => StructureGroup.Resource,
        _ => StructureGroup.ComplexType,
    };
}

/// <summary>The three top-level structure groupings used in every report bucket.</summary>
internal enum StructureGroup
{
    PrimitiveType,
    ComplexType,
    Resource,
}

internal static class StructureGroupExtensions
{
    /// <summary>The <c>### </c> heading label for a group.</summary>
    public static string Heading(this StructureGroup group) => group switch
    {
        StructureGroup.PrimitiveType => "Primitive types",
        StructureGroup.ComplexType => "Complex types",
        StructureGroup.Resource => "Resources",
        _ => group.ToString(),
    };
}
