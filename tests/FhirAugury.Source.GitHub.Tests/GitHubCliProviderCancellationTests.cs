using System.Text;
using System.Text.Json;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// A <see cref="GhCliRunner"/> stand-in that records every argument string and serves
/// synthetic <c>issue list</c> / <c>pr list</c> / detail payloads, so provider behaviour
/// can be exercised without invoking the real <c>gh</c> executable.
/// </summary>
internal sealed class FakeGhCliRunner : GhCliRunner
{
    private readonly List<string> _invocations = [];

    public FakeGhCliRunner()
        : base(
            Options.Create(new GitHubServiceOptions { GhCli = new GhCliConfiguration() }),
            NullLogger<GhCliRunner>.Instance)
    {
    }

    /// <summary>Every argument string passed to the runner, in call order.</summary>
    public IReadOnlyList<string> Invocations => _invocations;

    /// <summary>Issue numbers the fake <c>issue list</c> should return.</summary>
    public int[] IssueNumbers { get; set; } = [];

    /// <summary>PR numbers the fake <c>pr list</c> should return.</summary>
    public int[] PrNumbers { get; set; } = [];

    /// <summary>Token whose cancellation is triggered before the Nth detail call returns.</summary>
    public CancellationTokenSource? CancelOnDetailCall { get; set; }

    /// <summary>1-based detail-call ordinal at which <see cref="CancelOnDetailCall"/> fires.</summary>
    public int CancelAtDetailCall { get; set; }

    /// <summary>1-based detail-call ordinal that throws an ordinary (non-cancellation) error.</summary>
    public int ThrowAtDetailCall { get; set; }

    /// <summary>Item numbers whose detail fetch throws an ordinary (non-cancellation) error.</summary>
    public HashSet<int> FailDetailForNumbers { get; set; } = [];

    /// <summary>Item numbers whose detail fetch was requested, in call order.</summary>
    public List<int> DetailNumbersInOrder { get; } = [];

    /// <summary>Detail-fetch invocations observed so far (repo metadata excluded).</summary>
    public int DetailCallCount { get; private set; }

    /// <summary>Counts invocations whose argument string starts with the given prefix.</summary>
    public int CountInvocations(string prefix) =>
        _invocations.Count(a => a.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Extracts the item number from a <c>{issue|pr} view {number} ...</c> argument string.</summary>
    private static int ParseItemNumber(string arguments)
    {
        string[] parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && int.TryParse(parts[2], out int number) ? number : 0;
    }

    public override Task<JsonDocument> RunAsync(string arguments, CancellationToken ct)
    {
        _invocations.Add(arguments);

        if (arguments.StartsWith("repo view", StringComparison.Ordinal))
            return Task.FromResult(JsonDocument.Parse(RepoMetadataJson()));

        // Everything else on this path is a per-item detail fetch.
        DetailCallCount++;

        int number = ParseItemNumber(arguments);
        DetailNumbersInOrder.Add(number);

        if (ThrowAtDetailCall > 0 && DetailCallCount == ThrowAtDetailCall)
            throw new InvalidOperationException("gh command failed (exit 1): synthetic failure");

        if (FailDetailForNumbers.Contains(number))
            throw new InvalidOperationException("gh command failed (exit 1): synthetic failure");

        if (CancelOnDetailCall is not null && CancelAtDetailCall > 0 && DetailCallCount >= CancelAtDetailCall)
        {
            CancelOnDetailCall.Cancel();
            throw new OperationCanceledException(CancelOnDetailCall.Token);
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult(JsonDocument.Parse(DetailJson()));
    }

    public override async IAsyncEnumerable<JsonElement> StreamArrayAsync(
        string arguments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _invocations.Add(arguments);

        int[] numbers =
            arguments.StartsWith("issue list", StringComparison.Ordinal) ? IssueNumbers :
            arguments.StartsWith("pr list", StringComparison.Ordinal) ? PrNumbers :
            [];

        bool isPr = arguments.StartsWith("pr list", StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(ListJson(numbers, isPr));
        foreach (JsonElement element in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            yield return element.Clone();
        }

        await Task.CompletedTask;
    }

    public override async IAsyncEnumerable<JsonElement> StreamPaginatedApiAsync(
        string apiPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _invocations.Add($"api {apiPath}");
        ct.ThrowIfCancellationRequested();
        yield break;
#pragma warning disable CS0162 // Unreachable — required to make this an async iterator.
        await Task.CompletedTask;
#pragma warning restore CS0162
    }

    private static string RepoMetadataJson() =>
        """
        {
          "name": "fhir",
          "nameWithOwner": "HL7/fhir",
          "description": "test",
          "hasIssuesEnabled": true,
          "owner": { "login": "HL7" },
          "defaultBranchRef": { "name": "master" }
        }
        """;

    private static string DetailJson() =>
        """
        {
          "comments": [],
          "reviews": [],
          "commits": [],
          "baseRefName": "master",
          "mergedAt": null
        }
        """;

    private static string ListJson(int[] numbers, bool isPr)
    {
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < numbers.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($$"""
                {
                  "number": {{numbers[i]}},
                  "title": "Item {{numbers[i]}}",
                  "body": "body",
                  "state": "CLOSED",
                  "author": { "login": "someone" },
                  "assignees": [],
                  "labels": [],
                  "milestone": null,
                  "createdAt": "2024-01-01T00:00:00Z",
                  "updatedAt": "2024-01-02T00:00:00Z",
                  "closedAt": "2024-01-03T00:00:00Z",
                  {{(isPr ? "\"mergedAt\": null, \"headRefName\": \"topic\", \"baseRefName\": \"master\", \"isDraft\": false," : "")}}
                  "url": "https://github.com/HL7/fhir/issues/{{numbers[i]}}"
                }
                """);
        }

        sb.Append(']');
        return sb.ToString();
    }
}

/// <summary>
/// Phase 2 (slot 0826-01): a stop during ingestion must abort within one item and return a
/// partial result flagged <c>Canceled</c>, while ordinary per-item failures must still be
/// absorbed so the loop continues.
/// </summary>
public class GitHubCliProviderCancellationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public GitHubCliProviderCancellationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cli_cancel_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    private GitHubCliProvider CreateProvider(FakeGhCliRunner runner)
    {
        GitHubServiceOptions options = new()
        {
            SyncSchedule = "01:00:00",
            FhirCoreRepositories = ["HL7/fhir"],
            UtgRepositories = [],
            FhirExtensionsPackRepositories = [],
        };

        // The backfill path never touches IResponseCache.
        return new GitHubCliProvider(
            Options.Create(options),
            runner,
            _db,
            cache: null!,
            new GitHubBackfillCheckpointStore(_db, NullLogger<GitHubBackfillCheckpointStore>.Instance),
            NullLogger<GitHubCliProvider>.Instance);
    }

    [Fact]
    public async Task DownloadBackfillAsync_WhenCanceledMidRun_StopsWithinOneItem()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();

        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [10, 9, 8, 7, 6, 5, 4, 3, 2, 1],
            PrNumbers = [],
            CancelOnDetailCall = cts,
            CancelAtDetailCall = 3,
        };

        GitHubCliProvider provider = CreateProvider(runner);
        IngestionResult result = await provider.DownloadBackfillAsync("HL7/fhir", resumeFrom: null, cts.Token);

        Assert.True(
            runner.DetailCallCount <= 4,
            $"Expected the loop to abort within one item of the cancel point, but issued {runner.DetailCallCount} detail calls.");
        Assert.True(result.ItemsProcessed < 10);
    }

    [Fact]
    public async Task DownloadBackfillAsync_WhenCanceled_SetsCanceled_AndRecordsNoCancellationErrors()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();

        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [10, 9, 8, 7, 6, 5, 4, 3, 2, 1],
            PrNumbers = [],
            CancelOnDetailCall = cts,
            CancelAtDetailCall = 2,
        };

        GitHubCliProvider provider = CreateProvider(runner);
        IngestionResult result = await provider.DownloadBackfillAsync("HL7/fhir", resumeFrom: null, cts.Token);

        Assert.True(result.Canceled);
        Assert.DoesNotContain(result.Errors, e => e.Contains("cancel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DownloadBackfillAsync_WhenItemFailsWithRealError_ContinuesAndRecordsError()
    {
        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [5, 4, 3, 2, 1],
            PrNumbers = [],
            ThrowAtDetailCall = 2,
        };

        GitHubCliProvider provider = CreateProvider(runner);
        IngestionResult result = await provider.DownloadBackfillAsync("HL7/fhir", resumeFrom: null, CancellationToken.None);

        Assert.False(result.Canceled);
        Assert.Equal(5, result.ItemsProcessed);
        Assert.Single(result.Errors);
        Assert.Contains("synthetic failure", result.Errors[0]);
    }

    // ── Phase 4: resume, watermark, and terminal-state ownership ─────────

    private GitHubBackfillCheckpointStore CreateStore() =>
        new GitHubBackfillCheckpointStore(_db, NullLogger<GitHubBackfillCheckpointStore>.Instance);

    [Fact]
    public async Task DownloadBackfillAsync_WithResumeCursor_SkipsDetailFetchAboveWatermark()
    {
        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [10, 9, 8, 7, 6, 5, 4, 3, 2, 1],
            PrNumbers = [],
        };

        GitHubBackfillCursor cursor = new GitHubBackfillCursor { IssuesCompletedAbove = 6 };

        GitHubCliProvider provider = CreateProvider(runner);
        await provider.DownloadBackfillAsync("HL7/fhir", cursor, CancellationToken.None);

        // Items 10..6 are above the watermark and must skip their detail fetch; 5..1 must not.
        Assert.Equal(5, runner.DetailCallCount);
    }

    [Fact]
    public async Task DownloadBackfillAsync_WithPendingRetry_ReattemptsThoseItemsAboveWatermark()
    {
        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [10, 9, 8, 7, 6, 5, 4, 3, 2, 1],
            PrNumbers = [],
        };

        GitHubBackfillCursor cursor = new GitHubBackfillCursor
        {
            IssuesCompletedAbove = 6,
            PendingRetry = [9, 7],
        };

        GitHubCliProvider provider = CreateProvider(runner);
        await provider.DownloadBackfillAsync("HL7/fhir", cursor, CancellationToken.None);

        // 5 below the watermark, plus the two re-attempted pending items.
        Assert.Equal(7, runner.DetailCallCount);
    }

    [Fact]
    public async Task DownloadBackfillAsync_WhenItemFails_DoesNotAdvanceWatermarkPastIt_AndRecordsPendingRetry()
    {
        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [5, 4, 3, 2, 1],
            PrNumbers = [],
            ThrowAtDetailCall = 3, // item #3
        };

        GitHubCliProvider provider = CreateProvider(runner);
        await provider.DownloadBackfillAsync("HL7/fhir", resumeFrom: null, CancellationToken.None);

        GitHubBackfillCursor? cursor = CreateStore().ReadCursor("HL7/fhir");

        Assert.NotNull(cursor);
        Assert.Contains(3, cursor!.PendingRetry);
        // The walk continued below #3, but #3 itself stays in the retry set so the
        // watermark's "everything above is done, except PendingRetry" invariant holds.
        Assert.Equal(1, cursor.IssuesCompletedAbove);
    }

    [Fact]
    public async Task DownloadBackfillAsync_WhenCanceledMidItem_ExcludesThatItemFromWatermark()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();

        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [10, 9, 8, 7, 6, 5, 4, 3, 2, 1],
            PrNumbers = [],
            CancelOnDetailCall = cts,
            CancelAtDetailCall = 4, // item #7
        };

        GitHubCliProvider provider = CreateProvider(runner);
        IngestionResult result = await provider.DownloadBackfillAsync("HL7/fhir", resumeFrom: null, cts.Token);

        Assert.True(result.Canceled);

        GitHubBackfillCursor? cursor = CreateStore().ReadCursor("HL7/fhir");
        Assert.NotNull(cursor);
        // #10, #9 and #8 completed; the interrupted #7 must not be inside the watermark.
        Assert.Equal(8, cursor!.IssuesCompletedAbove);
        Assert.False(cursor.IssuesPhaseComplete);
    }

    [Fact]
    public async Task DownloadBackfillAsync_SortsDescending_WhenRunnerReturnsAscending()
    {
        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [1, 2, 3, 4, 5], // deliberately ascending
            PrNumbers = [],
        };

        GitHubCliProvider provider = CreateProvider(runner);
        await provider.DownloadBackfillAsync("HL7/fhir", resumeFrom: null, CancellationToken.None);

        // The watermark invariant depends on a descending walk, so the provider must sort
        // rather than inherit gh's ordering.
        Assert.Equal([5, 4, 3, 2, 1], runner.DetailNumbersInOrder);
    }

    [Fact]
    public async Task DownloadBackfillAsync_WhenCountEqualsBackfillLimit_DoesNotMarkPhaseComplete()
    {
        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [3, 2, 1],
            PrNumbers = [],
        };

        GitHubServiceOptions options = new()
        {
            SyncSchedule = "01:00:00",
            FhirCoreRepositories = ["HL7/fhir"],
            UtgRepositories = [],
            FhirExtensionsPackRepositories = [],
            GhCli = new GhCliConfiguration { BackfillLimit = 3 },
        };

        GitHubCliProvider provider = new GitHubCliProvider(
            Options.Create(options), runner, _db, cache: null!, CreateStore(),
            NullLogger<GitHubCliProvider>.Instance);

        await provider.DownloadBackfillAsync("HL7/fhir", resumeFrom: null, CancellationToken.None);

        GitHubBackfillCursor? cursor = CreateStore().ReadCursor("HL7/fhir");

        Assert.NotNull(cursor);
        Assert.False(cursor!.IssuesPhaseComplete);
    }

    [Fact]
    public async Task DownloadBackfillAsync_TraversingSkippedPrefix_DoesNotRegressWatermark()
    {
        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [10, 9, 8, 7, 6],
            PrNumbers = [200],
            // Keeps the repo incomplete so the progress row survives to be asserted on.
            FailDetailForNumbers = [200],
        };

        // Only #9 is outstanding; everything at or above #6 is otherwise done.
        GitHubBackfillCursor cursor = new GitHubBackfillCursor
        {
            IssuesCompletedAbove = 6,
            PendingRetry = [9],
        };

        GitHubCliProvider provider = CreateProvider(runner);
        await provider.DownloadBackfillAsync("HL7/fhir", cursor, CancellationToken.None);

        GitHubBackfillCursor? updated = CreateStore().ReadCursor("HL7/fhir");

        Assert.NotNull(updated);
        // Repairing #9 must not drag the watermark up to 9; it stays at 6.
        Assert.Equal(6, updated!.IssuesCompletedAbove);
        Assert.DoesNotContain(9, updated.PendingRetry);
    }

    [Fact]
    public async Task DownloadBackfillAsync_WhenBothPhasesExhaust_WritesTerminalMarkerAndClearsProgress()
    {
        FakeGhCliRunner runner = new FakeGhCliRunner
        {
            IssueNumbers = [4, 3],
            PrNumbers = [2, 1],
        };

        GitHubCliProvider provider = CreateProvider(runner);
        await provider.DownloadBackfillAsync("HL7/fhir", resumeFrom: null, CancellationToken.None);

        GitHubBackfillCheckpointStore store = CreateStore();

        Assert.Contains("HL7/fhir", store.GetCompletedRepos());
        Assert.Null(store.ReadCursor("HL7/fhir"));
    }
}
