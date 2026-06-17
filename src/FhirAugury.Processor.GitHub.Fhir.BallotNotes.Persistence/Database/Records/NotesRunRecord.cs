using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

/// <summary>
/// Provenance and lifecycle for a notes run: the repo + since-commit window a
/// batch of notes was hydrated against. The report SPA shows the most recent
/// row in its header (reading <c>RepoOwner</c>…<c>RunAt</c>). The status columns
/// (<see cref="Status"/>, counters, timestamps) drive the processor's
/// <c>202</c>-accepted / poll model and are additive, so the SPA read is
/// unaffected.
/// </summary>
[LdgSQLiteTable("notes_runs")]
public partial record class NotesRunRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    /// <summary>Stable key for the window: <c>owner/name@sinceSha..headSha</c>.</summary>
    [LdgSQLiteUnique]
    public required string RunKey { get; set; }

    public required string RepoOwner { get; set; }
    public required string RepoName { get; set; }
    public string RepoCategory { get; set; } = string.Empty;

    public string SinceSha { get; set; } = string.Empty;
    public string SinceShortSha { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public string HeadShortSha { get; set; } = string.Empty;

    /// <summary>Run state: <c>running</c>, <c>completed</c>, or <c>failed</c>.</summary>
    public string Status { get; set; } = "running";

    /// <summary>Total units discovered for the window (set after grouping).</summary>
    public int UnitsTotal { get; set; }

    /// <summary>Units whose evidence has been hydrated so far.</summary>
    public int UnitsHydrated { get; set; }

    /// <summary>Total commits across all units' windows (cumulative).</summary>
    public int CommitsInWindow { get; set; }

    /// <summary>Total tickets attributed across all units (cumulative).</summary>
    public int TicketsAttributed { get; set; }

    /// <summary>When the run began.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the run reached a terminal state (<c>completed</c>/<c>failed</c>).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Failure detail when <see cref="Status"/> is <c>failed</c>; otherwise empty.</summary>
    public string Error { get; set; } = string.Empty;

    public required DateTimeOffset RunAt { get; set; }
}
