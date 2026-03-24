using System.Diagnostics;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Extracts commit metadata and changed files from a local git clone
/// using git log --name-status.
/// </summary>
public class GitHubCommitFileExtractor(GitHubDatabase database, ILogger<GitHubCommitFileExtractor> logger)
{
    /// <summary>
    /// Extracts commits and their changed files from the local clone,
    /// storing them in the database. Processes commits newer than the last known SHA.
    /// </summary>
    public async Task ExtractAsync(string clonePath, string repoFullName, CancellationToken ct = default)
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

        string sinceArg = lastSha is not null ? $"{lastSha}..HEAD" : "HEAD~500..HEAD";
        string args = $"log {sinceArg} --name-status --format=%H%n%an%n%aI%n%s%n---END-HEADER---";

        string output = await RunGitAsync(clonePath, args, ct);
        if (string.IsNullOrWhiteSpace(output)) return;

        List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> commits = ParseGitLogOutput(output, repoFullName);
        using SqliteConnection connection = database.OpenConnection();

        int commitCount = 0, fileCount = 0;
        foreach ((GitHubCommitRecord? commit, List<GitHubCommitFileRecord>? files) in commits)
        {
            ct.ThrowIfCancellationRequested();

            GitHubCommitRecord? existing = GitHubCommitRecord.SelectSingle(connection, Sha: commit.Sha);
            if (existing is not null) continue;

            GitHubCommitRecord.Insert(connection, commit, ignoreDuplicates: true);
            commitCount++;

            foreach (GitHubCommitFileRecord file in files)
            {
                GitHubCommitFileRecord.Insert(connection, file, ignoreDuplicates: true);
                fileCount++;
            }
        }

        logger.LogInformation(
            "Extracted {Commits} commits and {Files} file changes from {Repo}",
            commitCount, fileCount, repoFullName);
    }

    internal static List<(GitHubCommitRecord Commit, List<GitHubCommitFileRecord> Files)> ParseGitLogOutput(
        string output, string repoFullName)
    {
        List<(GitHubCommitRecord, List<GitHubCommitFileRecord>)> results = new List<(GitHubCommitRecord, List<GitHubCommitFileRecord>)>();
        string[] lines = output.Split('\n', StringSplitOptions.None);
        int i = 0;

        while (i < lines.Length)
        {
            // Skip empty lines
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
            if (i >= lines.Length) break;

            // Read header: SHA, author, date, subject
            string sha = lines[i++].Trim();
            if (sha.Length < 7) continue;

            string author = i < lines.Length ? lines[i++].Trim() : "Unknown";
            string dateStr = i < lines.Length ? lines[i++].Trim() : "";
            string subject = i < lines.Length ? lines[i++].Trim() : "";

            // Skip to end of header marker
            while (i < lines.Length && lines[i].Trim() != "---END-HEADER---") i++;
            if (i < lines.Length) i++; // skip the marker

            DateTimeOffset date = DateTimeOffset.TryParse(dateStr, out DateTimeOffset d) ? d : DateTimeOffset.MinValue;

            GitHubCommitRecord commit = new GitHubCommitRecord
            {
                Id = GitHubCommitRecord.GetIndex(),
                Sha = sha,
                RepoFullName = repoFullName,
                Message = subject,
                Author = author,
                Date = date,
                Url = $"https://github.com/{repoFullName}/commit/{sha}",
            };

            // Read file changes until next empty line or end
            List<GitHubCommitFileRecord> files = new List<GitHubCommitFileRecord>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                string fileLine = lines[i].Trim();
                if (fileLine.Length >= 2 && (fileLine[0] is 'A' or 'M' or 'D' or 'R' or 'C'))
                {
                    string[] parts = fileLine.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        // R/C rows have 3 fields: changeType, oldPath, newPath — use newPath
                        bool isRenameOrCopy = parts[0].Trim().StartsWith('R') || parts[0].Trim().StartsWith('C');
                        string filePath = isRenameOrCopy && parts.Length >= 3
                            ? parts[2].Trim()
                            : parts[1].Trim();

                        files.Add(new GitHubCommitFileRecord
                        {
                            Id = GitHubCommitFileRecord.GetIndex(),
                            CommitSha = sha,
                            FilePath = filePath,
                            ChangeType = parts[0].Trim(),
                        });
                    }
                }
                i++;
            }

            results.Add((commit, files));
        }

        return results;
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
            logger.LogWarning("git stderr: {StdErr}", stderr);
        }

        return stdout;
    }
}
