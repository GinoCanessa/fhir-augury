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
        int linkCount = 0;

        foreach (ConfluencePageRecord page in pages)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(page.BodyStorage))
                continue;

            List<(string TargetPageId, string LinkType)> links =
                ConfluenceLinkExtractor.ExtractLinks(page.BodyStorage);

            foreach ((string targetPageId, string linkType) in links)
            {
                ConfluencePageLinkRecord.Insert(connection, new ConfluencePageLinkRecord
                {
                    Id = ConfluencePageLinkRecord.GetIndex(),
                    SourcePageId = page.ConfluenceId,
                    TargetPageId = targetPageId,
                    LinkType = linkType,
                }, ignoreDuplicates: true);
                linkCount++;
            }
        }

        logger.LogInformation("Rebuilt page links: {LinkCount} links from {PageCount} pages",
            linkCount, pages.Count);
    }
}
