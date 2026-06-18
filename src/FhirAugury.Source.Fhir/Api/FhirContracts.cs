namespace FhirAugury.Source.Fhir.Api;

/// <summary>A FHIR release package available in the spec database.</summary>
public record ReleaseInfo(
    int Key,
    string ShortName,
    string FhirVersion,
    string PackageId,
    string PackageVersion,
    string? Title);

/// <summary>
/// Envelope that echoes the resolved release alongside a release-scoped result,
/// so callers always see which release answered their request.
/// </summary>
public record FhirReleaseResponse<T>(ReleaseInfo Release, T Result);

/// <summary>High-level artifact counts for the spec database (used by /stats).</summary>
public record FhirSpecCounts(
    int Releases,
    int Structures,
    int CodeSystems,
    int ValueSets,
    int Operations,
    int SearchParameters);

// ── Structures ───────────────────────────────────────────────────────

/// <summary>Summary metadata for a structure (resource / datatype / profile / interface).</summary>
public record StructureSummary(
    string Id,
    string Name,
    string? Title,
    string ArtifactClass,
    string? Kind,
    string? FhirType,
    string? BaseDefinition,
    bool? IsAbstract,
    string? Status,
    string? StandardStatus,
    string? WorkGroup,
    int? FhirMaturity,
    string UnversionedUrl,
    string VersionedUrl,
    string? Description);

/// <summary>A structure's summary plus its (optionally nested) element tree.</summary>
public record StructureDetail(
    StructureSummary Summary,
    IReadOnlyList<ElementNode> Elements);

/// <summary>One element in a structure's element tree.</summary>
public record ElementNode(
    string Id,
    string Path,
    string Name,
    string? SliceName,
    int Min,
    string Max,
    string? Short,
    string? Definition,
    string TypeLiteral,
    IReadOnlyList<ElementTypeInfo> Types,
    BindingInfo? Binding,
    bool IsModifier,
    string? IsModifierReason,
    bool IsInherited,
    string? StandardStatus,
    string? FixedValue,
    string? PatternValue,
    string? MeaningWhenMissing,
    IReadOnlyList<ElementNode> Children);

/// <summary>A type allowed on an element, with any profile / target-profile constraints.</summary>
public record ElementTypeInfo(
    string Code,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> TargetProfiles);

/// <summary>A terminology binding on an element.</summary>
public record BindingInfo(
    string? Strength,
    string? ValueSetUrl,
    string? ValueSetName,
    IReadOnlyList<AdditionalBindingInfo> AdditionalBindings);

/// <summary>An additional (non-primary) terminology binding on an element.</summary>
public record AdditionalBindingInfo(
    string? Purpose,
    string? ValueSetUrl,
    string? Documentation);
