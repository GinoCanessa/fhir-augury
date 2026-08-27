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

    /// <summary>Detail-fetch invocations observed so far (repo metadata excluded).</summary>
    public int DetailCallCount { get; private set; }

    /// <summary>Counts invocations whose argument string starts with the given prefix.</summary>
    public int CountInvocations(string prefix) =>
        _invocations.Count(a => a.StartsWith(prefix, StringComparison.Ordinal));

    public override Task<JsonDocument> RunAsync(string arguments, CancellationToken ct)
    {
        _invocations.Add(arguments);

        if (arguments.StartsWith("repo view", StringComparison.Ordinal))
            return Task.FromResult(JsonDocument.Parse(RepoMetadataJson()));

        // Everything else on this path is a per-item detail fetch.
        DetailCallCount++;

        if (ThrowAtDetailCall > 0 && DetailCallCount == ThrowAtDetailCall)
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
        IngestionResult result = await provider.DownloadBackfillAsync("HL7/fhir", cts.Token);

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
        IngestionResult result = await provider.DownloadBackfillAsync("HL7/fhir", cts.Token);

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
        IngestionResult result = await provider.DownloadBackfillAsync("HL7/fhir", CancellationToken.None);

        Assert.False(result.Canceled);
        Assert.Equal(5, result.ItemsProcessed);
        Assert.Single(result.Errors);
        Assert.Contains("synthetic failure", result.Errors[0]);
    }
}
