using FhirAugury.Common.Text;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Standalone rebuilder for the confluence_jira_refs cross-reference table.
/// Scans all pages for Jira ticket patterns and rebuilds the index from scratch.
/// </summary>
public class ConfluenceJiraRefRebuilder(
    ConfluenceDatabase database,
    ILogger<ConfluenceJiraRefRebuilder> logger)
{
    public void RebuildAll(CancellationToken ct)
    {
        using SqliteConnection connection = database.OpenConnection();

        using (SqliteCommand deleteCmd = new SqliteCommand("DELETE FROM confluence_jira_refs", connection))
            deleteCmd.ExecuteNonQuery();

        List<ConfluencePageRecord> pages = ConfluencePageRecord.SelectList(connection);
        int refCount = 0;

        foreach (ConfluencePageRecord page in pages)
        {
            ct.ThrowIfCancellationRequested();
            string pageText = $"{page.Title} {page.BodyPlain}";
            List<JiraTicketMatch> tickets = JiraTicketExtractor.ExtractTickets(pageText);

            foreach (JiraTicketMatch ticket in tickets)
            {
                ConfluenceJiraRefRecord.Insert(connection, new ConfluenceJiraRefRecord
                {
                    Id = ConfluenceJiraRefRecord.GetIndex(),
                    ConfluenceId = page.ConfluenceId,
                    JiraKey = ticket.JiraKey,
                    Context = ticket.Context,
                }, ignoreDuplicates: true);
                refCount++;
            }
        }

        logger.LogInformation("Rebuilt Jira cross-references: {RefCount} refs from {PageCount} pages",
            refCount, pages.Count);
    }
}
