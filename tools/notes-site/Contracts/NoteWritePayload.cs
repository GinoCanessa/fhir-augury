namespace FhirAugury.Tools.NotesSite.Contracts;

/// <summary>
/// The write contract for a single drafted ballot note. Authored by the
/// <c>notes-artifact</c> / <c>notes-page</c> / <c>notes-datatype</c> skills and
/// passed to <c>notes-site write</c> as JSON (via <c>--in &lt;file&gt;</c> or
/// stdin). Mirrors the codebase's <c>prepared-ticket-write</c> direct-DB-write
/// pattern. Deserialized case-insensitively.
/// </summary>
public sealed class NoteWritePayload
{
    /// <summary>Unit kind: <c>Artifact</c>, <c>Page</c>, or <c>DataType</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Unit name (e.g. <c>Observation</c>, <c>security</c>, <c>datatypes</c>).</summary>
    public string Name { get; set; } = string.Empty;

    public string RepoOwner { get; set; } = string.Empty;
    public string RepoName { get; set; } = string.Empty;
    public string RepoCategory { get; set; } = string.Empty;

    public string WorkGroup { get; set; } = string.Empty;
    public string WorkGroupCode { get; set; } = string.Empty;

    public string SinceSha { get; set; } = string.Empty;
    public string SinceShortSha { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public string HeadShortSha { get; set; } = string.Empty;

    public int CommitsInWindow { get; set; }
    public int TicketsAttributed { get; set; }

    /// <summary>Recommendation flag: <c>yes</c>, <c>no</c>, or <c>unknown</c>.</summary>
    public string NeedsNote { get; set; } = "unknown";

    public string CurrentBallotNoteHtml { get; set; } = string.Empty;
    public string ProposedBallotNoteHtml { get; set; } = string.Empty;
    public string RollupSummaryMarkdown { get; set; } = string.Empty;
    public string NotesForReviewerMarkdown { get; set; } = string.Empty;
    public string SourceFilesNote { get; set; } = string.Empty;

    public DateTimeOffset? GeneratedAt { get; set; }

    public List<NoteSourceFilePayload> SourceFiles { get; set; } = [];
    public List<NoteCommitPayload> Commits { get; set; } = [];
    public List<NoteTicketPayload> Tickets { get; set; } = [];
}

/// <summary>A source file considered part of the note's unit.</summary>
public sealed class NoteSourceFilePayload
{
    public string Path { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool TouchedInWindow { get; set; }
}

/// <summary>A commit in the window with its attributed ticket keys.</summary>
public sealed class NoteCommitPayload
{
    public string Sha { get; set; } = string.Empty;
    public string ShortSha { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorDate { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
    public List<string> TicketKeys { get; set; } = [];
}

/// <summary>A Jira ticket attributed to the note's unit within the window.</summary>
public sealed class NoteTicketPayload
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string WorkGroup { get; set; } = string.Empty;
    public string Specification { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int CommitCount { get; set; }
}
