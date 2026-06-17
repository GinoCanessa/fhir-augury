using FhirAugury.Common.Database;
using FhirAugury.Tools.NotesSite.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Tools.NotesSite.Database;

/// <summary>
/// The notes (output) SQLite database. Greenfield, augury-convention
/// cslightdbgen schema. <see cref="SourceDatabase.Initialize"/> creates each
/// table. Call <see cref="DropTables"/> first for a clean re-run.
/// </summary>
public sealed class NotesDatabase : SourceDatabase
{
    private static readonly string[] s_tableNames =
    [
        NoteSourceFileRecord.DefaultTableName,
        NoteCommitRecord.DefaultTableName,
        NoteTicketRecord.DefaultTableName,
        NoteRecord.DefaultTableName,
        NotesRunRecord.DefaultTableName,
    ];

    public NotesDatabase(string dbPath, ILogger logger, bool readOnly = false)
        : base(dbPath, logger, readOnly)
    {
    }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        NoteRecord.CreateTable(connection);
        NoteSourceFileRecord.CreateTable(connection);
        NoteCommitRecord.CreateTable(connection);
        NoteTicketRecord.CreateTable(connection);
        NotesRunRecord.CreateTable(connection);
    }

    /// <summary>Drops every notes table (children first) for a clean re-run.</summary>
    public void DropTables()
    {
        using SqliteConnection connection = OpenConnection();
        foreach (string table in s_tableNames)
        {
            ExecuteNonQuery(connection, $"DROP TABLE IF EXISTS \"{table}\"");
        }
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
    /// Idempotently upserts one note plus its children and the owning run row.
    /// Deletes any prior rows for the same <see cref="NoteRecord.NoteId"/> first,
    /// so a re-draft of the same unit replaces the previous one cleanly. The
    /// generated <c>Insert</c> extension self-manages an ADO transaction per
    /// call, so this method does not open one of its own; the leading deletes run
    /// in autocommit mode and a re-run repeats them, keeping the operation
    /// effectively idempotent.
    /// </summary>
    public void SaveNote(
        NoteRecord note,
        IReadOnlyList<NoteSourceFileRecord> files,
        IReadOnlyList<NoteCommitRecord> commits,
        IReadOnlyList<NoteTicketRecord> tickets,
        NotesRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(note);

        using SqliteConnection connection = OpenConnection();

        DeleteByNoteId(connection, NoteSourceFileRecord.DefaultTableName, note.NoteId);
        DeleteByNoteId(connection, NoteCommitRecord.DefaultTableName, note.NoteId);
        DeleteByNoteId(connection, NoteTicketRecord.DefaultTableName, note.NoteId);
        DeleteByNoteId(connection, NoteRecord.DefaultTableName, note.NoteId);
        DeleteByColumn(connection, NotesRunRecord.DefaultTableName, nameof(NotesRunRecord.RunKey), run.RunKey);

        connection.Insert(note);
        foreach (NoteSourceFileRecord file in files) connection.Insert(file);
        foreach (NoteCommitRecord commit in commits) connection.Insert(commit);
        foreach (NoteTicketRecord ticket in tickets) connection.Insert(ticket);
        connection.Insert(run);
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

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
