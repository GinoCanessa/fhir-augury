using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

[ApiController]
[Route("api/v1")]
public class CrossRefController(JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("xref/{id}")]
    public IActionResult GetCrossReferences([FromRoute] string id, [FromQuery] string? source, [FromQuery] string? direction)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        string dir = direction?.ToLowerInvariant() ?? "both";
        List<SourceCrossReference> references = [];

        // Jira-to-Jira links (outgoing)
        if (dir is "outgoing" or "both")
        {
            List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, SourceKey: id);
            foreach (JiraIssueLinkRecord link in links)
            {
                JiraIssueRecord? target = JiraIssueRecord.SelectSingle(connection, Key: link.TargetKey);
                references.Add(new SourceCrossReference(
                    SourceSystems.Jira, id,
                    SourceSystems.Jira, link.TargetKey,
                    "linked_issue", null, "issue",
                    target?.Title, $"{options.BaseUrl}/browse/{link.TargetKey}"));
            }
        }

        // Jira-to-Jira links (incoming)
        if (dir is "incoming" or "both")
        {
            List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, TargetKey: id);
            foreach (JiraIssueLinkRecord link in links)
            {
                JiraIssueRecord? sourceIssue = JiraIssueRecord.SelectSingle(connection, Key: link.SourceKey);
                references.Add(new SourceCrossReference(
                    SourceSystems.Jira, link.SourceKey,
                    SourceSystems.Jira, id,
                    "linked_issue", null, "issue",
                    sourceIssue?.Title, $"{options.BaseUrl}/browse/{link.SourceKey}"));
            }
        }

        // Cross-source outgoing references (filtered by source when provided)
        // TODO: TargetTitle/TargetUrl are null for cross-source refs because Jira doesn't have the
        // target system's data. The Orchestrator should enrich these fields during fan-out.
        if (dir is "outgoing" or "both")
        {
            if (source is null || source.Equals(SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
            {
                foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: id))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, id,
                        SourceSystems.Zulip, r.TargetId,
                        "mentions", r.Context, r.ContentType, null, null));
                }
            }

            if (source is null || source.Equals(SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase))
            {
                foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: id))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, id,
                        SourceSystems.GitHub, r.TargetId,
                        "mentions", r.Context, r.ContentType, null, null));
                }
            }

            if (source is null || source.Equals(SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase))
            {
                foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: id))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, id,
                        SourceSystems.Confluence, r.TargetId,
                        "mentions", r.Context, r.ContentType, null, null));
                }
            }

            if (source is null || source.Equals(SourceSystems.Fhir, StringComparison.OrdinalIgnoreCase))
            {
                foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: id))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, id,
                        SourceSystems.Fhir, r.TargetId,
                        "mentions", r.Context, r.ContentType, null, null));
                }
            }
        }

        // FHIR element incoming references (find Jira items that mention a FHIR element)
        if ((source is null || string.Equals(source, SourceSystems.Fhir, StringComparison.OrdinalIgnoreCase)) && dir is "incoming" or "both")
        {
            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, ElementPath: id))
            {
                JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: r.SourceId);
                references.Add(new SourceCrossReference(
                    SourceSystems.Jira, r.SourceId,
                    SourceSystems.Fhir, r.ElementPath,
                    "mentions", r.Context, r.ContentType,
                    issue?.Title, issue is not null ? $"{options.BaseUrl}/browse/{r.SourceId}" : null));
            }
        }

        // Spec artifact links (GitHub-targeted)
        if (source is null || source.Equals(SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase))
        {
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: id);
            if (issue?.Specification is not null)
            {
                JiraSpecArtifactRecord? specArtifact = JiraSpecArtifactRecord.SelectSingle(connection, SpecKey: issue.Specification);
                if (specArtifact?.GitUrl is not null)
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, id,
                        SourceSystems.GitHub, specArtifact.GitUrl,
                        "spec_artifact", $"{specArtifact.SpecName} ({specArtifact.Family})",
                        "issue", issue.Title, $"{options.BaseUrl}/browse/{id}"));
                }
            }
        }

        return Ok(new CrossReferenceResponse(SourceSystems.Jira, id, dir, references));
    }
}