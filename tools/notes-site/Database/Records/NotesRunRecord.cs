using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.NotesSite.Database.Records;

/// <summary>
/// Provenance for a notes run: the repo + since-commit window that a batch of
/// notes was drafted against. The report SPA shows the most recent row in its
/// header. Upserted by the <c>write</c> verb (latest <see cref="RunAt"/> wins).
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

    public required DateTimeOffset RunAt { get; set; }
}
