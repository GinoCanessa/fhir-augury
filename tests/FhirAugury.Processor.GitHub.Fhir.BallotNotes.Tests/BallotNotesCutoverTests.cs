using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNotesCutoverTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"ballot-notes-cutover-{Guid.NewGuid():N}");

    [Fact]
    public async Task CutoverBuildsOneBaselineWithExactCurrentRevisions()
    {
        Directory.CreateDirectory(_directory);
        string databasePath = Path.Combine(_directory, "notes.db");
        using BallotNotesDatabase database = new(
            databasePath,
            NullLogger<BallotNotesDatabase>.Instance);
        database.Initialize();
        Seed(database, "note-a", "revision-a", "execution-a");
        Seed(database, "note-b", "revision-b", "execution-b");

        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(database.OpenConnection)
                .ActivateAsync(
                    new AuthoringCutoverRequest(
                        BallotNotesDatabase.AuthoringProcessorKind,
                        databasePath,
                        Path.Combine(_directory, "notes.backup.db")),
                    database);

        Assert.True(active.RevalidationRequired);
        Assert.Equal(
            1,
            Scalar<int>(
                database,
                "SELECT COUNT(*) FROM notes_hydration_executions WHERE IsCutoverBaseline = 1"));
        Assert.Equal(
            1,
            Scalar<int>(
                database,
                "SELECT COUNT(DISTINCT CurrentHydrationExecutionId) FROM notes"));
        AuthoringRunItemRecord[] items =
            [.. await new AuthoringRunStore(database.OpenConnection)
                .GetRunItemsAsync(active.RevalidationRunId!)];
        Assert.Equal(2, items.Length);
        Assert.Contains(items, item =>
            item.BusinessKey == "note-a" &&
            item.ExpectedSourceRevision == "revision-a");
        Assert.Contains(items, item =>
            item.BusinessKey == "note-b" &&
            item.ExpectedSourceRevision == "revision-b");
        Assert.Throws<AuthoringConflictException>(() =>
            database.UpdateNoteProse(
                "note-a",
                new BallotNoteProse { NeedsNote = "yes" },
                DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static void Seed(
        BallotNotesDatabase database,
        string noteId,
        string revision,
        string executionId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NoteRecord note = new()
        {
            NoteId = noteId,
            Type = "Artifact",
            Name = noteId,
            RepoOwner = "HL7",
            RepoName = "fhir",
            RepoCategory = "FhirCore",
            WorkGroup = "FHIR Infrastructure",
            WorkGroupCode = "fhir",
            SinceSha = "old",
            SinceShortSha = "old",
            HeadSha = "new",
            HeadShortSha = "new",
            CurrentEvidenceHash = $"hash-{noteId}",
            CurrentEvidenceRevision = revision,
            CurrentHydrationExecutionId = executionId,
            GeneratedAt = now,
            SavedAt = now,
        };
        database.UpsertUnitEvidence(note, [], [], []);
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notes
            SET CurrentEvidenceHash = @hash,
                CurrentEvidenceRevision = @revision,
                CurrentHydrationExecutionId = @executionId
            WHERE NoteId = @noteId
            """;
        command.Parameters.AddWithValue("@hash", $"hash-{noteId}");
        command.Parameters.AddWithValue("@revision", revision);
        command.Parameters.AddWithValue("@executionId", executionId);
        command.Parameters.AddWithValue("@noteId", noteId);
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(BallotNotesDatabase database, string sql)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }
}
