using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Filtering;

namespace FhirAugury.Processing.Jira.Common.Database.Records;

[LdgSQLiteTable("jira_processing_source_tickets")]
[LdgSQLiteIndex(nameof(Key))]
[LdgSQLiteIndex(nameof(Project))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(WorkGroup))]
[LdgSQLiteIndex(nameof(Type))]
[LdgSQLiteIndex(nameof(SourceTicketShape))]
[LdgSQLiteIndex(nameof(LastUpdated))]
[LdgSQLiteIndex(nameof(ProcessingStatus))]
[LdgSQLiteIndex(nameof(StartedProcessingAt))]
[LdgSQLiteIndex(nameof(CompletedProcessingAt))]
[LdgSQLiteIndex(nameof(LastProcessingAttemptAt))]
public partial record class JiraProcessingSourceTicketRecord : IProcessingWorkItem, IJiraProcessingTicketFilterCandidate
{
    [LdgSQLiteKey]
    public int RowId { get; set; }
    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Key { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Project { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string WorkGroup { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string SourceTicketShape { get; set; } = "fhir";
    public DateTimeOffset LastSyncedAt { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    public DateTimeOffset? StartedProcessingAt { get; set; }
    public DateTimeOffset? CompletedProcessingAt { get; set; }
    public DateTimeOffset? LastProcessingAttemptAt { get; set; }
    public string? ProcessingStatus { get; set; }
    public string? ProcessingError { get; set; }
    public int ProcessingAttemptCount { get; set; }
    public string? ErrorMessage { get; set; }
    public int? AgentExitCode { get; set; }
    public DateTimeOffset? ErrorOccurredAt { get; set; }
}
