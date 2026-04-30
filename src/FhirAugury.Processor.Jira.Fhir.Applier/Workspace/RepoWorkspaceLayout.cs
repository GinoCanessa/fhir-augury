namespace FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

/// <summary>
/// Pure-function path helpers for the per-(ticket, repo) workspace layout. Mirrors the
/// GitHub source's <c>safeName = owner_name</c> convention so on-disk repo directories
/// look identical in shape to <c>cache/github/repos/&lt;safeName&gt;/clone/</c> and a
/// downstream indexer can join Applier output back to the GitHub cache by safeName.
/// </summary>
public static class RepoWorkspaceLayout
{
    public const string ClonesSubDir = "clones";
    public const string BaselinesSubDir = "baselines";
    public const string WorktreesSubDir = "worktrees";

    public static string SafeName(string owner, string name) => $"{owner}_{name}";

    public static string SafeName(string repoFullName)
    {
        if (string.IsNullOrWhiteSpace(repoFullName))
        {
            throw new ArgumentException("repoFullName must be non-empty.", nameof(repoFullName));
        }
        return repoFullName.Replace('/', '_');
    }

    public static string PrimaryClonePath(string workingDirectory, string owner, string name)
        => Path.Combine(workingDirectory, ClonesSubDir, SafeName(owner, name));

    public static string BaselinePath(string workingDirectory, string owner, string name)
        => Path.Combine(workingDirectory, BaselinesSubDir, SafeName(owner, name));

    public static string WorktreePath(string workingDirectory, string owner, string name, string ticketKey)
        => Path.Combine(workingDirectory, WorktreesSubDir, SafeName(owner, name), ticketKey);

    public static string OutputPath(string outputDirectory, string ticketKey, string owner, string name)
        => Path.Combine(outputDirectory, ticketKey, SafeName(owner, name));
}
