using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Controllers;

[ApiController]
[Route("api/v1")]
public class CrossRefController(GitHubDatabase db) : ControllerBase
{
    [HttpGet("xref/{*key}")]
    public IActionResult GetCrossReferences([FromRoute] string key, [FromQuery] string? source, [FromQuery] string? direction)
    {
        using SqliteConnection connection = db.OpenConnection();
        string dir = direction?.ToLowerInvariant() ?? "both";
        string sourceType = source ?? SourceSystems.GitHub;
        List<SourceCrossReference> refs = [];

        if (string.Equals(sourceType, SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase) && dir is "outgoing" or "both")
        {
            foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: key))
            {
                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, r);
                refs.Add(new SourceCrossReference(
                    SourceSystems.GitHub, r.SourceId, SourceSystems.Jira, r.JiraKey,
                    "mentions", r.Context, r.ContentType, resolved?.Title, resolved?.Url));
            }

            foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: key))
            {
                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, r);
                refs.Add(new SourceCrossReference(
                    SourceSystems.GitHub, r.SourceId, SourceSystems.Zulip, r.TargetId,
                    "mentions", r.Context, r.ContentType, resolved?.Title, resolved?.Url));
            }

            foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: key))
            {
                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, r);
                refs.Add(new SourceCrossReference(
                    SourceSystems.GitHub, r.SourceId, SourceSystems.Confluence, r.TargetId,
                    "mentions", r.Context, r.ContentType, resolved?.Title, resolved?.Url));
            }

            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: key))
            {
                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, r);
                refs.Add(new SourceCrossReference(
                    SourceSystems.GitHub, r.SourceId, SourceSystems.Fhir, r.TargetId,
                    "mentions", r.Context, r.ContentType, resolved?.Title, resolved?.Url));
            }
        }

        if (string.Equals(sourceType, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase) && dir is "incoming" or "both")
        {
            foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, JiraKey: key))
            {
                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, r);
                refs.Add(new SourceCrossReference(
                    SourceSystems.GitHub, r.SourceId, SourceSystems.Jira, r.JiraKey,
                    "mentions", r.Context, r.ContentType, resolved?.Title, resolved?.Url));
            }
        }

        return Ok(new CrossReferenceResponse(sourceType, key, dir, refs));
    }
}