using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Controllers;

[ApiController]
[Route("api/v1")]
public class CrossRefController(ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) : ControllerBase
{
    [HttpGet("xref/{id}")]
    public IActionResult GetCrossReferences([FromRoute] string id, [FromQuery] string? source, [FromQuery] string? direction)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        List<SourceCrossReference> refs = [];
        string dir = direction?.ToLowerInvariant() ?? "both";
        string src = source ?? SourceSystems.Zulip;

        if (string.Equals(src, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase) && dir is "outgoing" or "both")
        {
            if (int.TryParse(id, out int msgId))
            {
                ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
                string sourceTitle = message is not null ? $"{message.StreamName} > {message.Topic}" : "";
                string sourceUrl = message is not null ? ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, msgId) : "";

                foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: id))
                {
                    refs.Add(new SourceCrossReference(
                        SourceSystems.Zulip, id,
                        SourceSystems.Jira, r.JiraKey,
                        "mentions", r.Context,
                        r.ContentType, sourceTitle, sourceUrl));
                }

                foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: id))
                {
                    refs.Add(new SourceCrossReference(
                        SourceSystems.Zulip, id,
                        SourceSystems.GitHub, r.TargetId,
                        "mentions", r.Context,
                        r.ContentType, sourceTitle, sourceUrl));
                }

                foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: id))
                {
                    refs.Add(new SourceCrossReference(
                        SourceSystems.Zulip, id,
                        SourceSystems.Confluence, r.TargetId,
                        "mentions", r.Context,
                        r.ContentType, sourceTitle, sourceUrl));
                }

                foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: id))
                {
                    refs.Add(new SourceCrossReference(
                        SourceSystems.Zulip, id,
                        SourceSystems.Fhir, r.TargetId,
                        "mentions", r.Context,
                        r.ContentType, sourceTitle, sourceUrl));
                }
            }
        }

        // FHIR element incoming references (find Zulip messages that mention a FHIR element)
        if (string.Equals(src, SourceSystems.Fhir, StringComparison.OrdinalIgnoreCase) && dir is "incoming" or "both")
        {
            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, ElementPath: id))
            {
                if (int.TryParse(r.SourceId, out int msgId))
                {
                    ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
                    refs.Add(new SourceCrossReference(
                        SourceSystems.Zulip, r.SourceId,
                        SourceSystems.Fhir, r.ElementPath,
                        "mentions", r.Context, r.ContentType,
                        message is not null ? $"{message.StreamName} > {message.Topic}" : null,
                        message is not null ? ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, msgId) : null));
                }
            }
        }

        if (string.Equals(src, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase) && dir is "incoming" or "both")
        {
            List<JiraXRefRecord> jiraRefs = JiraXRefRecord.SelectList(connection, JiraKey: id);
            HashSet<string> seen = [];
            foreach (JiraXRefRecord jiraRef in jiraRefs)
            {
                if (!seen.Add(jiraRef.SourceId)) continue;
                if (!int.TryParse(jiraRef.SourceId, out int msgId)) continue;

                ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
                if (message is null) continue;

                refs.Add(new SourceCrossReference(
                    SourceSystems.Zulip, jiraRef.SourceId,
                    SourceSystems.Jira, id,
                    "mentions", jiraRef.Context,
                    jiraRef.ContentType,
                    $"{message.StreamName} > {message.Topic}",
                    ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, msgId)));
            }
        }

        return Ok(new CrossReferenceResponse(src, id, dir, refs));
    }
}