using System.Diagnostics;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Extracts commit metadata and changed files from a local git clone
/// using a two-pass git log strategy: --name-status for change types,
/// --numstat for per-file line counts, merged by SHA.
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

        for (int i = 0; i < newCommits.Count; i += batchSize)
        {
            List<GitHubCommitRecord> batch =
                newCommits.GetRange(i, Math.Min(batchSize, newCommits.Count - i));
            batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        for (int i = 0; i < newFiles.Count; i += batchSize)
        {
            List<GitHubCommitFileRecord> batch =
                newFiles.GetRange(i, Math.Min(batchSize, newFiles.Count - i));
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
    /// When a previous SHA exists, uses "{sha}..HEAD" for incremental extraction.
    /// Otherwise uses "HEAD" with a -n limit to cap initial extraction,
    /// avoiding the HEAD~N crash when a repo has fewer than N commits.
    /// A non-positive <paramref name="maxInitialCommits"/> removes the cap
    /// (full-history initial extraction).
    /// </summary>
    internal static (string SinceArg, string LimitArg) BuildLogRange(string? lastSha, int maxInitialCommits = 500)
    {
        if (lastSha is not null)
            return ($"{lastSha}..HEAD", "");
        return maxInitialCommits > 0 ? ("HEAD", $" -n {maxInitialCommits}") : ("HEAD", "");
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
