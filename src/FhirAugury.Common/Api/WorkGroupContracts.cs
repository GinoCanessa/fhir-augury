namespace FhirAugury.Common.Api;

// ── Page metadata ───────────────────────────────────────────────

/// <summary>Generic pagination metadata for list responses.</summary>
public record PageMetadata(int Total, int Limit, int Offset);

// ── Work-group summary & list ──────────────────────────────────

/// <summary>Per-repo file/artifact counts for a single canonical work-group.</summary>
public record WorkGroupRepoCount(string RepoFullName, int FileCount, int ArtifactCount);

/// <summary>Canonical HL7 work-group with cross-repo coverage counts.</summary>
public record WorkGroupSummary(
    string Code,
    string Name,
    bool Retired,
    int TotalFileCount,
    int TotalArtifactCount,
    List<WorkGroupRepoCount> Repos);

/// <summary>Response for <c>GET /workgroups</c>.</summary>
public record WorkGroupListResponse(List<WorkGroupSummary> WorkGroups);

// ── File / artifact rows ───────────────────────────────────────

/// <summary>A single work-group attributed file row from spec_file_map.</summary>
public record WorkGroupFileItem(
    string RepoFullName,
    string ArtifactKey,
    string FilePath,
    string MapType,
    string? WorkGroup,
    string? WorkGroupRaw);

/// <summary>Response for <c>GET /workgroups/files</c>.</summary>
public record WorkGroupFileListResponse(List<WorkGroupFileItem> Files, PageMetadata Page);

/// <summary>A single work-group attributed artifact row from canonical_artifacts or structure_definitions.</summary>
public record WorkGroupArtifactItem(
    string Source,
    string RepoFullName,
    string Name,
    string? ResourceType,
    string FilePath,
    string Url,
    string? WorkGroup,
    string? WorkGroupRaw);

/// <summary>Response for <c>GET /workgroups/artifacts</c>.</summary>
public record WorkGroupArtifactListResponse(List<WorkGroupArtifactItem> Artifacts, PageMetadata Page);

// ── Resolve ─────────────────────────────────────────────────────

/// <summary>Result of <c>GET /workgroups/resolve</c> for a (repo, path) pair.</summary>
public record WorkGroupResolveResponse(
    string RepoFullName,
    string Path,
    string? WorkGroup,
    string? WorkGroupRaw,
    string MatchedStage);

// ── Unresolved review ───────────────────────────────────────────

/// <summary>One example occurrence of an unresolved <c>WorkGroupRaw</c> input.</summary>
public record WorkGroupUnresolvedExample(
    string Table,
    string RepoFullName,
    string PathOrKey);

/// <summary>Aggregated unresolved <c>WorkGroupRaw</c> value with a small example set.</summary>
public record WorkGroupUnresolvedItem(
    string WorkGroupRaw,
    int OccurrenceCount,
    List<WorkGroupUnresolvedExample> Examples);

/// <summary>Response for <c>GET /workgroups/unresolved</c>.</summary>
public record WorkGroupUnresolvedListResponse(List<WorkGroupUnresolvedItem> Items, PageMetadata Page);
