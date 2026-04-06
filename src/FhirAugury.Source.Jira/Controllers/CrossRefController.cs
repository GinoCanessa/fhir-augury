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
    [HttpGet("xref/{key}")]
    public IActionResult GetCrossReferences([FromRoute] string key, [FromQuery] string? source, [FromQuery] string? direction)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        string dir = direction?.ToLowerInvariant() ?? "both";
        List<SourceCrossReference> references = [];

        // Jira-to-Jira links (outgoing)
        if (dir is "outgoing" or "both")
        {
            List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, SourceKey: key);
            foreach (JiraIssueLinkRecord link in links)
            {
                JiraIssueRecord? target = JiraIssueRecord.SelectSingle(connection, Key: link.TargetKey);
                references.Add(new SourceCrossReference(
                    SourceSystems.Jira, key,
                    SourceSystems.Jira, link.TargetKey,
                    "linked_issue", null, "issue",
                    target?.Title, $"{options.BaseUrl}/browse/{link.TargetKey}"));
            }
        }

        // Jira-to-Jira links (incoming)
        if (dir is "incoming" or "both")
        {
            List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, TargetKey: key);
            foreach (JiraIssueLinkRecord link in links)
            {
                JiraIssueRecord? sourceIssue = JiraIssueRecord.SelectSingle(connection, Key: link.SourceKey);
                references.Add(new SourceCrossReference(
                    SourceSystems.Jira, link.SourceKey,
                    SourceSystems.Jira, key,
                    "linked_issue", null, "issue",
                    sourceIssue?.Title, $"{options.BaseUrl}/browse/{link.SourceKey}"));
            }
        }

        // Cross-source outgoing references (filtered by source when provided)
        if (dir is "outgoing" or "both")
        {
            if (source is null || source.Equals(SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
            {
                foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: key))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.Zulip, r.TargetId,
                        "mentions", r.Context, "issue", null, null));
                }
            }

            if (source is null || source.Equals(SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase))
            {
                foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: key))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.GitHub, r.TargetId,
                        "mentions", r.Context, "issue", null, null));
                }
            }

            if (source is null || source.Equals(SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase))
            {
                foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: key))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.Confluence, r.TargetId,
                        "mentions", r.Context, "issue", null, null));
                }
            }

            if (source is null || source.Equals(SourceSystems.Fhir, StringComparison.OrdinalIgnoreCase))
            {
                foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: key))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.Fhir, r.TargetId,
                        "mentions", r.Context, "issue", null, null));
                }
            }
        }

        // Spec artifact links (GitHub-targeted)
        if (source is null || source.Equals(SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase))
        {
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (issue?.Specification is not null)
            {
                JiraSpecArtifactRecord? specArtifact = JiraSpecArtifactRecord.SelectSingle(connection, SpecKey: issue.Specification);
                if (specArtifact?.GitUrl is not null)
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.GitHub, specArtifact.GitUrl,
                        "spec_artifact", $"{specArtifact.SpecName} ({specArtifact.Family})",
                        "issue", issue.Title, $"{options.BaseUrl}/browse/{key}"));
                }
            }
        }

        return Ok(new CrossReferenceResponse(SourceSystems.Jira, key, dir, references));
    }
}