namespace FhirAugury.Processor.Jira.Fhir.Applier.Configuration;

/// <summary>
/// Per-repo configuration for the applier. <see cref="BuildCommand"/> is a template
/// rendered by <c>BuildCommandRunner</c>; <see cref="OutputRoots"/> scopes both baseline
/// snapshots and post-build diffs so plain source-tree edits never participate in the
/// review output set.
/// </summary>
public sealed class ApplierRepoOptions
{
    public required string Owner { get; set; }
    public required string Name { get; set; }
    public string PrimaryBranch { get; set; } = "main";
    public string BuildCommand { get; set; } = string.Empty;

    /// <summary>
    /// Repo-relative globs identifying the files that participate in baseline snapshots
    /// and post-build diffs. Defaults to <c>output/**</c> for FHIR IG-style builds.
    /// </summary>
    public List<string> OutputRoots { get; set; } = ["output/**"];

    public string FullName => $"{Owner}/{Name}";
}
