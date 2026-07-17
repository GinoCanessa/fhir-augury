namespace FhirAugury.Common.Api;

// ── Summary & detail types ──────────────────────────────────────

/// <summary>Lightweight summary of a Jira specification for list endpoints.</summary>
public record JiraSpecSummary(
    string SpecKey,
    string Family,
    string? SpecName,
    string? CanonicalUrl,
    string? GitUrl,
    string? DefaultWorkgroup,
    string DefaultVersion,
    int ArtifactCount,
    int PageCount,
    int VersionCount);

/// <summary>Full specification detail with child collections.</summary>
public record JiraSpecDetail(
    JiraSpecSummary Summary,
    List<JiraSpecVersionInfo> Versions,
    List<JiraSpecArtifactInfo> Artifacts,
    List<JiraSpecPageInfo> Pages);

// ── Child info records ──────────────────────────────────────────

/// <summary>Published version of a specification.</summary>
public record JiraSpecVersionInfo(string Code, string? Url, bool Deprecated);

/// <summary>Artifact entry within a specification.</summary>
public record JiraSpecArtifactInfo(
    string ArtifactKey,
    string Name,
    string? ArtifactId,
    string? ResourceType,
    string? Workgroup,
    bool Deprecated);

/// <summary>Page entry within a specification.</summary>
public record JiraSpecPageInfo(
    string PageKey,
    string Name,
    string? Url,
    string? Workgroup,
    bool Deprecated);

// ── Resolution results ──────────────────────────────────────────

/// <summary>Result of resolving a Jira artifact key to its specification context.</summary>
public record JiraArtifactResolution(
    string ArtifactKey,
    string SpecKey,
    string Family,
    string? SpecName,
    string? ArtifactId,
    string? ResourceType,
    string ArtifactName,
    string? CanonicalUrl,
    bool Deprecated);

/// <summary>Result of resolving a Jira page key to its specification context.</summary>
public record JiraPageResolution(
    string PageKey,
    string SpecKey,
    string Family,
    string? SpecName,
    string PageName,
    string? Url,
    string? CanonicalUrl,
    bool Deprecated);

// ── Workgroup & family info ─────────────────────────────────────

/// <summary>Workgroup metadata with associated spec count.</summary>
public record JiraWorkgroupInfo(
    string Key,
    string Name,
    string? Webcode,
    string? Listserv,
    bool Deprecated,
    int SpecCount);

/// <summary>Product family summary with spec counts.</summary>
public record JiraFamilyInfo(string Family, int SpecCount, int ActiveSpecCount);

// ── Response wrappers ───────────────────────────────────────────

/// <summary>Response containing a list of Jira specifications.</summary>
public record JiraSpecListResponse(List<JiraSpecSummary> Specifications);

/// <summary>Response containing a list of workgroups.</summary>
public record JiraWorkgroupListResponse(List<JiraWorkgroupInfo> Workgroups);

/// <summary>Response containing a list of product families.</summary>
public record JiraFamilyListResponse(List<JiraFamilyInfo> Families);
