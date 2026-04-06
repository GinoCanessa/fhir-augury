using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Controllers;

[ApiController]
[Route("api/v1")]
public class CrossRefController(ConfluenceDatabase db, IOptions<ConfluenceServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("xref/{id}")]
    public IActionResult GetCrossReferences([FromRoute] string id, [FromQuery] string? source, [FromQuery] string? direction)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        List<SourceCrossReference> refs = [];
        string dir = direction?.ToLowerInvariant() ?? "both";
        string src = source ?? SourceSystems.Confluence;

        if (string.Equals(src, SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase) && dir is "outgoing" or "both")
        {
            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: id);
            string sourceTitle = page?.Title ?? "";
            string sourceUrl = page?.Url ?? "";

            foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: id))
            {
                refs.Add(new SourceCrossReference(
                    SourceSystems.Confluence, id,
                    SourceSystems.Jira, r.JiraKey,
                    "mentions", r.Context,
                    r.ContentType, sourceTitle, sourceUrl));
            }

            foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: id))
            {
                refs.Add(new SourceCrossReference(
                    SourceSystems.Confluence, id,
                    SourceSystems.Zulip, r.TargetId,
                    "mentions", r.Context,
                    r.ContentType, sourceTitle, sourceUrl));
            }

            foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: id))
            {
                refs.Add(new SourceCrossReference(
                    SourceSystems.Confluence, id,
                    SourceSystems.GitHub, r.TargetId,
                    "mentions", r.Context,
                    r.ContentType, sourceTitle, sourceUrl));
            }

            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: id))
            {
                refs.Add(new SourceCrossReference(
                    SourceSystems.Confluence, id,
                    SourceSystems.Fhir, r.TargetId,
                    "mentions", r.Context,
                    r.ContentType, sourceTitle, sourceUrl));
            }
        }

        if (string.Equals(src, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase) && dir is "incoming" or "both")
        {
            List<JiraXRefRecord> jiraRefs = JiraXRefRecord.SelectList(connection, JiraKey: id);
            HashSet<string> seen = [];
            foreach (JiraXRefRecord jiraRef in jiraRefs)
            {
                if (!seen.Add(jiraRef.SourceId)) continue;
                ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: jiraRef.SourceId);
                if (page is null) continue;

                refs.Add(new SourceCrossReference(
                    SourceSystems.Confluence, jiraRef.SourceId,
                    SourceSystems.Jira, id,
                    "mentions", jiraRef.Context,
                    jiraRef.ContentType, page.Title, page.Url));
            }
        }

        return Ok(new CrossReferenceResponse(src, id, dir, refs));
    }
}