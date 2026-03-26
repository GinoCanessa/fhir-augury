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

        List<ConfluencePageRecord> pages = ConfluencePageRecord.SelectList(connection);
        int refCount = 0;

        foreach (ConfluencePageRecord page in pages)
        {
            ct.ThrowIfCancellationRequested();
            string pageText = $"{page.Title} {page.BodyPlain}";

            foreach (JiraXRefRecord r in JiraReferenceExtractor.GetReferences("page", page.ConfluenceId, null, pageText))
            {
                r.Id = JiraXRefRecord.GetIndex();
                JiraXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }

            foreach (ZulipXRefRecord r in ZulipReferenceExtractor.GetReferences("page", page.ConfluenceId, pageText))
            {
                r.Id = ZulipXRefRecord.GetIndex();
                ZulipXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }

            foreach (GitHubXRefRecord r in GitHubReferenceExtractor.GetReferences("page", page.ConfluenceId, pageText))
            {
                r.Id = GitHubXRefRecord.GetIndex();
                GitHubXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }

            foreach (FhirElementXRefRecord r in FhirElementReferenceExtractor.GetReferences("page", page.ConfluenceId, pageText))
            {
                r.Id = FhirElementXRefRecord.GetIndex();
                FhirElementXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }
        }

        logger.LogInformation("Rebuilt cross-references: {RefCount} refs from {PageCount} pages",
            refCount, pages.Count);
    }
}
