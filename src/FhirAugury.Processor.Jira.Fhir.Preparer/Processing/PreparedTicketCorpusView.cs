using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

/// <summary>
/// Resolves the immutable carried coordinates and complete revised staging
/// for one publication-reconciliation run without exposing staged rows to
/// ordinary canonical readers.
/// </summary>
public sealed class PreparedTicketCorpusView(PreparerDatabase database)
{
    private readonly PreparerDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public Task<PreparedTicketPublicationCorpusOverlay> GetAsync(
        string runId,
        CancellationToken ct = default)
        => _database.GetPublicationReconciliationCorpusAsync(runId, ct);
}
