using System.Globalization;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database.Records;

namespace FhirAugury.Processing.Jira.Common.Authoring;

/// <summary>
/// Computes the source revision frozen into Jira authoring receipts.
/// </summary>
public static class JiraSourceRevision
{
    public static string Compute(JiraProcessingSourceTicketRecord ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return Compute(
            ticket.LastUpdated,
            ticket.Key,
            ticket.Title,
            ticket.Status,
            ticket.WorkGroup,
            ticket.Type,
            ticket.Specification);
    }

    public static string Compute(
        DateTimeOffset? updatedAt,
        string? key,
        string? title,
        string? status,
        string? workGroup,
        string? type,
        string? specification)
    {
        if (updatedAt is not null)
        {
            return updatedAt.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        return AuthoringResultHasher.HashNormalizedUtf8(
            string.Join(
                "\n",
                key,
                title,
                status,
                workGroup,
                type,
                specification));
    }

    public static string Compute(
        string? key,
        string? title,
        string? status,
        string? workGroup,
        string? type,
        string? specification,
        DateTimeOffset? updatedAt)
        => Compute(
            updatedAt,
            key,
            title,
            status,
            workGroup,
            type,
            specification);
}
