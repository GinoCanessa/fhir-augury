using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Indexing;

/// <summary>
/// Maps FHIR artifacts to file paths in tracked repositories.
/// Uses JIRA-Spec-Artifacts data reconciled against the repository's file tree.
/// </summary>
public class ArtifactFileMapper(GitHubDatabase database, ILogger<ArtifactFileMapper> logger)
{
    /// <summary>
    /// Resolves a query parameter (artifact_key, artifact_id, page_key, or element_path)
    /// to a set of file paths via the github_spec_file_map table.
    /// </summary>
    public List<string> ResolveFilePaths(
        SqliteConnection connection,
        string repoFullName,
        string? artifactKey = null,
        string? artifactId = null,
        string? pageKey = null,
        string? elementPath = null)
    {
        var paths = new List<string>();

        if (!string.IsNullOrEmpty(artifactKey))
        {
            var maps = GitHubSpecFileMapRecord.SelectList(connection, RepoFullName: repoFullName, ArtifactKey: artifactKey);
            paths.AddRange(maps.Select(m => m.FilePath));
        }

        if (!string.IsNullOrEmpty(artifactId))
        {
            using var cmd = new SqliteCommand(
                "SELECT FilePath FROM github_spec_file_map WHERE RepoFullName = @repo AND ArtifactKey LIKE @pattern",
                connection);
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.Parameters.AddWithValue("@pattern", $"%{artifactId}%");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                paths.Add(reader.GetString(0));
        }

        if (!string.IsNullOrEmpty(pageKey))
        {
            using var cmd = new SqliteCommand(
                "SELECT FilePath FROM github_spec_file_map WHERE RepoFullName = @repo AND MapType = 'page' AND ArtifactKey = @key",
                connection);
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.Parameters.AddWithValue("@key", pageKey);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                paths.Add(reader.GetString(0));
        }

        if (!string.IsNullOrEmpty(elementPath))
        {
            using var cmd = new SqliteCommand(
                "SELECT FilePath FROM github_spec_file_map WHERE RepoFullName = @repo AND FilePath LIKE @pattern",
                connection);
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.Parameters.AddWithValue("@pattern", $"%{elementPath}%");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                paths.Add(reader.GetString(0));
        }

        return paths.Distinct().ToList();
    }

    /// <summary>
    /// Builds or refreshes the artifact-to-file mapping for a repository
    /// by matching artifact keys against source/input directory structures.
    /// </summary>
    public void BuildMappings(string repoFullName, string clonePath, CancellationToken ct = default)
    {
        if (!Directory.Exists(clonePath))
        {
            logger.LogWarning("Clone path does not exist: {Path}", clonePath);
            return;
        }

        using var connection = database.OpenConnection();

        // Clear existing mappings for this repo
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM github_spec_file_map WHERE RepoFullName = @repo";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.ExecuteNonQuery();
        }

        int mapCount = 0;

        // Map directories under source/ (core FHIR repo convention)
        var sourceDir = Path.Combine(clonePath, "source");
        if (Directory.Exists(sourceDir))
        {
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                ct.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(dir);
                var relativePath = Path.GetRelativePath(clonePath, dir).Replace('\\', '/');

                GitHubSpecFileMapRecord.Insert(connection, new GitHubSpecFileMapRecord
                {
                    Id = GitHubSpecFileMapRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    ArtifactKey = dirName,
                    FilePath = relativePath,
                    MapType = "directory",
                }, ignoreDuplicates: true);
                mapCount++;
            }

            // Map specific resource definition files
            foreach (var file in Directory.GetFiles(sourceDir, "*.xml", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileNameWithoutExtension(file);
                var relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');

                GitHubSpecFileMapRecord.Insert(connection, new GitHubSpecFileMapRecord
                {
                    Id = GitHubSpecFileMapRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    ArtifactKey = fileName,
                    FilePath = relativePath,
                    MapType = "file",
                }, ignoreDuplicates: true);
                mapCount++;
            }
        }

        // Map directories under input/ (IG Publisher convention)
        var inputDir = Path.Combine(clonePath, "input");
        if (Directory.Exists(inputDir))
        {
            foreach (var dir in Directory.GetDirectories(inputDir, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(dir);
                var relativePath = Path.GetRelativePath(clonePath, dir).Replace('\\', '/');

                GitHubSpecFileMapRecord.Insert(connection, new GitHubSpecFileMapRecord
                {
                    Id = GitHubSpecFileMapRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    ArtifactKey = dirName,
                    FilePath = relativePath,
                    MapType = "directory",
                }, ignoreDuplicates: true);
                mapCount++;
            }
        }

        logger.LogInformation("Built {Count} artifact-file mappings for {Repo}", mapCount, repoFullName);
    }
}
