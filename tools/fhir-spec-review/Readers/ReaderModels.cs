namespace FhirAugury.Tools.FhirSpecReview.Readers;

/// <summary>A narrative spec page enumerated from <c>publish.ini</c> (or the glob fallback).</summary>
/// <param name="PageFileName">Page file name relative to <c>source/</c> (e.g. <c>datatypes.html</c>).</param>
/// <param name="Label">Human label from <c>[page-titles]</c>, when present.</param>
/// <param name="ExistsInPublishIni">True if listed in <c>publish.ini</c>; null when enumerated via glob fallback.</param>
/// <param name="ExistsInSource">True if the file exists in the clone tree.</param>
/// <param name="WorkGroupCode">Work-group code from the spec file map, when resolvable.</param>
/// <param name="WorkGroupName">Work-group display name, when resolvable.</param>
internal sealed record NarrativePageInfo(
    string PageFileName,
    string? Label,
    bool? ExistsInPublishIni,
    bool ExistsInSource,
    string? WorkGroupCode,
    string? WorkGroupName);

/// <summary>A FHIR artifact (resource / interface / profile) derived from the current-build structure definitions.</summary>
/// <param name="FhirId">Canonical id (last URL segment, e.g. <c>Patient</c> or <c>bp</c>).</param>
/// <param name="Name">Computer-friendly name.</param>
/// <param name="ArtifactType">Artifact class, lower-cased (resource / interface / profile / ...).</param>
/// <param name="SourceDirRelative">Expected source directory relative to the clone root, or null when not derivable.</param>
/// <param name="SourceDirectoryExists">Whether the expected source directory exists.</param>
/// <param name="SourceDefinitionExists">Whether the expected definition file exists.</param>
/// <param name="IntroPageFilename">Intro page file name if present in the source directory; otherwise null.</param>
/// <param name="NotesPageFilename">Notes page file name if present in the source directory; otherwise null.</param>
/// <param name="WorkGroupCode">Responsible work-group code, when known.</param>
/// <param name="WorkGroupName">Responsible work-group display name, when known.</param>
/// <param name="Status">Publication status.</param>
/// <param name="MaturityLevel">FHIR Maturity Model level.</param>
/// <param name="StandardsStatus">Standards status.</param>
/// <param name="CanonicalUrl">Source canonical URL (<c>sd.Url</c>) the <see cref="FhirId"/> was derived from; null when unavailable.</param>
internal sealed record ArtifactInfo(
    string FhirId,
    string Name,
    string ArtifactType,
    string? SourceDirRelative,
    bool? SourceDirectoryExists,
    bool? SourceDefinitionExists,
    string? IntroPageFilename,
    string? NotesPageFilename,
    string? WorkGroupCode,
    string? WorkGroupName,
    string? Status,
    int? MaturityLevel,
    string? StandardsStatus,
    string? CanonicalUrl);

/// <summary>One element-review row sourced from the current-build R6 vocabulary.</summary>
internal sealed record ArtifactElementDetail(
    string Path,
    bool IsRequired,
    string? MaxCardinality,
    bool IsTrialUse,
    bool HasFixed,
    bool HasPattern,
    bool RequiredBinding,
    string? RequiredBindingValueSet,
    bool ExternalRequiredBinding,
    string? MeaningWhenMissing,
    bool IsModifier,
    int ElementOrder);

/// <summary>One operation-inventory row sourced from the current-build R6 vocabulary.</summary>
internal sealed record ArtifactOperationDetail(
    string OperationId,
    string? Code,
    string? Name,
    string? OperationKind,
    string? Status,
    string? StandardsStatus,
    int? FhirMaturity,
    bool? IsExperimental,
    string? WorkGroup,
    string? Description,
    int OperationOrder);

/// <summary>One search-parameter-inventory row sourced from the current-build R6 vocabulary.</summary>
internal sealed record ArtifactSearchParameterDetail(
    string SearchParamId,
    string? Name,
    string? Status,
    int? FhirMaturity,
    string? StandardsStatus,
    bool? IsExperimental,
    string? WorkGroup,
    string? SearchType,
    string? Description,
    int ParamOrder);
