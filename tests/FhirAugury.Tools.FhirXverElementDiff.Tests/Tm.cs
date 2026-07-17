using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Tests;

/// <summary>
/// Concise builders for the internal diff models, so tests can craft structures and
/// elements without touching a database. Element paths are used verbatim; the
/// root-relative and normalized keys are computed exactly as the reader does.
/// </summary>
internal static class Tm
{
    public static ElementType T(string name, string? profile = null) => new(name, profile);

    public static ElementModel Elem(
        string path,
        int min = 0,
        string max = "1",
        bool inherited = false,
        string? basePath = null,
        IReadOnlyList<ElementType>? types = null,
        IReadOnlyList<string>? targets = null,
        string? slice = null)
    {
        string root = ElementModel.ComputeRootRelativePath(path);
        int dot = path.LastIndexOf('.');
        string name = dot < 0 ? path : path[(dot + 1)..];
        return new ElementModel(
            Path: path,
            RootRelativePath: root,
            NormalizedKey: ElementModel.ComputeNormalizedKey(root),
            Name: name,
            SliceName: slice,
            Min: min,
            MaxString: max,
            IsInherited: inherited,
            BasePath: basePath ?? path,
            TypeLiteral: string.Empty,
            Types: types ?? [],
            TargetProfiles: targets ?? []);
    }

    public static StructureModel Struct(string name, string kind, params ElementModel[] elements) => new(
        Name: name,
        Kind: kind,
        Derivation: "specialization",
        IsAbstract: false,
        BaseDefinition: null,
        FhirType: name,
        WorkGroup: null,
        SnapshotCount: elements.Length,
        Elements: elements);

    public static ReleaseModel Release(ReleaseId id, params StructureModel[] structures) => new(
        new ResolvedRelease(id, "test.db", 1, "hl7.fhir.test", "x.y.z", null),
        structures);
}
