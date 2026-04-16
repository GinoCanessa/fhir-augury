using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Rebuilds all xref_* cross-reference tables by scanning all Confluence pages
/// and running shared extractors. Replaces the old ConfluenceJiraRefRebuilder.
/// </summary>
public class ConfluenceXRefRebuilder(
    ConfluenceDatabase database,
    ILogger<ConfluenceXRefRebuilder> logger)
{
    public void RebuildAll(CancellationToken ct)
    {
        using SqliteConnection connection = database.OpenConnection();

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM xref_jira;
                DELETE FROM xref_zulip;
                DELETE FROM xref_github;
                DELETE FROM xref_fhir_element;
                """;
            cmd.ExecuteNonQuery();
        }

        List<JiraXRefRecord> jiraRefs = [];
        List<ZulipXRefRecord> zulipRefs = [];
        List<GitHubXRefRecord> githubRefs = [];
        List<FhirElementXRefRecord> fhirRefs = [];

        // Scan pages
        List<ConfluencePageRecord> pages = ConfluencePageRecord.SelectList(connection);
        foreach (ConfluencePageRecord page in pages)
        {
            ct.ThrowIfCancellationRequested();
            string pageText = $"{page.Title} {page.BodyPlain}";
            ExtractAll(page.ConfluenceId, ContentTypes.Page, pageText, jiraRefs, zulipRefs, githubRefs, fhirRefs);
        }

        // Scan comments (xrefs keyed to parent page ID, matching how Jira maps comment xrefs to the parent issue)
        List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection);
        foreach (ConfluenceCommentRecord comment in comments)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(comment.Body)) continue;
            ExtractAll(comment.ConfluencePageId, ContentTypes.Comment, comment.Body, jiraRefs, zulipRefs, githubRefs, fhirRefs);
        }

        ct.ThrowIfCancellationRequested();

        BatchInsert(connection, jiraRefs, (c, b) => b.Insert(c, ignoreDuplicates: true, insertPrimaryKey: true));
        BatchInsert(connection, zulipRefs, (c, b) => b.Insert(c, ignoreDuplicates: true, insertPrimaryKey: true));
        BatchInsert(connection, githubRefs, (c, b) => b.Insert(c, ignoreDuplicates: true, insertPrimaryKey: true));
        BatchInsert(connection, fhirRefs, (c, b) => b.Insert(c, ignoreDuplicates: true, insertPrimaryKey: true));

        int refCount = jiraRefs.Count + zulipRefs.Count + githubRefs.Count + fhirRefs.Count;

        logger.LogInformation("Rebuilt cross-references: {RefCount} refs from {PageCount} pages and {CommentCount} comments",
            refCount, pages.Count, comments.Count);
    }

    private static void ExtractAll(
        string sourceId,
        string contentType,
        string text,
        List<JiraXRefRecord> jiraRefs,
        List<ZulipXRefRecord> zulipRefs,
        List<GitHubXRefRecord> githubRefs,
        List<FhirElementXRefRecord> fhirRefs)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (JiraXRefRecord r in JiraReferenceExtractor.GetReferences(contentType, sourceId, null, text))
        {
            r.Id = JiraXRefRecord.GetIndex();
            jiraRefs.Add(r);
        }

        foreach (ZulipXRefRecord r in ZulipReferenceExtractor.GetReferences(contentType, sourceId, text))
        {
            r.Id = ZulipXRefRecord.GetIndex();
            zulipRefs.Add(r);
        }

        foreach (GitHubXRefRecord r in GitHubReferenceExtractor.GetReferences(contentType, sourceId, text))
        {
            r.Id = GitHubXRefRecord.GetIndex();
            githubRefs.Add(r);
        }

        foreach (FhirElementXRefRecord r in FhirElementReferenceExtractor.GetReferences(contentType, sourceId, text))
        {
            r.Id = FhirElementXRefRecord.GetIndex();
            fhirRefs.Add(r);
        }
    }

    private static void BatchInsert<T>(SqliteConnection connection, List<T> records, Action<SqliteConnection, List<T>> insertBatch)
    {
        if (records.Count == 0) return;
        const int batchSize = 1000;
        for (int i = 0; i < records.Count; i += batchSize)
        {
            List<T> batch = records.GetRange(i, Math.Min(batchSize, records.Count - i));
            insertBatch(connection, batch);
        }
    }
}
