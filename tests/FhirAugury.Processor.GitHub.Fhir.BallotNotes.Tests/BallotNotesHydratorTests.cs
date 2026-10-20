using System.Diagnostics;
using System.Net;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNotesHydratorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"ballotnotes-hydrator-{Guid.NewGuid():N}");
    private readonly string _cloneRoot;
    private readonly BallotNotesDatabase _database;

    public BallotNotesHydratorTests()
    {
        Directory.CreateDirectory(_directory);
        _cloneRoot = Path.Combine(_directory, "repos");
        _database = new BallotNotesDatabase(
            Path.Combine(_directory, "notes.db"),
            NullLogger<BallotNotesDatabase>.Instance);
        _database.Initialize();
    }

    [Fact]
    public async Task HydrationUsesFrozenHeadAfterMainCloneAdvances()
    {
        string since = await GitFixture.CreateAsync(
            _cloneRoot,
            "testowner",
            "testrepo");
        string clone = Path.Combine(
            _cloneRoot,
            "testowner_testrepo",
            "clone");
        string frozenHead = (await GitRunner.RunAsync(
            clone,
            ["rev-parse", "HEAD"])).Trim();
        string executionId = Guid.NewGuid().ToString("N");
        HydrationMutationLease lease =
            _database.TryAcquireHydrationLease(executionId)!;
        _database.BeginHydrationExecution(
            NewExecution(executionId, since, frozenHead),
            lease);

        Directory.CreateDirectory(Path.Combine(clone, "source"));
        await File.WriteAllTextAsync(
            Path.Combine(clone, "source", "new-page.html"),
            "<html>new page</html>");
        await Git(clone, "add", "-A");
        await Git(clone, "commit", "-q", "-m", "FHIR-3 later page");

        BallotNotesHydrationOptions options = new()
        {
            CloneRoot = _cloneRoot,
            GitHubDbPath = "",
            FhirR6DbPath = "",
            FhirSpecDbPath = "",
            OrchestratorAddress = "http://localhost",
            JiraSourceAddress = "http://localhost",
            MaxParallelism = 1,
        };
        HttpClient client = new(new NotFoundHandler())
        {
            BaseAddress = new Uri("http://localhost"),
        };
        TicketAttributor attributor = new(
            client,
            Options.Create(options),
            NullLogger<TicketAttributor>.Instance);
        BallotNotesHydrator hydrator = new(
            _database,
            attributor,
            Options.Create(options),
            NullLogger<BallotNotesHydrator>.Instance);

        HydrationResult result = await hydrator.HydrateAsync(
            new BallotNotesHydrationRequest
            {
                ExecutionId = executionId,
                RepoOwner = "testowner",
                RepoName = "testrepo",
                SinceSha = since,
                HeadSha = frozenHead,
                RunKey = $"testowner/testrepo@{since}..{frozenHead}",
                MutationLease = lease,
            });

        Assert.Equal("completed", result.Status);
        Assert.Equal(
            frozenHead,
            _database.GetHydrationExecution(executionId)!.HeadSha);
        Assert.All(
            _database.GetHydrationExecutionItems(executionId),
            item => Assert.DoesNotContain("new-page", item.NoteId));
        Assert.All(
            _database.ListNotes(new NoteQueryFilter
            {
                Repo = "testowner/testrepo",
            }),
            item => Assert.DoesNotContain("new-page", item.NoteId));
    }

    [Fact]
    public void RehydrationPreservesProseButMarksItStale()
    {
        string first = CreateExecutionWithEvidence("note-a", "head-1");
        _database.UpdateNoteProse(
            "note-a",
            new BallotNoteProse
            {
                NeedsNote = "yes",
                ProposedBallotNoteHtml = "<blockquote>old prose</blockquote>",
            },
            DateTimeOffset.UtcNow);
        string second = CreateExecutionWithEvidence("note-a", "head-2");

        NoteDetail detail = _database.GetNote("note-a")!;
        Assert.NotEqual(first, second);
        Assert.Equal(second, detail.Note.CurrentHydrationExecutionId);
        Assert.Equal("<blockquote>old prose</blockquote>",
            detail.Note.ProposedBallotNoteHtml);
        Assert.Equal("stale", detail.Status);
        Assert.Equal("stale", detail.Note.ProseVerificationStatus);
    }

    [Fact]
    public void ProseWritesDoNotChangeEvidenceIdentity()
    {
        CreateExecutionWithEvidence("note-a", "head-1");
        NoteRecord before = _database.GetNote("note-a")!.Note;

        _database.UpdateNoteProse(
            "note-a",
            new BallotNoteProse
            {
                NeedsNote = "yes",
                ProposedBallotNoteHtml = "<blockquote>draft</blockquote>",
                RollupSummaryMarkdown = "rollup",
                NotesForReviewerMarkdown = "review",
                SourceFilesNote = "maintenance-only prose",
            },
            DateTimeOffset.UtcNow);

        NoteRecord after = _database.GetNote("note-a")!.Note;
        Assert.Equal(before.CurrentEvidenceHash, after.CurrentEvidenceHash);
        Assert.Equal(
            before.CurrentEvidenceRevision,
            after.CurrentEvidenceRevision);
    }

    private string CreateExecutionWithEvidence(string noteId, string head)
    {
        string id = Guid.NewGuid().ToString("N");
        HydrationMutationLease lease =
            _database.TryAcquireHydrationLease(id)!;
        _database.BeginHydrationExecution(
            NewExecution(id, "since", head),
            lease);
        _database.SetHydrationMembership(
            id,
            lease,
            [new HydrationMembershipDefinition(noteId, "Artifact")]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _database.UpsertUnitEvidence(
            id,
            lease,
            new NoteRecord
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
            },
            [],
            [],
            []);
        _database.BumpHydrationProgress(id, lease, 1, 0, 0);
        _database.FinishHydrationExecution(
            id,
            lease,
            "completed",
            null);
        _database.ReleaseMutationLease(lease);
        return id;
    }

    private static NotesHydrationExecutionRecord NewExecution(
        string id,
        string since,
        string head)
        => new()
        {
            Id = id,
            RunKey = $"HL7/fhir@{since}..{head}",
            RepoOwner = "HL7",
            RepoName = "fhir",
            SinceSha = since,
            SinceShortSha = since,
            HeadSha = head,
            HeadShortSha = head,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow,
        };

    private static async Task Git(string workingDirectory, params string[] args)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(startInfo)!;
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(error);
        }
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        TestFileCleanup.SafeDeleteDirectory(_directory);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
