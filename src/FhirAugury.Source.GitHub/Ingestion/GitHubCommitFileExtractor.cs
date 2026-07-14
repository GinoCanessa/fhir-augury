using System.Diagnostics;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Extracts commit metadata and changed files from a local git clone using a
/// two-pass git log strategy: <c>--raw --no-abbrev</c> for change types and
/// post-image blob SHAs, <c>--numstat</c> for per-file line counts, merged by
/// SHA. Normal runs walk forward (<c>{lastSha}..HEAD</c>); an initial run walks
/// back from HEAD capped by <c>maxInitialCommits</c> (non-positive = full
/// history). When a repo is configured uncapped, an already-ingested slice is
/// deepened backward automatically until its parentless root commit(s) are
/// stored (see <see cref="ShouldDeepen"/>). That root gate assumes a <b>full</b>
/// clone — a shallow/grafted clone would report its graft boundary as a root and
/// could close the gate on an incomplete history.
/// </summary>
public class GitHubCommitFileExtractor(GitHubDatabase database, ILogger<GitHubCommitFileExtractor> logger)
{
    private const char RecordSeparator = '\x00';
    private const char FieldSeparator = '\x01';
    private const string EndHeaderMarker = "---END-HEADER---";
    internal const int RenameDetectionLimit = 5000;

    /// <summary>
    /// Extracts commits and their changed files from the local clone,
    /// storing them in the database. Processes commits newer than the last known SHA.
    /// </summary>
    public async Task ExtractAsync(string clonePath, string repoFullName, int maxInitialCommits = 500, CancellationToken ct = default)
    {
        if (!Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            logger.LogWarning("No git repository found at {Path}", clonePath);
            return;
        }

        // Find the last known commit SHA to do incremental extraction
        string? lastSha = null;
        using (SqliteConnection conn = database.OpenConnection())
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Sha FROM github_commits WHERE RepoFullName = @repo ORDER BY Date DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            lastSha = cmd.ExecuteScalar()?.ToString();
        }

        (string sinceArg, string limitArg) = BuildLogRange(lastSha, maxInitialCommits);

        // Backward deepening: when a repo is configured uncapped (full history)
        // and already has commits but its parentless root commit(s) are not yet
        // ingested, re-issue the full-history range instead of the forward-only
        // {lastSha}..HEAD. The pre-loaded-SHA dedup below then inserts only the
        // missing (older) commits — deepening the window in one pass while still
        // catching any new tip commits (HEAD is a superset of {lastSha}..HEAD).
        // Once the root(s) land this gate closes and subsequent runs revert to
        // cheap forward increments. The guard keeps finite-cap and first-ever
        // runs on the exact prior code path (no rev-list spawn, no behavior
        // change).
        bool deepen = false;
        if (maxInitialCommits <= 0 && lastSha is not null)
        {
            bool allRootsIngested = await AreAllRootsIngestedAsync(clonePath, repoFullName, ct);
            deepen = ShouldDeepen(maxInitialCommits, hasPriorHistory: true, allRootsIngested);
            if (deepen)
            {
                logger.LogInformation(
                    "Deepening full commit history for {Repo} (backfilling older commits)", repoFullName);
                (sinceArg, limitArg) = BuildLogRange(lastSha, maxInitialCommits, deepen: true);
            }
        }

        // Pass 1: metadata + --raw (change types + post-image blob SHAs)
        string pass1Args = BuildPass1Args(sinceArg, limitArg);
        string pass1Output = await RunGitAsync(clonePath, pass1Args, ct);

        if (string.IsNullOrWhiteSpace(pass1Output)) return;

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> commits = ParsePass1(pass1Output, repoFullName);

        if (commits.Count == 0) return;

        // Pass 2: numstat (per-file line counts → summed for commit-level totals)
        string pass2Args = $"log {sinceArg}{limitArg} --format=%H --numstat";
        string pass2Output = await RunGitAsync(clonePath, pass2Args, ct);

        if (!string.IsNullOrWhiteSpace(pass2Output))
        {
            Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> stats = ParsePass2(pass2Output);
            MergeStats(commits, stats);
        }

        using SqliteConnection connection = database.OpenConnection();

        // Pre-fetch existing SHAs for this repo (single query) to avoid per-commit SELECT.
        HashSet<string> existingShas = new HashSet<string>(StringComparer.Ordinal);
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Sha FROM github_commits WHERE RepoFullName = @repo";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read()) existingShas.Add(reader.GetString(0));
        }

        List<GitHubCommitRecord> newCommits = [];
        List<GitHubCommitFileRecord> newFiles = [];
        foreach ((GitHubCommitRecord? commit, List<GitHubCommitFileRecord>? files) in commits)
        {
            ct.ThrowIfCancellationRequested();
            if (existingShas.Contains(commit.Sha)) continue;

            newCommits.Add(commit);
            newFiles.AddRange(files);
        }

        const int batchSize = 1000;

        // Crash-safe / idempotent write (files first, commits last).
        //
        // Each generated batch Insert wraps its work in its OWN transaction, so
        // commits and files cannot share one enclosing transaction. Instead we
        // (1) delete any github_commit_files rows for exactly the SHAs we are
        // about to insert — cleaning up file rows left by a prior interrupted
        // pass without duplicating them, since that index is non-unique — then
        // (2) insert the file rows, then (3) insert the commit rows LAST. Because
        // the oldest (root) commit is the final row written in the newest-first
        // walk, "root present" soundly implies every commit AND its files are
        // present, which is exactly what the deepen root-gate reads. For
        // brand-new commits the delete is a no-op, so the normal incremental path
        // writes identical data as before.
        List<string> newShas = [.. newCommits.Select(c => c.Sha)];
        DeleteCommitFilesForShas(connection, newShas);

        for (int i = 0; i < newFiles.Count; i += batchSize)
        {
            List<GitHubCommitFileRecord> batch =
                newFiles.GetRange(i, Math.Min(batchSize, newFiles.Count - i));
            batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        for (int i = 0; i < newCommits.Count; i += batchSize)
        {
            List<GitHubCommitRecord> batch =
                newCommits.GetRange(i, Math.Min(batchSize, newCommits.Count - i));
            batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        int commitCount = newCommits.Count;
        int fileCount = newFiles.Count;

        logger.LogInformation(
            "Extracted {Commits} commits and {Files} file changes from {Repo}",
            commitCount, fileCount, repoFullName);
    }

    /// <summary>
    /// Parses Pass 1 output: NUL-delimited records with SOH-delimited header fields,
    /// followed by --raw --no-abbrev lines after the ---END-HEADER--- sentinel.
    /// </summary>
    internal static List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> ParsePass1(
        string output, string repoFullName)
    {
        List<(GitHubCommitRecord, List<GitHubCommitFileRecord>)> results = [];
        string[] blocks = output.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string block in blocks)
        {
            string trimmedBlock = block.Trim();
            if (string.IsNullOrEmpty(trimmedBlock)) continue;

            string[] fields = trimmedBlock.Split(FieldSeparator);
            if (fields.Length < 11) continue;

            string sha = fields[0].Trim();
            if (sha.Length < 7) continue;

            string authorName = fields[1].Trim();
            string authorEmail = fields[2].Trim();
            string authorDateStr = fields[3].Trim();
            string committerName = fields[4].Trim();
            string committerEmail = fields[5].Trim();
            // fields[6] = committer date (parsed but deferred from storage)
            string subject = fields[7].Trim();
            string body = fields[8].Trim();
            string refs = fields[9].Trim();

            // fields[10] starts with "---END-HEADER---" followed by name-status lines
            string remainder = fields[10];
            int markerIdx = remainder.IndexOf(EndHeaderMarker, StringComparison.Ordinal);
            string fileSection = markerIdx >= 0
                ? remainder[(markerIdx + EndHeaderMarker.Length)..]
                : "";

            DateTimeOffset date = DateTimeOffset.TryParse(authorDateStr, out DateTimeOffset d) ? d : DateTimeOffset.MinValue;

            GitHubCommitRecord commit = new GitHubCommitRecord
            {
                Id = GitHubCommitRecord.GetIndex(),
                Sha = sha,
                RepoFullName = repoFullName,
                Message = subject,
                Body = string.IsNullOrEmpty(body) ? null : body,
                Author = authorName,
                AuthorEmail = string.IsNullOrEmpty(authorEmail) ? null : authorEmail,
                CommitterName = string.IsNullOrEmpty(committerName) ? null : committerName,
                CommitterEmail = string.IsNullOrEmpty(committerEmail) ? null : committerEmail,
                Date = date,
                Url = $"https://github.com/{repoFullName}/commit/{sha}",
                Refs = string.IsNullOrEmpty(refs) ? null : refs,
            };

            List<GitHubCommitFileRecord> files = ParseRawLines(fileSection, sha);
            results.Add((commit, files));
        }

        return results;
    }

    /// <summary>
    /// All-zero 40-hex blob sentinel git emits for the missing side of an
    /// add (old blob) or delete (new blob).
    /// </summary>
    private const string ZeroBlob = "0000000000000000000000000000000000000000";

    /// <summary>
    /// Parses <c>git ... --raw --no-abbrev</c> lines of the form
    /// <c>:&lt;oldmode&gt; &lt;newmode&gt; &lt;oldblob&gt; &lt;newblob&gt; &lt;status&gt;\t&lt;path&gt;</c>
    /// (rename/copy rows carry a second <c>\t&lt;newpath&gt;</c>). Records only
    /// A/M/D/R/C rows (type-changes <c>T</c> and others are ignored, matching the
    /// prior name-status behavior), using the new path for R/C and capturing the
    /// post-image blob SHA (<c>null</c> for deletions' all-zero sentinel).
    /// </summary>
    internal static List<GitHubCommitFileRecord> ParseRawLines(string section, string sha)
    {
        List<GitHubCommitFileRecord> files = [];
        string[] lines = section.Split('\n', StringSplitOptions.None);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] != ':') continue;

            // Split metadata (before first tab) from the path(s).
            string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            // Metadata: ":<oldmode> <newmode> <oldblob> <newblob> <status>"
            string[] meta = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 5) continue;

            string changeType = meta[^1];
            string newBlob = meta[^2];
            if (changeType.Length == 0) continue;

            char statusChar = changeType[0];
            if (statusChar is not ('A' or 'M' or 'D' or 'R' or 'C')) continue;

            // R/C rows carry old + new paths; use the new path.
            bool isRenameOrCopy = statusChar is 'R' or 'C';
            string filePath = isRenameOrCopy && parts.Length >= 3
                ? parts[2].Trim()
                : parts[1].Trim();

            string? blobSha = string.Equals(newBlob, ZeroBlob, StringComparison.Ordinal)
                ? null
                : newBlob;

            files.Add(new GitHubCommitFileRecord
            {
                Id = GitHubCommitFileRecord.GetIndex(),
                CommitSha = sha,
                FilePath = filePath,
                ChangeType = changeType,
                BlobSha = blobSha,
            });
        }

        return files;
    }

    /// <summary>
    /// Parses Pass 2 output: SHA lines followed by numstat lines (insertions\tdeletions\tpath).
    /// Returns commit-level totals keyed by SHA.
    /// </summary>
    internal static Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> ParsePass2(string output)
    {
        Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> stats = [];
        string[] lines = output.Split('\n', StringSplitOptions.None);
        int i = 0;

        while (i < lines.Length)
        {
            // Skip blank lines
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
            if (i >= lines.Length) break;

            // SHA line (40 hex chars)
            string sha = lines[i].Trim();
            i++;
            if (sha.Length < 7) continue;

            // Skip blank line between SHA and numstat
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;

            int filesChanged = 0, insertions = 0, deletions = 0;

            // Read numstat lines until blank line or next SHA-like line
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                string numLine = lines[i].Trim();
                string[] parts = numLine.Split('\t', StringSplitOptions.None);
                if (parts.Length >= 3)
                {
                    filesChanged++;
                    // Binary files show "-\t-\tpath"
                    if (parts[0] != "-" && int.TryParse(parts[0], out int ins))
                        insertions += ins;
                    if (parts[1] != "-" && int.TryParse(parts[1], out int del))
                        deletions += del;
                }
                i++;
            }

            stats[sha] = (filesChanged, insertions, deletions);
        }

        return stats;
    }

    /// <summary>Merges Pass 2 numstat totals into Pass 1 commit records by SHA.</summary>
    internal static void MergeStats(
        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> commits,
        Dictionary<string, (int FilesChanged, int Insertions, int Deletions)> stats)
    {
        foreach ((GitHubCommitRecord commit, _) in commits)
        {
            if (stats.TryGetValue(commit.Sha, out (int FilesChanged, int Insertions, int Deletions) s))
            {
                commit.FilesChanged = s.FilesChanged;
                commit.Insertions = s.Insertions;
                commit.Deletions = s.Deletions;
            }
        }
    }

    /// <summary>
    /// Builds the Pass-1 git argument string (metadata + --raw --no-abbrev).
    /// The `-c diff.renameLimit` config flag must precede the `log` subcommand
    /// so git applies it; this raises the rename-detection cap to preserve
    /// accurate R/C rows on large commits instead of emitting a benign warning.
    /// `--raw --no-abbrev` is a superset of `--name-status`: it carries the same
    /// A/M/D/R/C status plus the full (un-abbreviated) post-image blob SHA per
    /// file, at no extra spawn cost. Rename detection stays on git's
    /// `diff.renames` default (no `-M`), matching prior behavior.
    /// </summary>
    internal static string BuildPass1Args(string sinceArg, string limitArg) =>
        $"-c diff.renameLimit={RenameDetectionLimit} log {sinceArg}{limitArg} " +
        $"--raw --no-abbrev --format=%x00%H%x01%an%x01%ae%x01%aI%x01%cn%x01%ce%x01%cI%x01%s%x01%b%x01%D%x01{EndHeaderMarker}";

    /// <summary>
    /// Builds the git log range and optional limit arguments.
    /// When a previous SHA exists and <paramref name="deepen"/> is false, uses
    /// "{sha}..HEAD" for forward-only incremental extraction.
    /// Otherwise uses "HEAD" with a -n limit to cap initial extraction,
    /// avoiding the HEAD~N crash when a repo has fewer than N commits.
    /// A non-positive <paramref name="maxInitialCommits"/> removes the cap
    /// (full-history extraction). When <paramref name="deepen"/> is true the
    /// forward-only shortcut is bypassed and the full-history range is used even
    /// though a prior SHA exists, so the pre-loaded-SHA dedup backfills only the
    /// missing (older) commits.
    /// </summary>
    internal static (string SinceArg, string LimitArg) BuildLogRange(
        string? lastSha, int maxInitialCommits = 500, bool deepen = false)
    {
        if (lastSha is not null && !deepen)
            return ($"{lastSha}..HEAD", "");
        return maxInitialCommits > 0 ? ("HEAD", $" -n {maxInitialCommits}") : ("HEAD", "");
    }

    /// <summary>
    /// Parses the output of <c>git rev-list --max-parents=0 HEAD</c> (the
    /// parentless root commit(s) reachable from HEAD) into a list of SHAs,
    /// trimming blank lines and ignoring short/garbage tokens. A full clone has
    /// real roots; a shallow/grafted clone would falsely report its graft
    /// boundary as a root — this extractor assumes a full clone (see class docs).
    /// </summary>
    internal static List<string> ParseRootShas(string revListOutput)
    {
        List<string> roots = [];
        if (string.IsNullOrWhiteSpace(revListOutput)) return roots;

        foreach (string raw in revListOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string sha = raw.Trim();
            if (sha.Length >= 7) roots.Add(sha);
        }

        return roots;
    }

    /// <summary>
    /// Decides whether an extraction should re-walk full history to deepen an
    /// already-ingested repo backward. True only when the repo is configured
    /// uncapped (<paramref name="effectiveMaxInitialCommits"/> &lt;= 0), it
    /// already has commits (<paramref name="hasPriorHistory"/>), and its git
    /// root commit(s) are not yet all present
    /// (<paramref name="allRootsIngested"/> is false). Once the root(s) land,
    /// this returns false and the extractor reverts to cheap forward increments.
    /// </summary>
    internal static bool ShouldDeepen(int effectiveMaxInitialCommits, bool hasPriorHistory, bool allRootsIngested)
        => effectiveMaxInitialCommits <= 0 && hasPriorHistory && !allRootsIngested;

    /// <summary>
    /// Returns true when every parentless root commit reachable from HEAD is
    /// already stored in <c>github_commits</c> for <paramref name="repoFullName"/>.
    /// Runs <c>git rev-list --max-parents=0 HEAD</c> (sub-second) and checks each
    /// root against the DB; returns false on the first missing root. Returns true
    /// defensively when git reports no roots. Because the insert phase writes the
    /// oldest (root) commit last, a present root soundly implies the full history
    /// — and every commit's file rows — are present.
    /// </summary>
    private async Task<bool> AreAllRootsIngestedAsync(string clonePath, string repoFullName, CancellationToken ct)
    {
        string revListOutput = await RunGitAsync(clonePath, "rev-list --max-parents=0 HEAD", ct);
        List<string> roots = ParseRootShas(revListOutput);
        if (roots.Count == 0) return true;

        using SqliteConnection connection = database.OpenConnection();
        foreach (string root in roots)
        {
            ct.ThrowIfCancellationRequested();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM github_commits WHERE RepoFullName = @repo AND Sha = @sha LIMIT 1";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.Parameters.AddWithValue("@sha", root);
            if (cmd.ExecuteScalar() is null) return false;
        }

        return true;
    }

    /// <summary>
    /// Deletes every <c>github_commit_files</c> row whose <c>CommitSha</c> is in
    /// <paramref name="shas"/>, chunking the <c>IN (...)</c> list to stay under
    /// SQLite's bound-parameter limit. Called immediately before re-inserting the
    /// file rows for those commits so a retried/interrupted extraction cannot
    /// leave duplicated or orphaned file rows (the file index is non-unique). A
    /// no-op for brand-new SHAs, so the normal incremental path is unchanged.
    /// </summary>
    private static void DeleteCommitFilesForShas(SqliteConnection connection, List<string> shas)
    {
        if (shas.Count == 0) return;

        const int chunkSize = 500;
        for (int i = 0; i < shas.Count; i += chunkSize)
        {
            List<string> chunk = shas.GetRange(i, Math.Min(chunkSize, shas.Count - i));
            using SqliteCommand cmd = connection.CreateCommand();
            string[] paramNames = new string[chunk.Count];
            for (int j = 0; j < chunk.Count; j++)
            {
                paramNames[j] = $"@s{j}";
                cmd.Parameters.AddWithValue($"@s{j}", chunk[j]);
            }
            cmd.CommandText =
                $"DELETE FROM github_commit_files WHERE CommitSha IN ({string.Join(",", paramNames)})";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Partitions git stderr into benign "warning:" diagnostics vs. other output.
    /// Returns (Benign, Other), either of which is null when its bucket is empty.
    /// </summary>
    internal static (string? Benign, string? Other) ClassifyStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return (null, null);

        List<string> benign = [];
        List<string> other = [];
        foreach (string raw in stderr.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
                benign.Add(line);
            else
                other.Add(line);
        }

        return (
            benign.Count > 0 ? string.Join('\n', benign) : null,
            other.Count > 0 ? string.Join('\n', other) : null);
    }

    private async Task<string> RunGitAsync(string workingDir, string arguments, CancellationToken ct)
    {
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} failed with exit code {process.ExitCode}: {stderr}");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            (string? benign, string? other) = ClassifyStderr(stderr);
            if (benign is not null)
                logger.LogDebug("git stderr (benign): {StdErr}", benign);
            if (other is not null)
                logger.LogWarning("git stderr: {StdErr}", other);
        }

        return stdout;
    }
}
