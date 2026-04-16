using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Standalone rebuilder for the confluence_page_links table.
/// Scans all pages' storage format for internal page links and rebuilds the index.
/// </summary>
public class ConfluenceLinkRebuilder(
    ConfluenceDatabase database,
    ILogger<ConfluenceLinkRebuilder> logger)
{
    public void RebuildAll(CancellationToken ct)
    {
        using SqliteConnection connection = database.OpenConnection();

        using (SqliteCommand deleteCmd = new SqliteCommand("DELETE FROM confluence_page_links", connection))
            deleteCmd.ExecuteNonQuery();

        List<ConfluencePageRecord> pages = ConfluencePageRecord.SelectList(connection);
        List<ConfluencePageLinkRecord> toInsert = [];

        foreach (ConfluencePageRecord page in pages)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(page.BodyStorage))
                continue;

            List<(string TargetPageId, string LinkType)> links =
                ConfluenceLinkExtractor.ExtractLinks(page.BodyStorage);

            foreach ((string targetPageId, string linkType) in links)
            {
                toInsert.Add(new ConfluencePageLinkRecord
                {
                    Id = ConfluencePageLinkRecord.GetIndex(),
                    SourcePageId = page.ConfluenceId,
                    TargetPageId = targetPageId,
                    LinkType = linkType,
                });
            }
        }

        ct.ThrowIfCancellationRequested();

        if (toInsert.Count > 0)
        {
            const int batchSize = 1000;
            for (int i = 0; i < toInsert.Count; i += batchSize)
            {
                List<ConfluencePageLinkRecord> batch = toInsert.GetRange(i, Math.Min(batchSize, toInsert.Count - i));
                batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
            }
        }

        logger.LogInformation("Rebuilt page links: {LinkCount} links from {PageCount} pages",
            toInsert.Count, pages.Count);
    }
}
