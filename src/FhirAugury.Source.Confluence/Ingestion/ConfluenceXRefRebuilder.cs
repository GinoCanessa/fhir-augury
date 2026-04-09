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

        int refCount = 0;

        // Scan pages
        List<ConfluencePageRecord> pages = ConfluencePageRecord.SelectList(connection);
        foreach (ConfluencePageRecord page in pages)
        {
            ct.ThrowIfCancellationRequested();
            string pageText = $"{page.Title} {page.BodyPlain}";
            refCount += ExtractAndInsertAll(connection, page.ConfluenceId, ContentTypes.Page, pageText);
        }

        // Scan comments (xrefs keyed to parent page ID, matching how Jira maps comment xrefs to the parent issue)
        List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection);
        foreach (ConfluenceCommentRecord comment in comments)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(comment.Body)) continue;
            refCount += ExtractAndInsertAll(connection, comment.ConfluencePageId, ContentTypes.Comment, comment.Body);
        }

        logger.LogInformation("Rebuilt cross-references: {RefCount} refs from {PageCount} pages and {CommentCount} comments",
            refCount, pages.Count, comments.Count);
    }

    private static int ExtractAndInsertAll(SqliteConnection connection, string sourceId, string contentType, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int count = 0;

        foreach (JiraXRefRecord r in JiraReferenceExtractor.GetReferences(contentType, sourceId, null, text))
        {
            r.Id = JiraXRefRecord.GetIndex();
            JiraXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        foreach (ZulipXRefRecord r in ZulipReferenceExtractor.GetReferences(contentType, sourceId, text))
        {
            r.Id = ZulipXRefRecord.GetIndex();
            ZulipXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        foreach (GitHubXRefRecord r in GitHubReferenceExtractor.GetReferences(contentType, sourceId, text))
        {
            r.Id = GitHubXRefRecord.GetIndex();
            GitHubXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        foreach (FhirElementXRefRecord r in FhirElementReferenceExtractor.GetReferences(contentType, sourceId, text))
        {
            r.Id = FhirElementXRefRecord.GetIndex();
            FhirElementXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        return count;
    }
}
