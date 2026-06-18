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

// ── Code systems ─────────────────────────────────────────────────────

/// <summary>Summary metadata for a code system.</summary>
public record CodeSystemSummary(
    string Id,
    string Name,
    string? Title,
    string UnversionedUrl,
    string VersionedUrl,
    string? Status,
    string? StandardStatus,
    string? WorkGroup,
    int? FhirMaturity,
    string? Content,
    string? HierarchyMeaning,
    int? Count,
    string? Description);

/// <summary>A code system's summary plus concept count, hierarchy flag, and property definitions.</summary>
public record CodeSystemDetail(
    CodeSystemSummary Summary,
    int ConceptCount,
    bool HasHierarchy,
    IReadOnlyList<CodeSystemPropertyDef> PropertyDefinitions);

/// <summary>A property definition declared by a code system.</summary>
public record CodeSystemPropertyDef(string Code, string Type, string? Uri, string? Description);

/// <summary>One concept in a code system (optionally with nested children).</summary>
public record ConceptNode(
    string Code,
    string? Display,
    string? Definition,
    IReadOnlyList<ConceptDesignation> Designations,
    IReadOnlyList<ConceptProperty> Properties,
    IReadOnlyList<ConceptNode> Children);

/// <summary>An alternative designation (translation / synonym) for a concept.</summary>
public record ConceptDesignation(string? Language, string? Use, string Value);

/// <summary>A property value attached to a concept.</summary>
public record ConceptProperty(string Code, string Type, string Value);

// ── Value sets ───────────────────────────────────────────────────────

/// <summary>Summary metadata for a value set.</summary>
public record ValueSetSummary(
    string Id,
    string Name,
    string? Title,
    string UnversionedUrl,
    string VersionedUrl,
    string? Status,
    string? StandardStatus,
    string? WorkGroup,
    int? FhirMaturity,
    int ConceptCount,
    string? Description);

/// <summary>A value set's summary plus its compose rules and binding rollups.</summary>
public record ValueSetDetail(
    ValueSetSummary Summary,
    IReadOnlyList<ComposeRule> Compose,
    IReadOnlyList<string> ReferencedSystems,
    string? StrongestBindingCore,
    int BindingCountCore,
    int BindingCountExtended);

/// <summary>One include/exclude rule within a value set's compose.</summary>
public record ComposeRule(
    string Mode,
    string? System,
    string? Version,
    IReadOnlyList<ComposeConcept> Concepts,
    IReadOnlyList<ComposeFilter> Filters,
    IReadOnlyList<string> ValueSets);

/// <summary>An explicitly enumerated concept in a compose rule.</summary>
public record ComposeConcept(string Code, string? Display);

/// <summary>A filter within a compose rule (property op value).</summary>
public record ComposeFilter(string Property, string Op, string Value);

/// <summary>One concept in a value set's expansion.</summary>
public record ValueSetConceptInfo(string System, string Code, string? Display, bool Inactive, bool Abstract);

/// <summary>An element that binds to a value set (reverse binding).</summary>
public record ElementBindingRef(string Resource, string Path, string? Strength);
