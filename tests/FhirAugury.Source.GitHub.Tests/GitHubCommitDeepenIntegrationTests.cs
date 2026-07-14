using System.Diagnostics;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Lazily probes whether a usable <c>git</c> executable is on PATH so the
/// real-git integration tests below can be skipped cleanly on machines/CI
/// without git, rather than failing.
/// </summary>
internal static class GitProbe
{
    private static readonly Lazy<bool> Probe = new(() =>
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    public static bool IsAvailable => Probe.Value;
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself when git is not available on
/// PATH (see <see cref="GitProbe"/>). Reports as "Skipped" rather than failing.
/// </summary>
public sealed class GitFactAttribute : FactAttribute
{
    public GitFactAttribute()
    {
        if (!GitProbe.IsAvailable)
            Skip = "git is not available on PATH; skipping real-git integration test.";
    }
}

/// <summary>
/// End-to-end proof of the backward-deepening path in
/// <see cref="GitHubCommitFileExtractor.ExtractAsync"/> against a real temp git
/// repository and a temp <see cref="GitHubDatabase"/> (slot 0714-01). Covers:
/// deepen-to-full-history, idempotent re-run, orphan-file (interrupted
/// files-first pass) repair with no duplicates, and the finite-cap no-deepen
/// control. The first real-git test in this project; git-guarded via
/// <see cref="GitFactAttribute"/>.
/// </summary>
public class GitHubCommitDeepenIntegrationTests : IDisposable
{
    private const string Repo = "owner/repo";

    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly string _cloneDir;

    public GitHubCommitDeepenIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"commit_deepen_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _cloneDir = Path.Combine(Path.GetTempPath(), $"commit_deepen_clone_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cloneDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
        TestFileCleanup.SafeDeleteDirectory(_cloneDir);
    }

    // ── Tests ────────────────────────────────────────────────────────

    [GitFact]
    public async Task Deepen_UncappedRepo_BackfillsFullHistoryBackward()
    {
        List<string> shas = InitLinearRepo(6); // shas[0] = root/oldest, shas[5] = tip/HEAD
        GitHubCommitFileExtractor extractor = NewExtractor();

        // Simulate the shallow, count-capped slice: only the newest commit.
        await extractor.ExtractAsync(_cloneDir, Repo, maxInitialCommits: 1);
        Assert.Equal(1, CommitCount());
        Assert.True(CommitExists(shas[5]));   // tip present
        Assert.False(CommitExists(shas[0]));  // root not yet ingested

        // Uncapped run: deepen backward to full history in one pass.
        await extractor.ExtractAsync(_cloneDir, Repo, maxInitialCommits: 0);

        Assert.Equal(6, CommitCount());
        Assert.True(CommitExists(shas[0]));   // root (oldest reachable) now present
        foreach (string sha in shas)
            Assert.True(CommitExists(sha));
    }

    [GitFact]
    public async Task Deepen_SecondRun_IsIdempotent()
    {
        List<string> shas = InitLinearRepo(6);
        GitHubCommitFileExtractor extractor = NewExtractor();

        await extractor.ExtractAsync(_cloneDir, Repo, maxInitialCommits: 1);
        await extractor.ExtractAsync(_cloneDir, Repo, maxInitialCommits: 0); // deepen

        int commitsAfterDeepen = CommitCount();
        int filesAfterDeepen = FileCount();
        Assert.Equal(6, commitsAfterDeepen);
        Assert.Equal(6, filesAfterDeepen); // one added file per commit

        // Root is now ingested, so the gate is closed: a re-run must add nothing
        // and must not duplicate file rows.
        await extractor.ExtractAsync(_cloneDir, Repo, maxInitialCommits: 0);

        Assert.Equal(commitsAfterDeepen, CommitCount());
        Assert.Equal(filesAfterDeepen, FileCount());
        _ = shas;
    }

    [GitFact]
    public async Task Deepen_RepairsOrphanFileRows_WithoutDuplicates()
    {
        List<string> shas = InitLinearRepo(6);
        GitHubCommitFileExtractor extractor = NewExtractor();

        // Shallow slice: newest commit only (1 commit + its 1 file).
        await extractor.ExtractAsync(_cloneDir, Repo, maxInitialCommits: 1);
        Assert.Equal(1, FileCount());

        // Simulate an interrupted files-first deepen pass: orphan file rows for
        // two MIDDLE commits whose commit rows (and the root) were never written.
        // Their paths match what the extractor will re-derive, so a missing
        // delete-before-reinsert would surface as duplicate rows.
        InsertOrphanFile(shas[2], "f2.txt");
        InsertOrphanFile(shas[3], "f3.txt");
        Assert.Equal(3, FileCount()); // tip's file + 2 orphans

        // Deepen: delete orphans for the SHAs being written, reinsert, land root.
        await extractor.ExtractAsync(_cloneDir, Repo, maxInitialCommits: 0);

        Assert.Equal(6, CommitCount());
        Assert.True(CommitExists(shas[0])); // root present ⇒ sound completion
        foreach (string sha in shas)
            Assert.Equal(1, FileCountForCommit(sha)); // exactly one row each — no dups
        Assert.Equal(6, FileCount());                 // no orphaned rows remain
    }

    [GitFact]
    public async Task FiniteCap_WithPriorHistory_DoesNotDeepen()
    {
        List<string> shas = InitLinearRepo(6);
        GitHubCommitFileExtractor extractor = NewExtractor();

        await extractor.ExtractAsync(_cloneDir, Repo, maxInitialCommits: 1);
        Assert.Equal(1, CommitCount());

        // Finite cap + prior history → forward-only ({lastSha}..HEAD); the root
        // gate is never consulted, so older commits are NOT backfilled.
        await extractor.ExtractAsync(_cloneDir, Repo, maxInitialCommits: 500);

        Assert.Equal(1, CommitCount());
        Assert.False(CommitExists(shas[0])); // root still absent
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private GitHubCommitFileExtractor NewExtractor() =>
        new(_db, NullLogger<GitHubCommitFileExtractor>.Instance);

    /// <summary>
    /// Creates a linear git history of <paramref name="count"/> commits, each
    /// adding a distinct file (so each commit has exactly one file row) with a
    /// deterministic, strictly increasing commit date and a FHIR-style message.
    /// Returns the SHAs oldest-first (index 0 = parentless root, last = HEAD/tip).
    /// </summary>
    private List<string> InitLinearRepo(int count)
    {
        RunGit(_cloneDir, "init -q");
        DateTimeOffset baseDate = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int k = 0; k < count; k++)
        {
            File.WriteAllText(Path.Combine(_cloneDir, $"f{k}.txt"), $"content {k}\n");
            RunGit(_cloneDir, $"add f{k}.txt");
            RunGit(
                _cloneDir,
                $"-c user.email=test@augury.example -c user.name=\"Augury Test\" -c commit.gpgsign=false " +
                $"commit -q -m \"feat: change {k} FHIR-{1000 + k}\"",
                baseDate.AddDays(k));
        }

        string log = RunGit(_cloneDir, "log --reverse --format=%H");
        return [.. log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())];
    }

    private static string RunGit(string workingDir, string args, DateTimeOffset? date = null)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (date is DateTimeOffset d)
        {
            string gitDate = $"@{d.ToUnixTimeSeconds()} +0000";
            psi.Environment["GIT_AUTHOR_DATE"] = gitDate;
            psi.Environment["GIT_COMMITTER_DATE"] = gitDate;
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed ({process.ExitCode}): {stderr}");

        return stdout;
    }

    private void InsertOrphanFile(string commitSha, string filePath)
    {
        using SqliteConnection connection = _db.OpenConnection();
        List<GitHubCommitFileRecord> rows =
        [
            new GitHubCommitFileRecord
            {
                Id = GitHubCommitFileRecord.GetIndex(),
                CommitSha = commitSha,
                FilePath = filePath,
                ChangeType = "A",
                BlobSha = null,
            },
        ];
        rows.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
    }

    private int CommitCount()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM github_commits WHERE RepoFullName = @repo";
        cmd.Parameters.AddWithValue("@repo", Repo);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private bool CommitExists(string sha)
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM github_commits WHERE RepoFullName = @repo AND Sha = @sha LIMIT 1";
        cmd.Parameters.AddWithValue("@repo", Repo);
        cmd.Parameters.AddWithValue("@sha", sha);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Total file rows in the (single-repo) temp DB, orphans included.</summary>
    private int FileCount()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM github_commit_files";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int FileCountForCommit(string sha)
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM github_commit_files WHERE CommitSha = @sha";
        cmd.Parameters.AddWithValue("@sha", sha);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
