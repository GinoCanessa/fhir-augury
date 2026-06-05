namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

/// <summary>
/// Database boundary the shared <see cref="HydrationCoordinator"/> /
/// <see cref="HydrationSweeper"/> speak through. Each concrete
/// processor-side database (preparer / planner) implements this in
/// addition to its own service-specific persistence surface.
/// </summary>
/// <remarks>
/// The <c>ticketKey</c> parameter on these methods is logical — the
/// implementation may map it to whatever column name its schema uses
/// (e.g. <c>TicketKey</c> for preparer, <c>IssueKey</c> for planner).
/// </remarks>
public interface IHydrationTargetDatabase
{
    /// <summary>Absolute path of the on-disk SQLite file backing this database.</summary>
    string DatabasePath { get; }

    Task<IReadOnlyList<string>> ListRelatedJiraKeysForTicketAsync(string ticketKey, CancellationToken ct);

    Task<IReadOnlyList<string>> ListRelatedZulipThreadIdsForTicketAsync(string ticketKey, CancellationToken ct);

    Task<IReadOnlyList<string>> ListRelatedGitHubItemIdsForTicketAsync(string ticketKey, CancellationToken ct);

    Task<IReadOnlyList<string>> ListReposForTicketAsync(string ticketKey, CancellationToken ct);

    Task<IReadOnlyList<string>> ListUnresolvedOrMissingHydrationKeysAsync(CancellationToken ct);

    Task SaveHydrationAsync(HydrationBatch batch, CancellationToken ct);
}
