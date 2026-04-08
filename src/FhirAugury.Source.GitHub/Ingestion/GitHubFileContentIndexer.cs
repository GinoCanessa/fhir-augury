using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Walks a cloned repository file tree, classifies files, parses content, and stores
/// extracted text in the github_file_contents table for FTS5 and BM25 indexing.
/// </summary>
public class GitHubFileContentIndexer(
    GitHubDatabase database,
    IOptions<GitHubServiceOptions> optionsAccessor,
    ILogger<GitHubFileContentIndexer> logger)
{
    private readonly FileContentIndexingOptions _config = optionsAccessor.Value.FileContentIndexing;

    private readonly XmlFileContentParser _xmlParser = new();
    private readonly JsonFileContentParser _jsonParser = new();
    private readonly MarkdownFileContentParser _markdownParser = new();
    private readonly PlainTextFileContentParser _textParser = new("text");
    private readonly PlainTextFileContentParser _codeParser = new("code");
    private readonly FallbackFileContentParser _fallbackParser = new();

    /// <summary>Result of a file content indexing run.</summary>
    public record FileIndexingResult(int Indexed, int SkippedByType, int SkippedByPattern, int SkippedBySize, int Failed);

    /// <summary>
    /// Performs a full index of all files in the given clone directory.
    /// </summary>
    public FileIndexingResult IndexRepositoryFiles(
        string repoFullName, string clonePath, CancellationToken ct = default,
        List<string>? priorityPaths = null,
        List<string>? additionalIgnorePatterns = null)
    {
        if (!_config.Enabled)
        {
            logger.LogDebug("File content indexing is disabled");
            return new FileIndexingResult(0, 0, 0, 0, 0);
        }

        IgnorePatternMatcher ignoreMatcher = BuildIgnoreMatcher(clonePath, additionalIgnorePatterns);
        HashSet<string> additionalSkipExts = _config.AdditionalSkipExtensions.Count > 0
            ? new HashSet<string>(_config.AdditionalSkipExtensions, StringComparer.OrdinalIgnoreCase)
            : null!;
        HashSet<string> additionalSkipDirs = _config.AdditionalSkipDirectories.Count > 0
            ? new HashSet<string>(_config.AdditionalSkipDirectories, StringComparer.OrdinalIgnoreCase)
            : null!;

        // Merge priority paths with global IncludeOnlyPaths
        List<string> effectiveIncludeOnlyPaths = priorityPaths is not null
            ? [.. _config.IncludeOnlyPaths, .. priorityPaths]
            : _config.IncludeOnlyPaths;

        int indexed = 0, skippedByType = 0, skippedByPattern = 0, skippedBySize = 0, failed = 0;
        List<GitHubFileContentRecord> batch = new(500);

        foreach (string filePath in EnumerateFiles(clonePath, additionalSkipDirs))
        {
            ct.ThrowIfCancellationRequested();

            if (indexed + batch.Count >= _config.MaxFilesPerRepo)
            {
                logger.LogWarning("Reached max files per repo limit ({Max}) for {Repo}",
                    _config.MaxFilesPerRepo, repoFullName);
                break;
            }

            string relativePath = Path.GetRelativePath(clonePath, filePath).Replace('\\', '/');

            // Check ignore patterns
            if (ignoreMatcher.IsExcluded(relativePath))
            {
                skippedByPattern++;
                continue;
            }

            // Check include-only paths
            if (effectiveIncludeOnlyPaths.Count > 0)
            {
                bool included = false;
                foreach (string includePath in effectiveIncludeOnlyPaths)
                {
                    string normalizedInclude = includePath.Replace('\\', '/').TrimEnd('/');
                    if (relativePath.StartsWith(normalizedInclude + "/", StringComparison.OrdinalIgnoreCase) ||
                        relativePath.Equals(normalizedInclude, StringComparison.OrdinalIgnoreCase))
                    {
                        included = true;
                        break;
                    }
                }
                if (!included)
                {
                    skippedByPattern++;
                    continue;
                }
            }

            // Classify
            FileTypeClassifier.FileAction action = FileTypeClassifier.Classify(relativePath, additionalSkipExts);
            if (action == FileTypeClassifier.FileAction.Skip)
            {
                skippedByType++;
                continue;
            }

            // Check file size
            FileInfo fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length > _config.MaxFileSizeBytes)
            {
                skippedBySize++;
                continue;
            }

            // Parse content
            try
            {
                (IFileContentParser parser, string parserType) = GetParser(action);
                string? contentText = null;

                using (FileStream fs = fi.OpenRead())
                {
                    contentText = parser.ExtractText(filePath, fs, _config.MaxExtractedTextLength);
                }

                batch.Add(new GitHubFileContentRecord
                {
                    Id = GitHubFileContentRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    FilePath = relativePath,
                    FileExtension = Path.GetExtension(relativePath).ToLowerInvariant(),
                    ParserType = parserType,
                    ContentText = contentText,
                    ContentLength = (int)fi.Length,
                    ExtractedLength = contentText?.Length ?? 0,
                });

                if (batch.Count >= 500)
                {
                    FlushBatch(batch);
                    indexed += batch.Count;
                    batch.Clear();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to parse {FilePath} in {Repo}", relativePath, repoFullName);
                failed++;
            }
        }

        if (batch.Count > 0)
        {
            FlushBatch(batch);
            indexed += batch.Count;
            batch.Clear();
        }

        logger.LogInformation(
            "File content indexing for {Repo}: {Indexed} indexed, {SkippedType} skipped by type, " +
            "{SkippedPattern} skipped by pattern, {SkippedSize} skipped by size, {Failed} failed",
            repoFullName, indexed, skippedByType, skippedByPattern, skippedBySize, failed);

        return new FileIndexingResult(indexed, skippedByType, skippedByPattern, skippedBySize, failed);
    }

    /// <summary>
    /// Incrementally re-indexes only the specified changed files. Deletes removed files from the index.
    /// </summary>
    public void IncrementalUpdate(string repoFullName, string clonePath, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
    {
        if (!_config.Enabled || changedFiles.Count == 0)
            return;

        IgnorePatternMatcher ignoreMatcher = BuildIgnoreMatcher(clonePath);
        HashSet<string> additionalSkipExts = _config.AdditionalSkipExtensions.Count > 0
            ? new HashSet<string>(_config.AdditionalSkipExtensions, StringComparer.OrdinalIgnoreCase)
            : null!;

        using SqliteConnection connection = database.OpenConnection();
        int updated = 0, removed = 0;

        foreach (string relativePath in changedFiles)
        {
            ct.ThrowIfCancellationRequested();

            string normalizedPath = relativePath.Replace('\\', '/');
            string fullPath = Path.Combine(clonePath, normalizedPath.Replace('/', Path.DirectorySeparatorChar));

            // If file no longer exists or is now excluded, remove from index
            if (!File.Exists(fullPath) || ignoreMatcher.IsExcluded(normalizedPath))
            {
                DeleteFileRecord(connection, repoFullName, normalizedPath);
                removed++;
                continue;
            }

            FileTypeClassifier.FileAction action = FileTypeClassifier.Classify(normalizedPath, additionalSkipExts);
            if (action == FileTypeClassifier.FileAction.Skip)
            {
                DeleteFileRecord(connection, repoFullName, normalizedPath);
                removed++;
                continue;
            }

            FileInfo fi = new FileInfo(fullPath);
            if (!fi.Exists || fi.Length > _config.MaxFileSizeBytes)
            {
                DeleteFileRecord(connection, repoFullName, normalizedPath);
                removed++;
                continue;
            }

            try
            {
                (IFileContentParser parser, string parserType) = GetParser(action);
                string? contentText;

                using (FileStream fs = fi.OpenRead())
                {
                    contentText = parser.ExtractText(fullPath, fs, _config.MaxExtractedTextLength);
                }

                GitHubFileContentRecord record = new GitHubFileContentRecord
                {
                    Id = GitHubFileContentRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    FilePath = normalizedPath,
                    FileExtension = Path.GetExtension(normalizedPath).ToLowerInvariant(),
                    ParserType = parserType,
                    ContentText = contentText,
                    ContentLength = (int)fi.Length,
                    ExtractedLength = contentText?.Length ?? 0,
                };

                UpsertFileRecord(connection, record);
                updated++;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to re-index {FilePath} in {Repo}", normalizedPath, repoFullName);
            }
        }

        logger.LogInformation("Incremental file indexing for {Repo}: {Updated} updated, {Removed} removed",
            repoFullName, updated, removed);
    }

    private IgnorePatternMatcher BuildIgnoreMatcher(string clonePath, List<string>? additionalIgnorePatterns = null)
    {
        List<string> allPatterns = additionalIgnorePatterns is not null
            ? [.. _config.IgnorePatterns, .. additionalIgnorePatterns]
            : _config.IgnorePatterns;

        string repoIgnoreFile = Path.Combine(clonePath, ".augury-index-ignore");
        return new IgnorePatternMatcher(
            allPatterns,
            File.Exists(repoIgnoreFile) ? repoIgnoreFile : null);
    }

    private (IFileContentParser Parser, string ParserType) GetParser(FileTypeClassifier.FileAction action)
    {
        return action switch
        {
            FileTypeClassifier.FileAction.ParseXml => (_xmlParser, "xml"),
            FileTypeClassifier.FileAction.ParseJson => (_jsonParser, "json"),
            FileTypeClassifier.FileAction.ParseMarkdown => (_markdownParser, "markdown"),
            FileTypeClassifier.FileAction.ParseText => (_textParser, "text"),
            FileTypeClassifier.FileAction.ParseCode => (_codeParser, "code"),
            FileTypeClassifier.FileAction.ParseFallback => (_fallbackParser, "text"),
            _ => (_fallbackParser, "text"),
        };
    }

    private IEnumerable<string> EnumerateFiles(string rootPath, IReadOnlySet<string>? additionalSkipDirs)
    {
        Stack<string> dirs = new Stack<string>();
        dirs.Push(rootPath);

        while (dirs.Count > 0)
        {
            string currentDir = dirs.Pop();
            string dirName = Path.GetFileName(currentDir);

            // Skip known directories (except root)
            if (currentDir != rootPath && FileTypeClassifier.IsSkippedDirectory(dirName, additionalSkipDirs))
                continue;

            string[] files;
            try { files = Directory.GetFiles(currentDir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (string file in files)
                yield return file;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(currentDir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (string subdir in subdirs)
                dirs.Push(subdir);
        }
    }

    private void FlushBatch(List<GitHubFileContentRecord> batch)
    {
        using SqliteConnection connection = database.OpenConnection();
        batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
    }

    private static void DeleteFileRecord(SqliteConnection connection, string repoFullName, string filePath)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM github_file_contents WHERE RepoFullName = @repo AND FilePath = @path";
        cmd.Parameters.AddWithValue("@repo", repoFullName);
        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertFileRecord(SqliteConnection connection, GitHubFileContentRecord record)
    {
        // Delete existing and insert new
        using SqliteCommand deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM github_file_contents WHERE RepoFullName = @repo AND FilePath = @path";
        deleteCmd.Parameters.AddWithValue("@repo", record.RepoFullName);
        deleteCmd.Parameters.AddWithValue("@path", record.FilePath);
        deleteCmd.ExecuteNonQuery();

        GitHubFileContentRecord.Insert(connection, record, ignoreDuplicates: true);
    }
}
