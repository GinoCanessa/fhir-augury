using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Text;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Scans GitHub content (issues, PRs, comments, commit messages) for
/// cross-references to Jira, Zulip, Confluence, and FHIR elements.
/// </summary>
public class GitHubXRefRebuilder(
    GitHubDatabase database,
    IOptions<GitHubServiceOptions> options,
    ILogger<GitHubXRefRebuilder> logger)
{
    private readonly GitHubServiceOptions _options = options.Value;

    /// <summary>
    /// Rebuilds cross-references for all repos: clears all xref tables once, then
    /// extracts from every repo. Use this when processing multiple repos to avoid
    /// the clear-per-repo wipe bug.
    /// </summary>
    public void RebuildAllRepos(IReadOnlyList<string> repoNames, HashSet<int>? validJiraNumbers = null, CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        ClearAllXRefTables(connection);

        int totalRefCount = 0;
        foreach (string repo in repoNames)
        {
            totalRefCount += ExtractRepoRefs(connection, repo, validJiraNumbers, ct);
        }

        logger.LogInformation("Extracted {Count} cross-references from {RepoCount} repos", totalRefCount, repoNames.Count);
    }

    /// <summary>
    /// Rebuilds cross-references for a single repo: clears all xref tables, then
    /// extracts from the specified repo only.
    /// </summary>
    public void RebuildAll(string repoFullName, HashSet<int>? validJiraNumbers = null, CancellationToken ct = default)
    {
        RebuildAllRepos([repoFullName], validJiraNumbers, ct);
    }

    private static void ClearAllXRefTables(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM xref_jira;
            DELETE FROM xref_zulip;
            DELETE FROM xref_confluence;
            DELETE FROM xref_fhir_element;
            """;
        cmd.ExecuteNonQuery();
    }

    private int ExtractRepoRefs(SqliteConnection connection, string repoFullName, HashSet<int>? validJiraNumbers, CancellationToken ct)
    {
        List<JiraXRefRecord> allJira = [];
        List<ZulipXRefRecord> allZulip = [];
        List<ConfluenceXRefRecord> allConfluence = [];
        List<FhirElementXRefRecord> allFhir = [];

        // Repo-scoped bare-number resolution applies to prose (commit/issue/comment)
        // content only — never to file contents (incidental integers).
        RepoJiraScope? scope = _options.ResolveJiraScope(repoFullName);

        void Collect(string text, string sourceType, string sourceId, RepoJiraScope? jiraScope)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            allJira.AddRange(ExtractJiraReferences(text, sourceType, sourceId, validJiraNumbers, jiraScope));
            allZulip.AddRange(ZulipReferenceExtractor.GetReferences(sourceType, sourceId, text));
            allConfluence.AddRange(ConfluenceReferenceExtractor.GetReferences(sourceType, sourceId, text));
            allFhir.AddRange(FhirElementReferenceExtractor.GetReferences(sourceType, sourceId, text));
        }

        // Scan issues (prose — scoped)
        List<GitHubIssueRecord> allIssues = GitHubIssueRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubIssueRecord issue in allIssues)
        {
            ct.ThrowIfCancellationRequested();
            Collect($"{issue.Title} {issue.Body}", ContentTypes.Issue, issue.UniqueKey, scope);
        }

        // Scan comments (prose — scoped)
        List<GitHubCommentRecord> allComments = GitHubCommentRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubCommentRecord comment in allComments)
        {
            ct.ThrowIfCancellationRequested();
            string sourceId = $"{comment.RepoFullName}#{comment.IssueNumber}:{comment.Id}";
            Collect(comment.Body, ContentTypes.Comment, sourceId, scope);
        }

        // Scan commits (prose — scoped). Scan the full message text (subject + body).
        List<GitHubCommitRecord> allCommits = GitHubCommitRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubCommitRecord commit in allCommits)
        {
            ct.ThrowIfCancellationRequested();
            string commitText = string.IsNullOrEmpty(commit.Body)
                ? commit.Message
                : $"{commit.Message}\n{commit.Body}";
            Collect(commitText, ContentTypes.Commit, commit.Sha, scope);
        }

        // Scan file contents (NOT scoped — incidental integers are not tickets)
        List<GitHubFileContentRecord> fileContents = GitHubFileContentRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubFileContentRecord file in fileContents)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(file.ContentText))
            {
                string sourceId = $"{file.RepoFullName}:{file.FilePath}";
                Collect(file.ContentText, ContentTypes.File, sourceId, jiraScope: null);
            }
        }

        int refCount = 0;
        refCount += BulkInsertXRefs(connection, allJira, static r => r.Id = JiraXRefRecord.GetIndex(),
            static (batch, c) => batch.Insert(c, ignoreDuplicates: true, insertPrimaryKey: true));
        refCount += BulkInsertXRefs(connection, allZulip, static r => r.Id = ZulipXRefRecord.GetIndex(),
            static (batch, c) => batch.Insert(c, ignoreDuplicates: true, insertPrimaryKey: true));
        refCount += BulkInsertXRefs(connection, allConfluence, static r => r.Id = ConfluenceXRefRecord.GetIndex(),
            static (batch, c) => batch.Insert(c, ignoreDuplicates: true, insertPrimaryKey: true));
        refCount += BulkInsertXRefs(connection, allFhir, static r => r.Id = FhirElementXRefRecord.GetIndex(),
            static (batch, c) => batch.Insert(c, ignoreDuplicates: true, insertPrimaryKey: true));

        logger.LogInformation("Extracted {Count} cross-references from {Repo}", refCount, repoFullName);
        return refCount;
    }

    private static int BulkInsertXRefs<T>(
        SqliteConnection connection,
        List<T> records,
        Action<T> assignId,
        Action<List<T>, SqliteConnection> insertBatch)
    {
        if (records.Count == 0) return 0;

        foreach (T r in records) assignId(r);

        const int batchSize = 1000;
        for (int i = 0; i < records.Count; i += batchSize)
        {
            List<T> batch = records.GetRange(i, Math.Min(batchSize, records.Count - i));
            insertBatch(batch, connection);
        }
        return records.Count;
    }

    /// <summary>
    /// Extracts Jira references using the shared extractor (FHIR-N, JF-N, GF-N, J#N, GF#N, Jira URLs).
    /// When <paramref name="repoScope"/> is supplied, also resolves repo-scoped bare integers.
    /// </summary>
    internal static List<JiraXRefRecord> ExtractJiraReferences(
        string text,
        string sourceType,
        string sourceId,
        HashSet<int>? validJiraNumbers,
        RepoJiraScope? repoScope = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Shared extractor for standard patterns (FHIR-N, JF-N, GF-N, J#N, GF#N, Jira URLs)
        CrossRefExtractionContext? context = validJiraNumbers is not null || repoScope is not null
            ? new CrossRefExtractionContext(ValidJiraNumbers: validJiraNumbers, RepoScope: repoScope)
            : null;
        return JiraReferenceExtractor.GetReferences(sourceType, sourceId, context, text);
    }
}
