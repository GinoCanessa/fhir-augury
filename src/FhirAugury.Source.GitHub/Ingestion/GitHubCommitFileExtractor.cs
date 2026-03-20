using System.Diagnostics;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
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
        using (var conn = database.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Sha FROM github_commits WHERE RepoFullName = @repo ORDER BY Date DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            lastSha = cmd.ExecuteScalar()?.ToString();
        }

        var sinceArg = lastSha is not null ? $"{lastSha}..HEAD" : "HEAD~500..HEAD";
        var args = $"log {sinceArg} --name-status --format=%H%n%an%n%aI%n%s%n---END-HEADER---";

        var output = await RunGitAsync(clonePath, args, ct);
        if (string.IsNullOrWhiteSpace(output)) return;

        var commits = ParseGitLogOutput(output, repoFullName);
        using var connection = database.OpenConnection();

        int commitCount = 0, fileCount = 0;
        foreach (var (commit, files) in commits)
        {
            ct.ThrowIfCancellationRequested();

            var existing = GitHubCommitRecord.SelectSingle(connection, Sha: commit.Sha);
            if (existing is not null) continue;

            GitHubCommitRecord.Insert(connection, commit, ignoreDuplicates: true);
            commitCount++;

            foreach (var file in files)
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
        var results = new List<(GitHubCommitRecord, List<GitHubCommitFileRecord>)>();
        var lines = output.Split('\n', StringSplitOptions.None);
        int i = 0;

        while (i < lines.Length)
        {
            // Skip empty lines
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
            if (i >= lines.Length) break;

            // Read header: SHA, author, date, subject
            var sha = lines[i++].Trim();
            if (sha.Length < 7) continue;

            var author = i < lines.Length ? lines[i++].Trim() : "Unknown";
            var dateStr = i < lines.Length ? lines[i++].Trim() : "";
            var subject = i < lines.Length ? lines[i++].Trim() : "";

            // Skip to end of header marker
            while (i < lines.Length && lines[i].Trim() != "---END-HEADER---") i++;
            if (i < lines.Length) i++; // skip the marker

            var date = DateTimeOffset.TryParse(dateStr, out var d) ? d : DateTimeOffset.MinValue;

            var commit = new GitHubCommitRecord
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
            var files = new List<GitHubCommitFileRecord>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                var fileLine = lines[i].Trim();
                if (fileLine.Length >= 2 && (fileLine[0] is 'A' or 'M' or 'D' or 'R' or 'C'))
                {
                    var parts = fileLine.Split('\t', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        files.Add(new GitHubCommitFileRecord
                        {
                            Id = GitHubCommitFileRecord.GetIndex(),
                            CommitSha = sha,
                            FilePath = parts[1].Trim(),
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

    private static async Task<string> RunGitAsync(string workingDir, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return stdout;
    }
}
