using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Index;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="WindowIndexReader"/> against a seeded throwaway
/// <c>github.db</c>: ordered window loads with faithful <c>ChangedPaths</c>,
/// git-authoritative <c>%h</c> and reconstructed <c>%aI</c>, coverage-gap
/// detection (missing commit / file-incomplete), NULL-blob and old-schema
/// tolerance, repo scoping, and a missing-DB no-op.
/// </summary>
public sealed class WindowIndexReaderTests : IDisposable
{
    private const string Repo = "HL7/fhir";
    private readonly string _tempDir;
    private readonly string _dbPath;

    private static readonly string ShaA = new('a', 40);
    private static readonly string ShaB = new('b', 40);
    private static readonly string ShaC = new('c', 40);
    private static readonly string ShaEmpty = new('e', 40);
    private static readonly string ShaIncomplete = new('f', 40);
    private static readonly string ShaOtherRepo = new('9', 40);

    public WindowIndexReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "windowidx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "github.db");
        SeedDb(_dbPath, withBlobShaColumn: true);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    private static WindowShaEntry Entry(string sha, string shortSha) => new(sha, shortSha);

    private void SeedDb(string dbPath, bool withBlobShaColumn)
    {
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();

        string filesTable = withBlobShaColumn
            ? "CREATE TABLE github_commit_files (Id INTEGER PRIMARY KEY, CommitSha TEXT, FilePath TEXT, ChangeType TEXT, BlobSha TEXT);"
            : "CREATE TABLE github_commit_files (Id INTEGER PRIMARY KEY, CommitSha TEXT, FilePath TEXT, ChangeType TEXT);";

        using (SqliteCommand create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE github_commits (" +
                "Id INTEGER PRIMARY KEY, Sha TEXT, RepoFullName TEXT, Message TEXT, Body TEXT, " +
                "Author TEXT, Date TEXT, FilesChanged INTEGER);" +
                filesTable;
            create.ExecuteNonQuery();
        }

        // ShaA — two files (modify + add) in a deliberate insertion order, trailing
        // whitespace in subject/body/author to prove the reader trims like the walk.
        Commit(conn, 1, ShaA, "  FHIR-1 Fix Observation  ", "  Body text  ", "  Jane Dev  ",
            new DateTimeOffset(2026, 6, 10, 12, 30, 45, TimeSpan.FromHours(-6)), filesChanged: 2);
        FileRow(conn, 1, ShaA, "source/observation/observation.xml", "M", "1111111111111111111111111111111111111111", withBlobShaColumn);
        FileRow(conn, 2, ShaA, "source/observation/observation-notes.xml", "A", "2222222222222222222222222222222222222222", withBlobShaColumn);

        // ShaB — a rename (FilePath already the new path, per ingest) + a deletion
        // (NULL post-image blob). Older/earlier commit than ShaA.
        Commit(conn, 2, ShaB, "Rename + delete", "", "John Dev",
            new DateTimeOffset(2026, 6, 9, 8, 0, 0, TimeSpan.Zero), filesChanged: 2);
        FileRow(conn, 3, ShaB, "source/new-name.html", "R100", "3333333333333333333333333333333333333333", withBlobShaColumn);
        FileRow(conn, 4, ShaB, "source/gone.html", "D", null, withBlobShaColumn);

        // ShaEmpty — an empty commit (FilesChanged = 0, no file rows): NOT a gap.
        Commit(conn, 3, ShaEmpty, "Empty tree touch", "", "Dev",
            new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero), filesChanged: 0);

        // ShaIncomplete — FilesChanged > 0 but no file rows: file-incomplete gap.
        Commit(conn, 4, ShaIncomplete, "Partially ingested", "", "Dev",
            new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.Zero), filesChanged: 3);

        // ShaOtherRepo — present, but under a different repo (must not match).
        Commit(conn, 5, ShaOtherRepo, "Elsewhere", "", "Dev",
            new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero), filesChanged: 1, repo: "other/repo");
        FileRow(conn, 5, ShaOtherRepo, "x.txt", "M", "4444444444444444444444444444444444444444", withBlobShaColumn);
    }

    private static void Commit(
        SqliteConnection conn, int id, string sha, string message, string body, string author,
        DateTimeOffset date, int filesChanged, string repo = Repo)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO github_commits (Id, Sha, RepoFullName, Message, Body, Author, Date, FilesChanged) " +
            "VALUES ($id, $sha, $repo, $msg, $body, $author, $date, $fc)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sha", sha);
        cmd.Parameters.AddWithValue("$repo", repo);
        cmd.Parameters.AddWithValue("$msg", message);
        cmd.Parameters.AddWithValue("$body", string.IsNullOrEmpty(body) ? (object)DBNull.Value : body);
        cmd.Parameters.AddWithValue("$author", author);
        cmd.Parameters.AddWithValue("$date", date); // bound as DateTimeOffset, mirroring production serialization
        cmd.Parameters.AddWithValue("$fc", filesChanged);
        cmd.ExecuteNonQuery();
    }

    private static void FileRow(
        SqliteConnection conn, int id, string sha, string path, string changeType, string? blobSha, bool withBlobShaColumn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = withBlobShaColumn
            ? "INSERT INTO github_commit_files (Id, CommitSha, FilePath, ChangeType, BlobSha) VALUES ($id, $sha, $path, $ct, $blob)"
            : "INSERT INTO github_commit_files (Id, CommitSha, FilePath, ChangeType) VALUES ($id, $sha, $path, $ct)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sha", sha);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$ct", changeType);
        if (withBlobShaColumn)
        {
            cmd.Parameters.AddWithValue("$blob", (object?)blobSha ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Read_loads_commits_in_window_order_with_full_changed_paths()
    {
        // Window order is newest-first (rev-list): B then A.
        WindowLoad load = WindowIndexReader.Read(
            _dbPath, Repo, [Entry(ShaB, "bbbbbbbbbb"), Entry(ShaA, "aaaaaaaaaa")]);

        Assert.Equal(2, load.Commits.Count);
        Assert.Equal(ShaB, load.Commits[0].Sha);
        Assert.Equal(ShaA, load.Commits[1].Sha);

        // A's files keep insertion (git-diff) order; rename uses the stored new path.
        Assert.Equal(
            ["source/observation/observation.xml", "source/observation/observation-notes.xml"],
            load.Commits[1].ChangedPaths);
        Assert.Equal(["source/new-name.html", "source/gone.html"], load.Commits[0].ChangedPaths);
    }

    [Fact]
    public void Read_uses_git_authoritative_short_sha_not_truncation()
    {
        // The passed %h differs from a naive 12-char cut; the reader must use it verbatim.
        WindowLoad load = WindowIndexReader.Read(_dbPath, Repo, [Entry(ShaA, "aaaaaaaaaa")]);

        WindowCommit commit = Assert.Single(load.Commits);
        Assert.Equal("aaaaaaaaaa", commit.ShortSha);
    }

    [Fact]
    public void Read_reconstructs_author_date_and_trims_metadata()
    {
        WindowLoad load = WindowIndexReader.Read(_dbPath, Repo, [Entry(ShaA, "aaaaaaaaaa")]);

        WindowCommit commit = Assert.Single(load.Commits);
        Assert.Equal("2026-06-10T12:30:45-06:00", commit.AuthorDate);
        Assert.Equal("FHIR-1 Fix Observation", commit.Subject);
        Assert.Equal("Body text", commit.Body);
        Assert.Equal("Jane Dev", commit.AuthorName);
    }

    [Fact]
    public void Read_surfaces_change_type_and_blob_sha_including_null_for_deletion()
    {
        WindowLoad load = WindowIndexReader.Read(_dbPath, Repo, [Entry(ShaB, "bbbbbbbbbb")]);

        WindowCommit commit = Assert.Single(load.Commits);
        Assert.Equal(2, commit.ChangedFiles.Count);

        WindowChangedFile rename = commit.ChangedFiles[0];
        Assert.Equal("source/new-name.html", rename.Path);
        Assert.Equal("R100", rename.ChangeType);
        Assert.Equal("3333333333333333333333333333333333333333", rename.BlobSha);

        WindowChangedFile deletion = commit.ChangedFiles[1];
        Assert.Equal("D", deletion.ChangeType);
        Assert.Null(deletion.BlobSha); // deletion's NULL post-image blob still loads
    }

    [Fact]
    public void Read_reports_missing_commit_shas()
    {
        string missingSha = new('7', 40);
        WindowLoad load = WindowIndexReader.Read(
            _dbPath, Repo, [Entry(ShaA, "aaaaaaaaaa"), Entry(missingSha, "7777777777")]);

        Assert.False(load.Coverage.IsCovered);
        Assert.Equal([missingSha], load.Coverage.MissingCommitShas);
        // The covered commit is still returned; the missing one is skipped.
        WindowCommit only = Assert.Single(load.Commits);
        Assert.Equal(ShaA, only.Sha);
    }

    [Fact]
    public void Read_scopes_commits_to_the_requested_repo()
    {
        // ShaOtherRepo exists, but under "other/repo" — not covered for HL7/fhir.
        WindowLoad load = WindowIndexReader.Read(_dbPath, Repo, [Entry(ShaOtherRepo, "9999999999")]);

        Assert.Empty(load.Commits);
        Assert.Equal([ShaOtherRepo], load.Coverage.MissingCommitShas);
    }

    [Fact]
    public void Read_flags_file_incomplete_commit_but_not_empty_commit()
    {
        WindowLoad load = WindowIndexReader.Read(
            _dbPath, Repo, [Entry(ShaEmpty, "eeeeeeeeee"), Entry(ShaIncomplete, "ffffffffff")]);

        // Both commits are emitted (present in github_commits)...
        Assert.Equal(2, load.Commits.Count);
        // ...but only the FilesChanged>0-with-no-rows one is a coverage gap.
        Assert.Equal([ShaIncomplete], load.Coverage.FileIncompleteShas);
        Assert.Empty(load.Coverage.MissingCommitShas);
        Assert.False(load.Coverage.IsCovered);
    }

    [Fact]
    public void Read_reports_covered_when_no_gaps()
    {
        WindowLoad load = WindowIndexReader.Read(
            _dbPath, Repo, [Entry(ShaA, "aaaaaaaaaa"), Entry(ShaB, "bbbbbbbbbb"), Entry(ShaEmpty, "eeeeeeeeee")]);

        Assert.True(load.Coverage.IsCovered);
        Assert.Equal(3, load.Commits.Count);
    }

    [Fact]
    public void Read_tolerates_old_schema_without_blob_sha_column()
    {
        string legacyDir = Path.Combine(_tempDir, "legacy");
        Directory.CreateDirectory(legacyDir);
        string legacyDb = Path.Combine(legacyDir, "github.db");
        SeedDb(legacyDb, withBlobShaColumn: false);

        WindowLoad load = WindowIndexReader.Read(
            legacyDb, Repo, [Entry(ShaB, "bbbbbbbbbb"), Entry(ShaA, "aaaaaaaaaa")]);

        Assert.Equal(2, load.Commits.Count);
        Assert.True(load.Coverage.IsCovered);
        // Paths still reconstruct; every blob is NULL because the column is absent.
        Assert.Equal(["source/new-name.html", "source/gone.html"], load.Commits[0].ChangedPaths);
        Assert.All(load.Commits.SelectMany(c => c.ChangedFiles), f => Assert.Null(f.BlobSha));
    }

    [Fact]
    public void Read_returns_empty_and_uncovered_when_db_missing()
    {
        string nope = Path.Combine(_tempDir, "does-not-exist.db");
        WindowLoad load = WindowIndexReader.Read(nope, Repo, [Entry(ShaA, "aaaaaaaaaa"), Entry(ShaB, "bbbbbbbbbb")]);

        Assert.Empty(load.Commits);
        Assert.False(load.Coverage.IsCovered);
        Assert.Equal([ShaA, ShaB], load.Coverage.MissingCommitShas);
    }

    [Fact]
    public void Read_returns_covered_empty_for_empty_window()
    {
        WindowLoad load = WindowIndexReader.Read(_dbPath, Repo, []);
        Assert.Empty(load.Commits);
        Assert.True(load.Coverage.IsCovered);
    }
}
