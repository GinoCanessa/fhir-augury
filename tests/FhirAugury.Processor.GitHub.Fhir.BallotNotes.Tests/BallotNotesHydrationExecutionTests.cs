using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNotesHydrationExecutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"ballotnotes-executions-{Guid.NewGuid():N}");
    private readonly BallotNotesDatabase _database;

    public BallotNotesHydrationExecutionTests()
    {
        Directory.CreateDirectory(_directory);
        _database = new BallotNotesDatabase(
            Path.Combine(_directory, "notes.db"),
            NullLogger<BallotNotesDatabase>.Instance);
        _database.Initialize();
    }

    [Fact]
    public void RepeatedLogicalWindowsRetainDistinctExecutionHistory()
    {
        string runKey = "HL7/fhir@since..head";
        NotesHydrationExecutionRecord first =
            CreateExecution(runKey, ["note-a", "note-b"]);
        NotesHydrationExecutionRecord second =
            CreateExecution(runKey, ["note-b", "note-c"]);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(
            second.Id,
            _database.GetRun(runKey)!.LatestExecutionId);
        using SqliteConnection connection = _database.OpenConnection();
        Assert.Equal(2, Scalar<int>(
            connection,
            "SELECT COUNT(*) FROM notes_hydration_executions WHERE RunKey = 'HL7/fhir@since..head'"));
        Assert.Equal(
            ["note-a", "note-b"],
            _database.GetHydrationExecutionItems(first.Id)
                .Select(item => item.NoteId).ToArray());
        Assert.Equal(
            ["note-b", "note-c"],
            _database.GetHydrationExecutionItems(second.Id)
                .Select(item => item.NoteId).ToArray());
        Assert.NotEqual(
            _database.GetHydrationExecutionItems(first.Id)
                .Single(item => item.NoteId == "note-b").EvidenceRevision,
            _database.GetHydrationExecutionItems(second.Id)
                .Single(item => item.NoteId == "note-b").EvidenceRevision);
    }

    [Fact]
    public void MembershipIsCompleteBeforeAnyItemIsHydrated()
    {
        string id = Guid.NewGuid().ToString("N");
        HydrationMutationLease lease =
            _database.TryAcquireHydrationLease(id)!;
        _database.BeginHydrationExecution(NewExecution(id, "window"), lease);
        _database.SetHydrationMembership(
            id,
            lease,
            [
                new HydrationMembershipDefinition("note-a", "Artifact"),
                new HydrationMembershipDefinition("note-b", "Page"),
            ]);

        IReadOnlyList<NotesHydrationRunItemRecord> items =
            _database.GetHydrationExecutionItems(id);
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal("pending", item.Status));
        Assert.Equal(2, _database.GetHydrationExecution(id)!.UnitsTotal);

        _database.FinishHydrationExecution(id, lease, "failed", "stop");
        _database.ReleaseMutationLease(lease);
        Assert.All(
            _database.GetHydrationExecutionItems(id),
            item => Assert.Equal("failed", item.Status));
    }

    [Fact]
    public void FailedExecutionDoesNotPartiallyReplaceLiveCorpus()
    {
        CreateExecution("initial", ["note-a", "note-b"]);
        string firstRevision =
            _database.GetNote("note-a")!.Note.CurrentEvidenceRevision;
        string secondRevision =
            _database.GetNote("note-b")!.Note.CurrentEvidenceRevision;

        string id = Guid.NewGuid().ToString("N");
        HydrationMutationLease lease =
            _database.TryAcquireHydrationLease(id)!;
        _database.BeginHydrationExecution(
            NewExecution(id, "replacement"),
            lease);
        _database.SetHydrationMembership(
            id,
            lease,
            [
                new HydrationMembershipDefinition("note-a", "Artifact"),
                new HydrationMembershipDefinition("note-b", "Artifact"),
            ]);
        _database.UpsertUnitEvidence(
            id,
            lease,
            Evidence("note-a", "replacement-head"),
            [],
            [],
            []);
        _database.BumpHydrationProgress(id, lease, 1, 0, 0);
        _database.MarkHydrationItemFailed(
            id,
            lease,
            "note-b",
            "simulated failure");
        _database.FinishHydrationExecution(
            id,
            lease,
            "failed",
            "simulated failure");
        _database.ReleaseMutationLease(lease);

        Assert.Equal(
            firstRevision,
            _database.GetNote("note-a")!.Note.CurrentEvidenceRevision);
        Assert.Equal(
            secondRevision,
            _database.GetNote("note-b")!.Note.CurrentEvidenceRevision);
        Assert.Equal(
            "head",
            _database.GetNote("note-a")!.Note.HeadSha);
        Assert.Equal("failed", _database.GetHydrationExecution(id)!.Status);
    }

    [Fact]
    public async Task StartupRecoveryFencesStaleWriterWithoutLiveLeaseTheft()
    {
        string path = Path.Combine(_directory, "restart.db");
        BallotNotesDatabase first = new(
            path,
            NullLogger<BallotNotesDatabase>.Instance,
            mutationOwnerGeneration: "generation-a");
        first.AcquireStartupOwnership();
        first.Initialize();
        string id = Guid.NewGuid().ToString("N");
        HydrationMutationLease staleLease =
            first.TryAcquireHydrationLease(id)!;
        first.BeginHydrationExecution(
            NewExecution(id, "interrupted"),
            staleLease);
        first.SetHydrationMembership(
            id,
            staleLease,
            [new HydrationMembershipDefinition("note-a", "Artifact")]);

        using BallotNotesDatabase staleWriter = new(
            path,
            NullLogger<BallotNotesDatabase>.Instance,
            mutationOwnerGeneration: "generation-a");
        staleWriter.Initialize();
        using BallotNotesDatabase restarted = new(
            path,
            NullLogger<BallotNotesDatabase>.Instance,
            mutationOwnerGeneration: "generation-b");
        restarted.Initialize();
        Assert.Null(restarted.TryAcquireHydrationLease("before-recovery"));
        Assert.Throws<InvalidOperationException>(
            restarted.AcquireStartupOwnership);

        first.Dispose();
        restarted.AcquireStartupOwnership();
        Assert.True(
            await restarted.RecoverInterruptedHydrationAsync() > 0);
        Assert.Equal(
            "failed",
            restarted.GetHydrationExecution(id)!.Status);
        Assert.Throws<AuthoringConflictException>(
            () => staleWriter.BumpHydrationProgress(
                id,
                staleLease,
                1,
                0,
                0));

        HydrationMutationLease recovered =
            restarted.TryAcquireHydrationLease("after-recovery")!;
        Assert.NotNull(recovered);
        restarted.ReleaseMutationLease(recovered);
    }

    private NotesHydrationExecutionRecord CreateExecution(
        string runKey,
        IReadOnlyList<string> noteIds)
    {
        string id = Guid.NewGuid().ToString("N");
        HydrationMutationLease lease =
            _database.TryAcquireHydrationLease(id)!;
        _database.BeginHydrationExecution(
            NewExecution(id, runKey),
            lease);
        _database.SetHydrationMembership(
            id,
            lease,
            noteIds.Select(noteId =>
                new HydrationMembershipDefinition(noteId, "Artifact"))
                .ToArray());
        foreach (string noteId in noteIds)
        {
            _database.UpsertUnitEvidence(
                id,
                lease,
                Evidence(noteId),
                [],
                [],
                []);
        }
        _database.BumpHydrationProgress(
            id,
            lease,
            noteIds.Count,
            0,
            0);
        _database.FinishHydrationExecution(
            id,
            lease,
            "completed",
            null);
        _database.ReleaseMutationLease(lease);
        return _database.GetHydrationExecution(id)!;
    }

    private static NotesHydrationExecutionRecord NewExecution(
        string id,
        string runKey)
        => new()
        {
            Id = id,
            RunKey = runKey,
            RepoOwner = "HL7",
            RepoName = "fhir",
            SinceSha = "since",
            SinceShortSha = "since",
            HeadSha = "head",
            HeadShortSha = "head",
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow,
        };

    private static NoteRecord Evidence(
        string noteId,
        string head = "head")
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new NoteRecord
        {
            NoteId = noteId,
            Type = "Artifact",
            Name = noteId,
            RepoOwner = "HL7",
            RepoName = "fhir",
            SinceSha = "since",
            SinceShortSha = "since",
            HeadSha = head,
            HeadShortSha = head,
            GeneratedAt = now,
            SavedAt = now,
        };
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        TestFileCleanup.SafeDeleteDirectory(_directory);
    }
}
