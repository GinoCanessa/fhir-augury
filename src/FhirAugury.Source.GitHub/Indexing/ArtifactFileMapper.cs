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
        List<string> paths = new List<string>();

        if (!string.IsNullOrEmpty(artifactKey))
        {
            List<GitHubSpecFileMapRecord> maps = GitHubSpecFileMapRecord.SelectList(connection, RepoFullName: repoFullName, ArtifactKey: artifactKey);
            paths.AddRange(maps.Select(m => m.FilePath));
        }

        if (!string.IsNullOrEmpty(artifactId))
        {
            using SqliteCommand cmd = new SqliteCommand(
                "SELECT FilePath FROM github_spec_file_map WHERE RepoFullName = @repo AND ArtifactKey LIKE @pattern",
                connection);
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.Parameters.AddWithValue("@pattern", $"%{artifactId}%");
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                paths.Add(reader.GetString(0));
        }

        if (!string.IsNullOrEmpty(pageKey))
        {
            using SqliteCommand cmd = new SqliteCommand(
                "SELECT FilePath FROM github_spec_file_map WHERE RepoFullName = @repo AND MapType = 'page' AND ArtifactKey = @key",
                connection);
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.Parameters.AddWithValue("@key", pageKey);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                paths.Add(reader.GetString(0));
        }

        if (!string.IsNullOrEmpty(elementPath))
        {
            // Precise resolution via github_sd_elements
            using SqliteCommand cmd = new SqliteCommand(
                @"SELECT DISTINCT sd.FilePath
                  FROM github_sd_elements e
                  JOIN github_structure_definitions sd ON e.StructureDefinitionId = sd.Id
                  WHERE sd.RepoFullName = @repo AND e.Path = @path",
                connection);
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.Parameters.AddWithValue("@path", elementPath);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                paths.Add(reader.GetString(0));

            // Fallback to LIKE-based search if no precise match
            if (paths.Count == 0)
            {
                using SqliteCommand fallbackCmd = new SqliteCommand(
                    "SELECT FilePath FROM github_spec_file_map WHERE RepoFullName = @repo AND FilePath LIKE @pattern",
                    connection);
                fallbackCmd.Parameters.AddWithValue("@repo", repoFullName);
                fallbackCmd.Parameters.AddWithValue("@pattern", $"%{elementPath}%");
                using SqliteDataReader fallbackReader = fallbackCmd.ExecuteReader();
                while (fallbackReader.Read())
                    paths.Add(fallbackReader.GetString(0));
            }
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

        using SqliteConnection connection = database.OpenConnection();

        // Clear existing mappings for this repo
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM github_spec_file_map WHERE RepoFullName = @repo";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.ExecuteNonQuery();
        }

        int mapCount = 0;

        // Map directories under source/ (core FHIR repo convention)
        string sourceDir = Path.Combine(clonePath, "source");
        if (Directory.Exists(sourceDir))
        {
            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                ct.ThrowIfCancellationRequested();
                string dirName = Path.GetFileName(dir);
                string relativePath = Path.GetRelativePath(clonePath, dir).Replace('\\', '/');

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
            foreach (string file in Directory.GetFiles(sourceDir, "*.xml", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string fileName = Path.GetFileNameWithoutExtension(file);
                string relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');

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
        string inputDir = Path.Combine(clonePath, "input");
        if (Directory.Exists(inputDir))
        {
            foreach (string dir in Directory.GetDirectories(inputDir, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                string dirName = Path.GetFileName(dir);
                string relativePath = Path.GetRelativePath(clonePath, dir).Replace('\\', '/');

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
