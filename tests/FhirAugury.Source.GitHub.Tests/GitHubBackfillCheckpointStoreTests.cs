using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 4 (slot 0826-01): the persisted backfill cursor round-trips faithfully and
/// degrades to "start from the top" rather than throwing on corrupt input.
/// </summary>
public class GitHubBackfillCursorTests
{
    [Fact]
    public void ToJson_ThenFromJson_RoundTrips()
    {
        GitHubBackfillCursor original = new GitHubBackfillCursor
        {
            IssuesCompletedAbove = 1234,
            PrsCompletedAbove = 4200,
            IssuesPhaseComplete = true,
            PrsPhaseComplete = false,
            PendingRetry = [4199, 3050, 17],
            StalledRepairPasses = 2,
        };

        GitHubBackfillCursor? parsed = GitHubBackfillCursor.FromJson(original.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(1234, parsed!.IssuesCompletedAbove);
        Assert.Equal(4200, parsed.PrsCompletedAbove);
        Assert.True(parsed.IssuesPhaseComplete);
        Assert.False(parsed.PrsPhaseComplete);
        Assert.Equal([4199, 3050, 17], parsed.PendingRetry);
        Assert.Equal(2, parsed.StalledRepairPasses);
    }

    [Fact]
    public void ToJson_ThenFromJson_RoundTripsNullWatermarks()
    {
        GitHubBackfillCursor original = new GitHubBackfillCursor();

        GitHubBackfillCursor? parsed = GitHubBackfillCursor.FromJson(original.ToJson());

        Assert.NotNull(parsed);
        Assert.Null(parsed!.IssuesCompletedAbove);
        Assert.Null(parsed.PrsCompletedAbove);
        Assert.Empty(parsed.PendingRetry);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    public void FromJson_MalformedOrEmpty_ReturnsNull(string? json)
    {
        Assert.Null(GitHubBackfillCursor.FromJson(json));
    }

    [Theory]
    // issuesComplete, prsComplete, pendingCount, expected
    [InlineData(true, true, 0, true)]
    [InlineData(true, true, 1, false)]
    [InlineData(true, false, 0, false)]
    [InlineData(false, true, 0, false)]
    [InlineData(false, false, 0, false)]
    [InlineData(false, false, 3, false)]
    public void IsComplete_TruthTable(bool issuesComplete, bool prsComplete, int pendingCount, bool expected)
    {
        GitHubBackfillCursor cursor = new GitHubBackfillCursor
        {
            IssuesPhaseComplete = issuesComplete,
            PrsPhaseComplete = prsComplete,
            PendingRetry = Enumerable.Range(1, pendingCount).ToArray(),
        };

        Assert.Equal(expected, cursor.IsComplete);
    }
}

/// <summary>
/// Phase 4 (slot 0826-01): the two-prefix state machine. Partial progress must never appear
/// under the terminal <c>backfill:</c> prefix — an older binary treats any such row as
/// complete, so writing partial state there would silently skip the backfill after a rollback.
/// </summary>
public class GitHubBackfillCheckpointStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly GitHubBackfillCheckpointStore _store;

    public GitHubBackfillCheckpointStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"backfill_ckpt_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _store = new GitHubBackfillCheckpointStore(_db, NullLogger<GitHubBackfillCheckpointStore>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    private List<GitHubSyncStateRecord> AllRows()
    {
        using SqliteConnection connection = _db.OpenConnection();
        return GitHubSyncStateRecord.SelectList(connection, SourceName: IGitHubDataProvider.SourceName);
    }

    [Fact]
    public void WriteCheckpoint_ThenReadCursor_RoundTrips()
    {
        GitHubBackfillCursor cursor = new GitHubBackfillCursor
        {
            PrsCompletedAbove = 4200,
            IssuesPhaseComplete = true,
            PendingRetry = [4199],
        };

        _store.WriteCheckpoint("HL7/fhir", cursor, itemsIngested: 812, lastError: "boom");

        GitHubBackfillCursor? read = _store.ReadCursor("HL7/fhir");

        Assert.NotNull(read);
        Assert.Equal(4200, read!.PrsCompletedAbove);
        Assert.True(read.IssuesPhaseComplete);
        Assert.Equal([4199], read.PendingRetry);
    }

    [Fact]
    public void WriteCheckpoint_DoesNotCreateTerminalMarker()
    {
        _store.WriteCheckpoint("HL7/fhir", new GitHubBackfillCursor { PrsCompletedAbove = 100 }, 10, null);

        List<GitHubSyncStateRecord> rows = AllRows();

        Assert.Contains(rows, r => r.SubSource == GitHubBackfillCheckpointStore.ProgressPrefix + "HL7/fhir");
        Assert.DoesNotContain(rows, r => r.SubSource == GitHubBackfillCheckpointStore.MarkerPrefix + "HL7/fhir");
    }

    [Fact]
    public void WriteCheckpoint_LeavesRepoOutOfCompletedSet()
    {
        _store.WriteCheckpoint("HL7/fhir", new GitHubBackfillCursor { PrsCompletedAbove = 100 }, 10, null);

        Assert.DoesNotContain("HL7/fhir", _store.GetCompletedRepos());
    }

    [Fact]
    public void WriteCheckpoint_WhenPhasesDoneButRetriesPending_UsesRepairRequiredStatus()
    {
        _store.WriteCheckpoint(
            "HL7/fhir",
            new GitHubBackfillCursor
            {
                IssuesPhaseComplete = true,
                PrsPhaseComplete = true,
                PendingRetry = [42],
            },
            10, null);

        GitHubSyncStateRecord row = AllRows()
            .Single(r => r.SubSource == GitHubBackfillCheckpointStore.ProgressPrefix + "HL7/fhir");

        Assert.Equal(GitHubBackfillCheckpointStore.StatusRepairRequired, row.Status);
    }

    [Fact]
    public void MarkComplete_WritesSuccessMarker_AndDeletesProgressRow()
    {
        _store.WriteCheckpoint("HL7/fhir", new GitHubBackfillCursor { PrsCompletedAbove = 100 }, 10, null);
        _store.MarkComplete("HL7/fhir", itemsIngested: 4285);

        List<GitHubSyncStateRecord> rows = AllRows();

        GitHubSyncStateRecord marker = Assert.Single(
            rows, r => r.SubSource == GitHubBackfillCheckpointStore.MarkerPrefix + "HL7/fhir");
        Assert.Equal(GitHubBackfillCheckpointStore.StatusSuccess, marker.Status);
        Assert.Equal(4285, marker.ItemsIngested);

        Assert.DoesNotContain(rows, r => r.SubSource == GitHubBackfillCheckpointStore.ProgressPrefix + "HL7/fhir");
        Assert.Null(_store.ReadCursor("HL7/fhir"));
        Assert.Contains("HL7/fhir", _store.GetCompletedRepos());
    }

    [Fact]
    public void GetCompletedRepos_IgnoresPartialAndRepairRequired()
    {
        _store.WriteCheckpoint("HL7/fhir", new GitHubBackfillCursor { PrsCompletedAbove = 100 }, 10, null);
        _store.WriteCheckpoint(
            "HL7/us-core",
            new GitHubBackfillCursor { IssuesPhaseComplete = true, PrsPhaseComplete = true, PendingRetry = [1] },
            10, null);
        _store.MarkComplete("HL7/fhir-extensions", itemsIngested: 5);

        HashSet<string> completed = _store.GetCompletedRepos();

        Assert.Equal(["HL7/fhir-extensions"], completed);
    }

    [Fact]
    public void ClearProgress_RemovesBothRows()
    {
        _store.WriteCheckpoint("HL7/fhir", new GitHubBackfillCursor { PrsCompletedAbove = 100 }, 10, null);
        _store.MarkComplete("HL7/fhir", itemsIngested: 4285);

        _store.ClearProgress("HL7/fhir");

        Assert.Empty(AllRows());
        Assert.Empty(_store.GetCompletedRepos());
        Assert.Null(_store.ReadCursor("HL7/fhir"));
    }

    [Fact]
    public void ReadCursor_WhenRowHasCorruptCursor_ReturnsNull()
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            GitHubSyncStateRecord.Insert(connection, new GitHubSyncStateRecord
            {
                Id = GitHubSyncStateRecord.GetIndex(),
                SourceName = IGitHubDataProvider.SourceName,
                SubSource = GitHubBackfillCheckpointStore.ProgressPrefix + "HL7/fhir",
                LastSyncAt = DateTimeOffset.UtcNow,
                LastCursor = "{not json",
                ItemsIngested = 0,
                SyncSchedule = null,
                NextScheduledAt = null,
                Status = GitHubBackfillCheckpointStore.StatusPartial,
                LastError = null,
            });
        }

        Assert.Null(_store.ReadCursor("HL7/fhir"));
    }
}
