namespace FhirAugury.Processing.Jira.Common.Api;

public sealed record JiraProcessingEnqueueTicketResponse(
    string Id,
    string Key,
    string? ProcessingStatus,
    string? RunId = null,
    string? RunItemId = null);
