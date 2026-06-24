using FhirAugury.Common.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;

/// <summary>
/// The ballot-notes SQLite database owned by the BallotNotes processor.
/// Greenfield, augury-convention cslightdbgen schema on the
/// <see cref="SourceDatabase"/> base (no Jira processing queue). Splits each
/// unit's lifecycle into an evidence half (written by hydration via
/// <see cref="UpsertUnitEvidence"/>) and a prose half (written back by the
/// drafting skills via <see cref="UpdateNoteProse"/>); re-hydration never
/// clobbers authored prose.
/// </summary>
public sealed class BallotNotesDatabase : SourceDatabase
{
    public BallotNotesDatabase(string dbPath, ILogger logger, bool readOnly = false)
        : base(dbPath, logger, readOnly)
    {
    }

    protected override void InitializeSchema(SqliteConnection connection) => EnsureSchema(connection);

    /// <summary>
    /// Idempotent. Creates every notes table via the generated
    /// <c>CREATE TABLE IF NOT EXISTS</c> partials. Safe to call against a
    /// connection this instance does not own (the read-only renderer path uses
    /// it), mirroring <c>PreparerDatabase.EnsureSchema</c>.
    /// </summary>
    public static void EnsureSchema(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        NoteRecord.CreateTable(connection);
        NoteSourceFileRecord.CreateTable(connection);
        NoteCommitRecord.CreateTable(connection);
        NoteTicketRecord.CreateTable(connection);
        NotesRunRecord.CreateTable(connection);
        NoteStructuralChangeRecord.CreateTable(connection);
        NoteExtensionRefRecord.CreateTable(connection);

        // Additive migrations for legacy DBs (cslightdbgen emits no ALTER):
        // back-fill columns added after the tables were first created. Must run
        // after the CreateTable calls so the tables exist to be altered.
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "WindowLabel", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NotesRunRecord.DefaultTableName, "WindowLabel", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteTicketRecord.DefaultTableName, "ChangeImpact", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteTicketRecord.DefaultTableName, "ChangeCategory", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteTicketRecord.DefaultTableName, "RelatedTicketKeys", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteTicketRecord.DefaultTableName, "IssueType", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "CurrentNoteIsAuguryGenerated", "INTEGER NOT NULL DEFAULT 0");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "PreservedHandAuthoredHtml", "TEXT NOT NULL DEFAULT ''");
    }

    /// <summary>Returns the number of notes currently stored.</summary>
    public int CountNotes()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{NoteRecord.DefaultTableName}\"";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Idempotently upserts one unit's hydrated evidence plus its children,
    /// keyed on <see cref="NoteRecord.NoteId"/>. Reads any existing prose +
    /// <see cref="NoteRecord.AuthoredAt"/> first and copies it forward, so a
    /// re-hydration of an already-authored unit never clobbers the note. Sets
    /// <see cref="NoteRecord.HydratedAt"/> and <see cref="NoteRecord.SavedAt"/>;
    /// <see cref="NoteRecord.GeneratedAt"/> is preserved when prose exists and
    /// reset to now otherwise (evidence-only units regenerate on every walk).
    /// </summary>
    public void UpsertUnitEvidence(
        NoteRecord evidence,
        IReadOnlyList<NoteSourceFileRecord> files,
        IReadOnlyList<NoteCommitRecord> commits,
        IReadOnlyList<NoteTicketRecord> tickets,
        IReadOnlyList<NoteStructuralChangeRecord>? structuralChanges = null,
        IReadOnlyList<NoteExtensionRefRecord>? extensionRefs = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(tickets);
        IReadOnlyList<NoteStructuralChangeRecord> structural = structuralChanges ?? [];
        IReadOnlyList<NoteExtensionRefRecord> extensions = extensionRefs ?? [];

        using SqliteConnection connection = OpenConnection();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        evidence.HydratedAt = now;
        evidence.SavedAt = now;

        ExistingNoteProse? existing = ReadExistingProse(connection, evidence.NoteId);
        if (existing is { AuthoredAt: not null } prior)
        {
            // Preserve an authored note across re-hydration: prose, NeedsNote,
            // AuthoredAt, and the authored "Generated" timestamp all carry forward.
            evidence.ProposedBallotNoteHtml = prior.ProposedBallotNoteHtml;
            evidence.RollupSummaryMarkdown = prior.RollupSummaryMarkdown;
            evidence.NotesForReviewerMarkdown = prior.NotesForReviewerMarkdown;
            evidence.SourceFilesNote = prior.SourceFilesNote;
            evidence.NeedsNote = prior.NeedsNote;
            evidence.AuthoredAt = prior.AuthoredAt;
            evidence.GeneratedAt = prior.GeneratedAt;
        }
        else
        {
            // First hydration or an evidence-only re-hydration: the fresh evidence
            // (including any resolver SourceFilesNote) stands and "Generated" is now.
            evidence.GeneratedAt = now;
        }

        DeleteByNoteId(connection, NoteSourceFileRecord.DefaultTableName, evidence.NoteId);
        DeleteByNoteId(connection, NoteCommitRecord.DefaultTableName, evidence.NoteId);
        DeleteByNoteId(connection, NoteTicketRecord.DefaultTableName, evidence.NoteId);
        DeleteByNoteId(connection, NoteStructuralChangeRecord.DefaultTableName, evidence.NoteId);
        DeleteByNoteId(connection, NoteExtensionRefRecord.DefaultTableName, evidence.NoteId);
        DeleteByNoteId(connection, NoteRecord.DefaultTableName, evidence.NoteId);

        connection.Insert(evidence);
        foreach (NoteSourceFileRecord file in files) connection.Insert(file);
        foreach (NoteCommitRecord commit in commits) connection.Insert(commit);
        foreach (NoteTicketRecord ticket in tickets) connection.Insert(ticket);
        foreach (NoteStructuralChangeRecord change in structural) connection.Insert(change);
        foreach (NoteExtensionRefRecord extension in extensions) connection.Insert(extension);
    }

    /// <summary>
    /// Writes back the authored prose for a unit, setting
    /// <see cref="NoteRecord.AuthoredAt"/>, refreshing
    /// <see cref="NoteRecord.GeneratedAt"/> and <see cref="NoteRecord.SavedAt"/>.
    /// Returns <c>false</c> when the slug was never hydrated (prose cannot attach
    /// to a non-existent unit).
    /// </summary>
    public bool UpdateNoteProse(string noteId, BallotNoteProse prose, DateTimeOffset authoredAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(noteId);
        ArgumentNullException.ThrowIfNull(prose);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            UPDATE "{NoteRecord.DefaultTableName}" SET
                NeedsNote = $needsNote,
                ProposedBallotNoteHtml = $proposed,
                RollupSummaryMarkdown = $rollup,
                NotesForReviewerMarkdown = $notes,
                SourceFilesNote = $srcNote,
                AuthoredAt = $authoredAt,
                GeneratedAt = $authoredAt,
                SavedAt = $authoredAt
            WHERE NoteId = $id
            """;
        cmd.Parameters.AddWithValue("$needsNote", string.IsNullOrEmpty(prose.NeedsNote) ? "unknown" : prose.NeedsNote);
        cmd.Parameters.AddWithValue("$proposed", prose.ProposedBallotNoteHtml ?? string.Empty);
        cmd.Parameters.AddWithValue("$rollup", prose.RollupSummaryMarkdown ?? string.Empty);
        cmd.Parameters.AddWithValue("$notes", prose.NotesForReviewerMarkdown ?? string.Empty);
        cmd.Parameters.AddWithValue("$srcNote", prose.SourceFilesNote ?? string.Empty);
        cmd.Parameters.AddWithValue("$authoredAt", authoredAt);
        cmd.Parameters.AddWithValue("$id", noteId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Creates (or resets) the <c>running</c> run row for a window, keyed on
    /// <see cref="NotesRunRecord.RunKey"/>. Called synchronously by the hydrate
    /// endpoint so a pollable status row exists before <c>202</c> is returned.
    /// </summary>
    public void BeginRun(NotesRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        using SqliteConnection connection = OpenConnection();
        DeleteByColumn(connection, NotesRunRecord.DefaultTableName, nameof(NotesRunRecord.RunKey), run.RunKey);
        connection.Insert(run);
    }

    /// <summary>
    /// Records the unit total and resolved HEAD for a run once the background
    /// grouping walk has completed (the values the synchronous
    /// <see cref="BeginRun"/> could not yet know).
    /// </summary>
    public void UpdateRunPlan(string runKey, int unitsTotal, string headSha, string headShortSha)
    {
        ArgumentException.ThrowIfNullOrEmpty(runKey);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            UPDATE "{NotesRunRecord.DefaultTableName}" SET
                UnitsTotal = $total,
                HeadSha = $headSha,
                HeadShortSha = $headShortSha,
                RunAt = $now
            WHERE RunKey = $runKey
            """;
        cmd.Parameters.AddWithValue("$total", unitsTotal);
        cmd.Parameters.AddWithValue("$headSha", headSha ?? string.Empty);
        cmd.Parameters.AddWithValue("$headShortSha", headShortSha ?? string.Empty);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue("$runKey", runKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Updates the hydrated-unit counter and cumulative totals for a running run.</summary>
    public void BumpRunProgress(string runKey, int unitsHydrated, int commitsInWindow, int ticketsAttributed)
    {
        ArgumentException.ThrowIfNullOrEmpty(runKey);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            UPDATE "{NotesRunRecord.DefaultTableName}" SET
                UnitsHydrated = $hydrated,
                CommitsInWindow = $commits,
                TicketsAttributed = $tickets,
                RunAt = $now
            WHERE RunKey = $runKey
            """;
        cmd.Parameters.AddWithValue("$hydrated", unitsHydrated);
        cmd.Parameters.AddWithValue("$commits", commitsInWindow);
        cmd.Parameters.AddWithValue("$tickets", ticketsAttributed);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue("$runKey", runKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Moves a run to a terminal state (<c>completed</c> or <c>failed</c>),
    /// stamping <see cref="NotesRunRecord.CompletedAt"/> and any error detail.
    /// </summary>
    public void FinishRun(string runKey, string status, string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(runKey);
        ArgumentException.ThrowIfNullOrEmpty(status);

        using SqliteConnection connection = OpenConnection();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            UPDATE "{NotesRunRecord.DefaultTableName}" SET
                Status = $status,
                CompletedAt = $now,
                Error = $error,
                RunAt = $now
            WHERE RunKey = $runKey
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$error", error ?? string.Empty);
        cmd.Parameters.AddWithValue("$runKey", runKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Lists notes matching <paramref name="filter"/>, newest-grouping order.</summary>
    public IReadOnlyList<NoteListRow> ListNotes(NoteQueryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();

        List<string> conditions = [];
        if (!string.IsNullOrWhiteSpace(filter.Repo))
        {
            conditions.Add("(RepoOwner || '/' || RepoName) = $repo");
            cmd.Parameters.AddWithValue("$repo", filter.Repo);
        }
        if (!string.IsNullOrWhiteSpace(filter.WorkGroupCode))
        {
            conditions.Add("WorkGroupCode = $wg COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$wg", filter.WorkGroupCode);
        }
        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            conditions.Add("Type = $type COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$type", filter.Type);
        }
        if (!string.IsNullOrWhiteSpace(filter.NeedsNote))
        {
            conditions.Add("NeedsNote = $needsNote COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$needsNote", filter.NeedsNote);
        }
        if (string.Equals(filter.Status, "authored", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add("AuthoredAt IS NOT NULL");
        }
        else if (string.Equals(filter.Status, "awaiting-note", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add("AuthoredAt IS NULL");
        }

        string where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;
        int limit = filter.Limit <= 0 ? 50 : filter.Limit;
        int offset = filter.Offset < 0 ? 0 : filter.Offset;

        cmd.CommandText =
            "SELECT NoteId, Type, Name, RepoOwner, RepoName, WorkGroup, WorkGroupCode, NeedsNote, " +
            "CommitsInWindow, TicketsAttributed, HydratedAt, AuthoredAt, GeneratedAt " +
            $"FROM \"{NoteRecord.DefaultTableName}\"{where} " +
            "ORDER BY WorkGroupCode, Type, Name LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        List<NoteListRow> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            DateTimeOffset? authoredAt = reader.IsDBNull(11) ? null : new DateTimeOffset(reader.GetDateTime(11));
            rows.Add(new NoteListRow
            {
                NoteId = reader.GetString(0),
                Type = reader.GetString(1),
                Name = reader.GetString(2),
                RepoOwner = reader.GetString(3),
                RepoName = reader.GetString(4),
                WorkGroup = reader.GetString(5),
                WorkGroupCode = reader.GetString(6),
                NeedsNote = reader.GetString(7),
                CommitsInWindow = reader.GetInt32(8),
                TicketsAttributed = reader.GetInt32(9),
                HydratedAt = reader.IsDBNull(10) ? null : new DateTimeOffset(reader.GetDateTime(10)),
                AuthoredAt = authoredAt,
                GeneratedAt = new DateTimeOffset(reader.GetDateTime(12)),
                Status = authoredAt is not null ? "authored" : "awaiting-note",
            });
        }
        return rows;
    }

    /// <summary>Returns one note with its full hydrated evidence, or <c>null</c> if absent.</summary>
    public NoteDetail? GetNote(string noteId)
    {
        ArgumentException.ThrowIfNullOrEmpty(noteId);

        using SqliteConnection connection = OpenConnection();
        NoteRecord? note = ReadNote(connection, noteId);
        if (note is null) return null;

        return new NoteDetail
        {
            Note = note,
            SourceFiles = ReadSourceFiles(connection, noteId),
            Commits = ReadCommits(connection, noteId),
            Tickets = ReadTickets(connection, noteId),
            StructuralChanges = ReadStructuralChanges(connection, noteId),
            ExtensionRefs = ReadExtensionRefs(connection, noteId),
        };
    }

    /// <summary>Returns the most recent run row, or <c>null</c> if none exists.</summary>
    public NotesRunRecord? GetLatestRun()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT RowId, RunKey, RepoOwner, RepoName, RepoCategory, SinceSha, SinceShortSha, " +
            "HeadSha, HeadShortSha, Status, UnitsTotal, UnitsHydrated, CommitsInWindow, TicketsAttributed, " +
            "StartedAt, CompletedAt, Error, RunAt, WindowLabel " +
            $"FROM \"{NotesRunRecord.DefaultTableName}\" ORDER BY RunAt DESC, RowId DESC LIMIT 1";
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? MapRun(reader) : null;
    }

    /// <summary>Returns a single run by its key, or <c>null</c> if absent.</summary>
    public NotesRunRecord? GetRun(string runKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(runKey);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT RowId, RunKey, RepoOwner, RepoName, RepoCategory, SinceSha, SinceShortSha, " +
            "HeadSha, HeadShortSha, Status, UnitsTotal, UnitsHydrated, CommitsInWindow, TicketsAttributed, " +
            "StartedAt, CompletedAt, Error, RunAt, WindowLabel " +
            $"FROM \"{NotesRunRecord.DefaultTableName}\" WHERE RunKey = $runKey LIMIT 1";
        cmd.Parameters.AddWithValue("$runKey", runKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? MapRun(reader) : null;
    }

    private static NoteRecord? ReadNote(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT NoteId, Type, Name, RepoOwner, RepoName, RepoCategory, WorkGroup, WorkGroupCode, " +
            "SinceSha, SinceShortSha, HeadSha, HeadShortSha, CommitsInWindow, TicketsAttributed, NeedsNote, " +
            "CurrentBallotNoteHtml, ProposedBallotNoteHtml, RollupSummaryMarkdown, NotesForReviewerMarkdown, " +
            "SourceFilesNote, HydratedAt, AuthoredAt, GeneratedAt, SavedAt, WindowLabel, " +
            "CurrentNoteIsAuguryGenerated, PreservedHandAuthoredHtml " +
            $"FROM \"{NoteRecord.DefaultTableName}\" WHERE NoteId = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", noteId);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new NoteRecord
        {
            NoteId = reader.GetString(0),
            Type = reader.GetString(1),
            Name = reader.GetString(2),
            RepoOwner = reader.GetString(3),
            RepoName = reader.GetString(4),
            RepoCategory = reader.GetString(5),
            WorkGroup = reader.GetString(6),
            WorkGroupCode = reader.GetString(7),
            SinceSha = reader.GetString(8),
            SinceShortSha = reader.GetString(9),
            HeadSha = reader.GetString(10),
            HeadShortSha = reader.GetString(11),
            CommitsInWindow = reader.GetInt32(12),
            TicketsAttributed = reader.GetInt32(13),
            NeedsNote = reader.GetString(14),
            CurrentBallotNoteHtml = reader.GetString(15),
            ProposedBallotNoteHtml = reader.GetString(16),
            RollupSummaryMarkdown = reader.GetString(17),
            NotesForReviewerMarkdown = reader.GetString(18),
            SourceFilesNote = reader.GetString(19),
            HydratedAt = reader.IsDBNull(20) ? null : new DateTimeOffset(reader.GetDateTime(20)),
            AuthoredAt = reader.IsDBNull(21) ? null : new DateTimeOffset(reader.GetDateTime(21)),
            GeneratedAt = new DateTimeOffset(reader.GetDateTime(22)),
            SavedAt = new DateTimeOffset(reader.GetDateTime(23)),
            WindowLabel = reader.GetString(24),
            CurrentNoteIsAuguryGenerated = reader.GetBoolean(25),
            PreservedHandAuthoredHtml = reader.GetString(26),
        };
    }

    private static List<NoteSourceFileRecord> ReadSourceFiles(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, NoteId, Path, Role, TouchedInWindow, FileOrder " +
            $"FROM \"{NoteSourceFileRecord.DefaultTableName}\" WHERE NoteId = $id ORDER BY FileOrder, RowId";
        cmd.Parameters.AddWithValue("$id", noteId);
        List<NoteSourceFileRecord> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteSourceFileRecord
            {
                Id = reader.GetString(0),
                NoteId = reader.GetString(1),
                Path = reader.GetString(2),
                Role = reader.GetString(3),
                TouchedInWindow = reader.GetBoolean(4),
                FileOrder = reader.GetInt32(5),
            });
        }
        return rows;
    }

    private static List<NoteCommitRecord> ReadCommits(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, NoteId, Sha, ShortSha, AuthorName, AuthorDate, Subject, WebUrl, TicketKeys, CommitOrder " +
            $"FROM \"{NoteCommitRecord.DefaultTableName}\" WHERE NoteId = $id ORDER BY CommitOrder, RowId";
        cmd.Parameters.AddWithValue("$id", noteId);
        List<NoteCommitRecord> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteCommitRecord
            {
                Id = reader.GetString(0),
                NoteId = reader.GetString(1),
                Sha = reader.GetString(2),
                ShortSha = reader.GetString(3),
                AuthorName = reader.GetString(4),
                AuthorDate = reader.GetString(5),
                Subject = reader.GetString(6),
                WebUrl = reader.GetString(7),
                TicketKeys = reader.GetString(8),
                CommitOrder = reader.GetInt32(9),
            });
        }
        return rows;
    }

    private static List<NoteTicketRecord> ReadTickets(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, NoteId, TicketKey, Title, Resolution, WorkGroup, Specification, Url, CommitCount, TicketOrder, " +
            "ChangeImpact, ChangeCategory, RelatedTicketKeys, IssueType " +
            $"FROM \"{NoteTicketRecord.DefaultTableName}\" WHERE NoteId = $id ORDER BY TicketOrder, RowId";
        cmd.Parameters.AddWithValue("$id", noteId);
        List<NoteTicketRecord> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteTicketRecord
            {
                Id = reader.GetString(0),
                NoteId = reader.GetString(1),
                TicketKey = reader.GetString(2),
                Title = reader.GetString(3),
                Resolution = reader.GetString(4),
                WorkGroup = reader.GetString(5),
                Specification = reader.GetString(6),
                Url = reader.GetString(7),
                CommitCount = reader.GetInt32(8),
                TicketOrder = reader.GetInt32(9),
                ChangeImpact = reader.GetString(10),
                ChangeCategory = reader.GetString(11),
                RelatedTicketKeys = reader.GetString(12),
                IssueType = reader.GetString(13),
            });
        }
        return rows;
    }

    private static List<NoteStructuralChangeRecord> ReadStructuralChanges(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, NoteId, SourcePath, ElementPath, ChangeKind, Detail, TicketKeys, ChangeOrder " +
            $"FROM \"{NoteStructuralChangeRecord.DefaultTableName}\" WHERE NoteId = $id ORDER BY ChangeOrder, RowId";
        cmd.Parameters.AddWithValue("$id", noteId);
        List<NoteStructuralChangeRecord> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteStructuralChangeRecord
            {
                Id = reader.GetString(0),
                NoteId = reader.GetString(1),
                SourcePath = reader.GetString(2),
                ElementPath = reader.GetString(3),
                ChangeKind = reader.GetString(4),
                Detail = reader.GetString(5),
                TicketKeys = reader.GetString(6),
                ChangeOrder = reader.GetInt32(7),
            });
        }
        return rows;
    }

    private static List<NoteExtensionRefRecord> ReadExtensionRefs(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, NoteId, ExtensionUrl, ExtensionName, ReplacementCoreElement, Rationale, RefOrder " +
            $"FROM \"{NoteExtensionRefRecord.DefaultTableName}\" WHERE NoteId = $id ORDER BY RefOrder, RowId";
        cmd.Parameters.AddWithValue("$id", noteId);
        List<NoteExtensionRefRecord> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteExtensionRefRecord
            {
                Id = reader.GetString(0),
                NoteId = reader.GetString(1),
                ExtensionUrl = reader.GetString(2),
                ExtensionName = reader.GetString(3),
                ReplacementCoreElement = reader.GetString(4),
                Rationale = reader.GetString(5),
                RefOrder = reader.GetInt32(6),
            });
        }
        return rows;
    }

    private static NotesRunRecord MapRun(SqliteDataReader reader) => new()
    {
        RunKey = reader.GetString(1),
        RepoOwner = reader.GetString(2),
        RepoName = reader.GetString(3),
        RepoCategory = reader.GetString(4),
        SinceSha = reader.GetString(5),
        SinceShortSha = reader.GetString(6),
        HeadSha = reader.GetString(7),
        HeadShortSha = reader.GetString(8),
        Status = reader.GetString(9),
        UnitsTotal = reader.GetInt32(10),
        UnitsHydrated = reader.GetInt32(11),
        CommitsInWindow = reader.GetInt32(12),
        TicketsAttributed = reader.GetInt32(13),
        StartedAt = reader.IsDBNull(14) ? null : new DateTimeOffset(reader.GetDateTime(14)),
        CompletedAt = reader.IsDBNull(15) ? null : new DateTimeOffset(reader.GetDateTime(15)),
        Error = reader.GetString(16),
        RunAt = new DateTimeOffset(reader.GetDateTime(17)),
        WindowLabel = reader.GetString(18),
    };

    private static ExistingNoteProse? ReadExistingProse(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT ProposedBallotNoteHtml, RollupSummaryMarkdown, NotesForReviewerMarkdown, " +
            "SourceFilesNote, NeedsNote, AuthoredAt, GeneratedAt " +
            $"FROM \"{NoteRecord.DefaultTableName}\" WHERE NoteId = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", noteId);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new ExistingNoteProse(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetDateTime(5)),
            new DateTimeOffset(reader.GetDateTime(6)));
    }

    private static void DeleteByNoteId(SqliteConnection connection, string table, string noteId)
        => DeleteByColumn(connection, table, "NoteId", noteId);

    private static void DeleteByColumn(SqliteConnection connection, string table, string column, string value)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{table}\" WHERE \"{column}\" = $v";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private sealed record ExistingNoteProse(
        string ProposedBallotNoteHtml,
        string RollupSummaryMarkdown,
        string NotesForReviewerMarkdown,
        string SourceFilesNote,
        string NeedsNote,
        DateTimeOffset? AuthoredAt,
        DateTimeOffset GeneratedAt);
}
