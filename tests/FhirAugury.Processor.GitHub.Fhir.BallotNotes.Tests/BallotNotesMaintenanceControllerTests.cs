using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Controllers;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNotesMaintenanceControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"ballotnotes-maintenance-{Guid.NewGuid():N}");
    private readonly BallotNotesDatabase _database;
    private readonly AuthoringRunStore _store;
    private readonly BallotNotesAuthoringRunCoordinator _coordinator;
    private readonly NotesHydrationExecutionRecord _execution;

    public BallotNotesMaintenanceControllerTests()
    {
        Directory.CreateDirectory(_directory);
        _database = new BallotNotesDatabase(
            Path.Combine(_directory, "notes.db"),
            NullLogger<BallotNotesDatabase>.Instance);
        _database.Initialize();
        _store = new AuthoringRunStore(_database.OpenConnection);
        _coordinator = new BallotNotesAuthoringRunCoordinator(
            _database,
            _store);
        _execution = SeedExecution();
    }

    [Fact]
    public async Task BatchIsAllOrNoneWhenAnyRevisionIsStale()
    {
        string firstRevision = Revision("note-a");
        BallotNotesWorkGroupReallocationRequest request = new(
            [
                Change("note-a", firstRevision, "New A", "new-a"),
                Change("note-b", "stale", "New B", "new-b"),
            ]);

        await Assert.ThrowsAsync<AuthoringConflictException>(
            () => _database.ReallocateWorkGroupsAsync(request));

        Assert.Equal("old", _database.GetNote("note-a")!.Note.WorkGroupCode);
        Assert.Equal("old", _database.GetNote("note-b")!.Note.WorkGroupCode);
    }

    [Fact]
    public async Task BatchUpdatesEveryRowWithExpectedEvidence()
    {
        BallotNotesMaintenanceController controller = new(_database);
        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.ReallocateWorkGroups(
                new BallotNotesWorkGroupReallocationRequest(
                    [
                        Change("note-a", Revision("note-a"), "New A", "new-a"),
                        Change("note-b", Revision("note-b"), "New B", "new-b"),
                    ]),
                CancellationToken.None));

        BallotNotesWorkGroupReallocationResult body =
            Assert.IsType<BallotNotesWorkGroupReallocationResult>(
                result.Value);
        Assert.Equal(2, body.UpdatedCount);
        Assert.Equal("new-a", _database.GetNote("note-a")!.Note.WorkGroupCode);
        Assert.Equal("new-b", _database.GetNote("note-b")!.Note.WorkGroupCode);
    }

    [Fact]
    public async Task ActiveAuthoringFenceRejectsMaintenanceBatch()
    {
        await ActivateAsync();
        BallotNotesAuthoringRunCreation creation =
            await _coordinator.CreateRunAsync(
            new BallotNotesAuthoringRunRequest(
                _execution.Id,
                DatabaseOnly: true));
        Assert.True(await _store.TryAcquireMutationFenceAsync(
            _coordinator.ProcessorKind,
            creation.Run.Id));
        BallotNotesMaintenanceController controller = new(_database);

        ConflictObjectResult result = Assert.IsType<ConflictObjectResult>(
            await controller.ReallocateWorkGroups(
                new BallotNotesWorkGroupReallocationRequest(
                    [
                        Change(
                            "note-a",
                            Revision("note-a"),
                            "New A",
                            "new-a"),
                    ]),
                CancellationToken.None));

        Assert.Contains(
            nameof(AuthoringConflictCode.MutationFenceUnavailable),
            result.Value!.ToString());
        Assert.Equal("old", _database.GetNote("note-a")!.Note.WorkGroupCode);
    }

    private NotesHydrationExecutionRecord SeedExecution()
    {
        string id = Guid.NewGuid().ToString("N");
        HydrationMutationLease lease =
            _database.TryAcquireHydrationLease(id)!;
        NotesHydrationExecutionRecord execution = new()
        {
            Id = id,
            RunKey = $"HL7/fhir@since..head-{id}",
            RepoOwner = "HL7",
            RepoName = "fhir",
            SinceSha = "since",
            SinceShortSha = "since",
            HeadSha = "head",
            HeadShortSha = "head",
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow,
        };
        _database.BeginHydrationExecution(execution, lease);
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
            Evidence("note-a"),
            [],
            [],
            []);
        _database.UpsertUnitEvidence(
            id,
            lease,
            Evidence("note-b"),
            [],
            [],
            []);
        _database.BumpHydrationProgress(id, lease, 2, 0, 0);
        _database.FinishHydrationExecution(
            id,
            lease,
            "completed",
            null);
        _database.ReleaseMutationLease(lease);
        return _database.GetHydrationExecution(id)!;
    }

    private string Revision(string noteId)
        => _database.GetNote(noteId)!.Note.CurrentEvidenceRevision;

    private static BallotNoteWorkGroupReallocation Change(
        string noteId,
        string revision,
        string workGroup,
        string code)
        => new(
            noteId,
            revision,
            workGroup,
            code,
            workGroup,
            code);

    private static NoteRecord Evidence(string noteId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new NoteRecord
        {
            NoteId = noteId,
            Type = "Artifact",
            Name = noteId,
            RepoOwner = "HL7",
            RepoName = "fhir",
            WorkGroup = "Old",
            WorkGroupCode = "old",
            WorkGroupNames = "Old",
            WorkGroupCodes = "old",
            SinceSha = "since",
            SinceShortSha = "since",
            HeadSha = "head",
            HeadShortSha = "head",
            GeneratedAt = now,
            SavedAt = now,
        };
    }

    private async Task ActivateAsync()
    {
        await _store.EnsureProcessorModeAsync(_coordinator.ProcessorKind);
        await _store.TransitionProcessorModeAsync(
            _coordinator.ProcessorKind,
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        await _store.TransitionProcessorModeAsync(
            _coordinator.ProcessorKind,
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        TestFileCleanup.SafeDeleteDirectory(_directory);
    }
}
