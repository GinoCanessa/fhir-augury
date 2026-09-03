using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;

namespace FhirAugury.Tools.TicketMdToDb.Hydration;

public static class PreparedSourceTicketProjector
{
    public static bool IsUsableSelfRow(HydrationJiraRow row) =>
        string.Equals(row.HydrationStatus, "resolved", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(row.Title)
        && !string.IsNullOrWhiteSpace(row.Status)
        && !string.IsNullOrWhiteSpace(row.Type)
        && !string.IsNullOrWhiteSpace(row.WorkGroup)
        && !string.IsNullOrWhiteSpace(row.Specification);

    public static async Task<JiraProcessingSourceTicketRecord?> ProjectAsync(
        HydrationBatch batch,
        JiraProcessingSourceTicketStore store,
        DateTimeOffset completedAt,
        CancellationToken ct)
    {
        HydrationJiraRow? self = batch.JiraRows.SingleOrDefault(
            row => string.Equals(row.JiraKey, batch.TicketKey, StringComparison.Ordinal));
        if (self is null || !IsUsableSelfRow(self))
        {
            return null;
        }

        int separator = self.JiraKey.IndexOf('-');
        string project = separator > 0
            ? self.JiraKey[..separator]
            : self.JiraKey;
        JiraIssueSummaryEntry source = new()
        {
            Key = self.JiraKey,
            ProjectKey = project,
            Title = self.Title ?? string.Empty,
            Status = self.Status ?? string.Empty,
            Type = self.Type ?? string.Empty,
            WorkGroup = self.WorkGroup ?? string.Empty,
            Specification = self.Specification ?? string.Empty,
            Priority = self.Priority ?? string.Empty,
            Url = self.Url,
            UpdatedAt = self.UpdatedAt,
        };
        JiraProcessingSourceTicketRecord projected = await store.UpsertAsync(
            source,
            sourceTicketShape: "fhir",
            resetProcessingStatus: false,
            ct);
        await store.MarkCompleteAsync(projected, completedAt, ct);
        if (!string.Equals(projected.ProcessingStatus, ProcessingStatusValues.Complete, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Projected source ticket did not reach complete state: {batch.TicketKey}");
        }

        return projected;
    }
}
