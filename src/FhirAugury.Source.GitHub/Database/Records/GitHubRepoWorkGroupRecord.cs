using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>
/// Per-repo derived default HL7 work-group attribution.
/// </summary>
/// <remarks>
/// Lives in its own table (rather than as a column on
/// <see cref="GitHubRepoRecord"/>) so that API-driven repo upserts in
/// <c>GitHubRestProvider</c> / <c>GitHubCliProvider</c> — which fully
/// rewrite the <c>github_repos</c> row from <c>MapRepo</c> output — cannot
/// blank out the value derived by <c>WorkGroupResolutionPass</c>.
/// </remarks>
[LdgSQLiteTable("github_repo_workgroups")]
[LdgSQLiteIndex(nameof(WorkGroup))]
public partial record class GitHubRepoWorkGroupRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>Owner/Name (e.g. <c>HL7/fhir</c>). One row per repo.</summary>
    [LdgSQLiteUnique]
    public required string RepoFullName { get; set; }

    /// <summary>Canonical HL7 work-group <c>code</c>, or <c>null</c> when no signal could be derived.</summary>
    public string? WorkGroup { get; set; }

    /// <summary>
    /// Original input that produced this row (config override value, or the
    /// majority free-text WG from JIRA-Spec). Preserved when it didn't
    /// resolve, or when it resolved to a code that differed from the input.
    /// </summary>
    public string? WorkGroupRaw { get; set; }

    /// <summary>
    /// Provenance for the derivation: <c>"config"</c> (explicit override
    /// from <c>GitHubServiceOptions.RepoOverrides</c>) or
    /// <c>"majority-jira-spec"</c> (most common WG across this repo's
    /// JIRA-Spec rows).
    /// </summary>
    public required string Source { get; set; }

    public required DateTimeOffset ResolvedAt { get; set; }
}
