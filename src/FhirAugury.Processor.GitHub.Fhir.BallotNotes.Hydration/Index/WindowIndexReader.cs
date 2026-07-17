using System.Globalization;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Index;

/// <summary>
/// Coverage report for a window load: which window SHAs the GitHub index does not
/// cover. A gap in either set means the walk cannot be reconstructed faithfully.
/// </summary>
/// <param name="MissingCommitShas">
/// Window SHAs with no <c>github_commits</c> row for the repo (stale index, or
/// history older than the initial-extraction cap).
/// </param>
/// <param name="FileIncompleteShas">
/// Window SHAs present in <c>github_commits</c> with <c>FilesChanged &gt; 0</c> but
/// zero <c>github_commit_files</c> rows (a partially-ingested commit).
/// </param>
public sealed record CoverageResult(
    IReadOnlyList<string> MissingCommitShas,
    IReadOnlyList<string> FileIncompleteShas)
{
    /// <summary>True when the index fully covers the window.</summary>
    public bool IsCovered => MissingCommitShas.Count == 0 && FileIncompleteShas.Count == 0;

    /// <summary>A fully-covered (empty-gap) result.</summary>
    public static CoverageResult Covered { get; } = new([], []);
}

/// <summary>The result of loading a commit window from the index: ordered commits + coverage.</summary>
public sealed record WindowLoad(
    IReadOnlyList<WindowCommit> Commits,
    CoverageResult Coverage);

/// <summary>
/// Loads a <c>since..HEAD</c> commit window from the read-only GitHub source DB
/// (<c>github_commits</c> + <c>github_commit_files</c>) in one pass, preserving the
/// caller's rev-list order, and reports whether the index covers the window.
/// Modelled on <see cref="Sources.PrTicketResolver"/>'s raw read-only SQLite
/// access; never throws on a SQLite error (degrades to "not covered"). Tolerates an
/// un-migrated index that lacks the <c>github_commit_files.BlobSha</c> column.
/// </summary>
public static class WindowIndexReader
{
    private const int ChunkSize = 400;

    /// <summary>
    /// Reads the <paramref name="window"/> commits (ordered, git-authoritative
    /// full + short SHAs) from the index at <paramref name="githubDbPath"/> for
    /// <paramref name="repoFullName"/> (<c>"{owner}/{name}"</c>). A missing DB, a
    /// SQLite error, or absent rows are reported through <see cref="CoverageResult"/>
    /// rather than thrown.
    /// </summary>
    public static WindowLoad Read(
        string githubDbPath,
        string repoFullName,
        IReadOnlyList<WindowShaEntry> window,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Count == 0) return new WindowLoad([], CoverageResult.Covered);

        // Ordered full SHAs + the git-authoritative %h for each (dedup defensively).
        List<string> orderedShas = [];
        Dictionary<string, string> shortByFull = new(StringComparer.Ordinal);
        foreach (WindowShaEntry entry in window)
        {
            if (string.IsNullOrWhiteSpace(entry.Sha)) continue;
            if (!shortByFull.ContainsKey(entry.Sha))
            {
                orderedShas.Add(entry.Sha);
                shortByFull[entry.Sha] = string.IsNullOrEmpty(entry.ShortSha) ? ShortSha(entry.Sha) : entry.ShortSha;
            }
        }

        if (string.IsNullOrWhiteSpace(githubDbPath) || !File.Exists(githubDbPath))
        {
            logger?.LogWarning("GitHub index DB not found at {Db}; commit window is uncovered.", githubDbPath);
            return new WindowLoad([], new CoverageResult(orderedShas, []));
        }

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = githubDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;

        try
        {
            using SqliteConnection connection = new(connectionString);
            connection.Open();

            bool hasBlobSha = ColumnExists(connection, "github_commit_files", "BlobSha");

            Dictionary<string, CommitMeta> metaBySha = LoadCommitMeta(connection, repoFullName, orderedShas);
            Dictionary<string, List<WindowChangedFile>> filesBySha = LoadCommitFiles(connection, orderedShas, hasBlobSha);

            List<WindowCommit> commits = [];
            List<string> missing = [];
            List<string> fileIncomplete = [];

            foreach (string sha in orderedShas)
            {
                if (!metaBySha.TryGetValue(sha, out CommitMeta? meta))
                {
                    missing.Add(sha);
                    continue;
                }

                filesBySha.TryGetValue(sha, out List<WindowChangedFile>? files);
                files ??= [];

                // A commit git counts as touching files but with no file rows was only
                // partially ingested; flag it (empty commits are not a gap).
                if (meta.FilesChanged > 0 && files.Count == 0)
                {
                    fileIncomplete.Add(sha);
                }

                commits.Add(new WindowCommit
                {
                    Sha = sha,
                    ShortSha = shortByFull[sha],
                    AuthorName = meta.AuthorName,
                    AuthorDate = meta.AuthorDate,
                    Subject = meta.Subject,
                    Body = meta.Body,
                    ChangedFiles = files,
                    ChangedPaths = [.. files.Select(f => f.Path)],
                });
            }

            return new WindowLoad(commits, new CoverageResult(missing, fileIncomplete));
        }
        catch (SqliteException ex)
        {
            logger?.LogWarning(ex, "Window index read failed against {Db}; treating window as uncovered.", githubDbPath);
            return new WindowLoad([], new CoverageResult(orderedShas, []));
        }
    }

    private static Dictionary<string, CommitMeta> LoadCommitMeta(
        SqliteConnection connection,
        string repoFullName,
        IReadOnlyList<string> shas)
    {
        Dictionary<string, CommitMeta> map = new(StringComparer.Ordinal);
        foreach (string[] chunk in shas.Chunk(ChunkSize))
        {
            using SqliteCommand cmd = connection.CreateCommand();
            string inClause = BuildInClause(cmd, chunk, "$s");
            cmd.CommandText =
                "SELECT Sha, Message, Body, Author, Date, FilesChanged FROM github_commits " +
                $"WHERE RepoFullName = $repo AND Sha IN ({inClause})";
            cmd.Parameters.AddWithValue("$repo", repoFullName);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                string sha = reader.GetString(0);
                string subject = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                string body = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                string author = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
                string authorDate = ReadAuthorDate(reader, 4);
                int filesChanged = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                map[sha] = new CommitMeta(subject, body, author, authorDate, filesChanged);
            }
        }
        return map;
    }

    private static Dictionary<string, List<WindowChangedFile>> LoadCommitFiles(
        SqliteConnection connection,
        IReadOnlyList<string> shas,
        bool hasBlobSha)
    {
        Dictionary<string, List<WindowChangedFile>> map = new(StringComparer.Ordinal);
        string blobSelect = hasBlobSha ? "BlobSha" : "NULL AS BlobSha";

        foreach (string[] chunk in shas.Chunk(ChunkSize))
        {
            using SqliteCommand cmd = connection.CreateCommand();
            string inClause = BuildInClause(cmd, chunk, "$s");
            cmd.CommandText =
                $"SELECT CommitSha, FilePath, ChangeType, {blobSelect} FROM github_commit_files " +
                $"WHERE CommitSha IN ({inClause}) ORDER BY Id";

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                string sha = reader.GetString(0);
                string path = reader.GetString(1);
                string changeType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                string? blobSha = reader.IsDBNull(3) ? null : reader.GetString(3);

                if (!map.TryGetValue(sha, out List<WindowChangedFile>? list))
                {
                    list = [];
                    map[sha] = list;
                }
                list.Add(new WindowChangedFile(path, changeType, blobSha));
            }
        }
        return map;
    }

    /// <summary>
    /// Reconstructs git <c>%aI</c> (strict ISO-8601, whole seconds, original offset)
    /// from the stored <see cref="DateTimeOffset"/>, so the round-trip is
    /// byte-identical to the git-log walk. Degrades to the raw text on a parse miss.
    /// </summary>
    private static string ReadAuthorDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return string.Empty;
        try
        {
            DateTimeOffset date = reader.GetFieldValue<DateTimeOffset>(ordinal);
            return date.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            return reader.GetString(ordinal);
        }
    }

    /// <summary>True when <paramref name="column"/> exists on <paramref name="table"/>.</summary>
    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // table_info columns: cid, name, type, notnull, dflt_value, pk
                if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (SqliteException)
        {
            // Missing table etc. → treat as no column (a broken/absent index).
        }
        return false;
    }

    private static string BuildInClause(SqliteCommand cmd, IReadOnlyList<string> values, string prefix)
    {
        string[] names = new string[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            string name = $"{prefix}{i}";
            names[i] = name;
            cmd.Parameters.AddWithValue(name, values[i]);
        }
        return string.Join(", ", names);
    }

    private static string ShortSha(string fullSha)
    {
        string full = fullSha.Trim();
        return full.Length > 12 ? full[..12] : full;
    }

    private sealed record CommitMeta(
        string Subject,
        string Body,
        string AuthorName,
        string AuthorDate,
        int FilesChanged);
}
