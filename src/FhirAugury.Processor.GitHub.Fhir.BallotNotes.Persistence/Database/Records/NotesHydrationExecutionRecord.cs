using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

[LdgSQLiteTable("notes_hydration_executions")]
[LdgSQLiteIndex(nameof(RunKey))]
[LdgSQLiteIndex(nameof(Status))]
public partial record class NotesHydrationExecutionRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string Id { get; set; }

    public required string RunKey { get; set; }
    public required string RepoOwner { get; set; }
    public required string RepoName { get; set; }
    public string RepoCategory { get; set; } = string.Empty;
    public required string SinceSha { get; set; }
    public required string SinceShortSha { get; set; }
    public required string HeadSha { get; set; }
    public required string HeadShortSha { get; set; }
    public string WindowLabel { get; set; } = string.Empty;
    public required string Status { get; set; }
    public string MutationRunId { get; set; } = string.Empty;
    public string MutationLeaseId { get; set; } = string.Empty;
    public int UnitsTotal { get; set; }
    public int UnitsHydrated { get; set; }
    public int CommitsInWindow { get; set; }
    public int TicketsAttributed { get; set; }
    public bool IsCutoverBaseline { get; set; }
    public required DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Error { get; set; } = string.Empty;
}
