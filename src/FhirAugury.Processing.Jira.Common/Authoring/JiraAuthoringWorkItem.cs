using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Database.Records;

namespace FhirAugury.Processing.Jira.Common.Authoring;

public sealed record JiraAuthoringWorkItem(
    AuthoringRunItemRecord RunItem,
    JiraProcessingSourceTicketRecord SourceTicket);
