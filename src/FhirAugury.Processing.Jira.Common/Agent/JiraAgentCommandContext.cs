namespace FhirAugury.Processing.Jira.Common.Agent;

public sealed record JiraAgentCommandContext
{
    public required string TicketKey { get; init; }
    public required string SourceTicketId { get; init; }
    public required string DatabasePath { get; init; }
    public string SourceTicketShape { get; init; } = "fhir";
    public IReadOnlyDictionary<string, string> ExtensionTokens { get; init; } = new Dictionary<string, string>();
    public bool IsAuthoringWorker { get; init; }
    public string? RunId { get; init; }
    public string? RunItemId { get; init; }
    public string? CallbackUrl { get; init; }
    public string? OperationId { get; init; }
    public string? OperationToken { get; init; }
    public string? ExpectedSourceRevision { get; init; }

    public void Validate()
    {
        string?[] workerValues =
        [
            RunId,
            RunItemId,
            CallbackUrl,
            OperationId,
            OperationToken,
            ExpectedSourceRevision,
        ];
        if (!IsAuthoringWorker)
        {
            if (workerValues.Any(value => value is not null))
            {
                throw new InvalidOperationException(
                    "Authoring worker context must not be supplied without FHIR_AUGURY_AUTHORING_WORKER mode.");
            }
            return;
        }

        if (workerValues.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "Authoring worker context requires run, item, callback, operation, token, and source revision values.");
        }
    }

    public override string ToString()
        => IsAuthoringWorker
            ? $"{nameof(JiraAgentCommandContext)} {{ TicketKey = {TicketKey}, RunId = {RunId}, RunItemId = {RunItemId}, OperationId = {OperationId}, OperationToken = [REDACTED] }}"
            : $"{nameof(JiraAgentCommandContext)} {{ TicketKey = {TicketKey}, SourceTicketId = {SourceTicketId}, DatabasePath = {DatabasePath} }}";
}
