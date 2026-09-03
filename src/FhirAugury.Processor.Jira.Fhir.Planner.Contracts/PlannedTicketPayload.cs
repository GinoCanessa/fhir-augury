namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;

/// <summary>
/// Wire-shape of a single planned ticket as written by the
/// <c>ticket-plan</c> agent. Field shape is a 1:1 mirror of the
/// existing planner record columns; payload-level validation is
/// performed by <see cref="PlannedTicketPayloadValidator"/>.
/// </summary>
public sealed class PlannedTicketPayload
{
    public required string Key { get; set; }
    public string Resolution { get; set; } = string.Empty;
    public string ResolutionSummary { get; set; } = string.Empty;
    public string FeatureProposal { get; set; } = string.Empty;
    public string DesignRationale { get; set; } = string.Empty;
    public DateTimeOffset? SavedAt { get; set; }
    public List<PlannedTicketRepoPayload> Repos { get; set; } = [];
    public List<PlannedTicketRepoChangePayload> RepoChanges { get; set; } = [];
    public List<PlannedTicketRepoImpactPayload> RepoImpacts { get; set; } = [];
    public List<PlannedTicketChangeValidationPayload> ChangeValidations { get; set; } = [];
    public List<PlannedTicketTestingConsiderationPayload> TestingConsiderations { get; set; } = [];
    public List<PlannedTicketOpenQuestionPayload> OpenQuestions { get; set; } = [];
}

public sealed class PlannedTicketRepoPayload
{
    public required string RepoKey { get; set; }
    public string? RepoRevision { get; set; }
    public string Justification { get; set; } = string.Empty;
}

public sealed class PlannedTicketRepoChangePayload
{
    public required string TicketRepoId { get; set; }
    public required string RepoKey { get; set; }
    public int ChangeSequence { get; set; }
    public required string FilePath { get; set; }
    public string ChangeTitle { get; set; } = string.Empty;
    public string ChangeDescription { get; set; } = string.Empty;
    public int? SourceLineStart { get; set; }
    public int? SourceLineEnd { get; set; }
    public List<string> ReplacementLines { get; set; } = [];
    public string Reason { get; set; } = string.Empty;
}

public sealed class PlannedTicketRepoImpactPayload
{
    public required string TicketRepoId { get; set; }
    public required string RepoKey { get; set; }
    public string? TicketRepoChangeId { get; set; }
    public required string AffectedFilePath { get; set; }
    public string HowAffected { get; set; } = string.Empty;
}

public sealed class PlannedTicketChangeValidationPayload
{
    public required string TicketRepoId { get; set; }
    public required string RepoKey { get; set; }
    public int ValidationSequence { get; set; }
    public string Action { get; set; } = string.Empty;
}

public sealed class PlannedTicketTestingConsiderationPayload
{
    public required string TicketRepoId { get; set; }
    public required string RepoKey { get; set; }
    public int ConsiderationSequence { get; set; }
    public string Consideration { get; set; } = string.Empty;
}

public sealed class PlannedTicketOpenQuestionPayload
{
    public required string TicketRepoId { get; set; }
    public required string RepoKey { get; set; }
    public int QuestionSequence { get; set; }
    public string Question { get; set; } = string.Empty;
}
