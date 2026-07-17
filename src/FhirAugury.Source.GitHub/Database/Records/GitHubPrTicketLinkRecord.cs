using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>
/// A first-class, many-to-many edge between a pull request and a Jira ticket,
/// projected from the already-extracted <c>xref_jira</c> rows. One logical edge
/// per <c>(RepoFullName, PrNumber, JiraKey)</c>; <see cref="Provenance"/> holds
/// the sorted, comma-joined set of sources that contributed the edge
/// (<c>description</c> / <c>comment</c> / <c>commit</c>).
/// </summary>
[LdgSQLiteTable("github_pr_ticket_links")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(PrNumber))]
[LdgSQLiteIndex(nameof(JiraKey))]
public partial record class GitHubPrTicketLinkRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }
    public required int PrNumber { get; set; }

    /// <summary>PR unique key in format "owner/repo#N".</summary>
    public required string PrUniqueKey { get; set; }

    public required string JiraKey { get; set; }

    /// <summary>Sorted, comma-joined provenance set: any of "description", "comment", "commit".</summary>
    public required string Provenance { get; set; }
}
