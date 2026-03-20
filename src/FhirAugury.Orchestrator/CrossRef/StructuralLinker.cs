using Fhiraugury;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using FhirAugury.Orchestrator.Routing;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Orchestrator.CrossRef;

/// <summary>
/// Creates structural cross-reference links using JIRA-Spec-Artifacts data.
/// Links Jira specifications to GitHub repositories without text scanning.
/// </summary>
public class StructuralLinker(
    OrchestratorDatabase database,
    SourceRouter router,
    ILogger<StructuralLinker> logger)
{
    /// <summary>
    /// Fetches spec artifacts from the Jira service and creates structural links
    /// between Jira specifications and GitHub repositories.
    /// </summary>
    public async Task<int> LinkSpecArtifactsAsync(CancellationToken ct)
    {
        var jiraClient = router.GetJiraClient();
        if (jiraClient is null)
        {
            logger.LogWarning("Jira service not available for structural linking");
            return 0;
        }

        var newLinks = 0;
        using var connection = database.OpenConnection();

        var request = new JiraListSpecArtifactsRequest();
        using var stream = jiraClient.ListSpecArtifacts(request, cancellationToken: ct);

        while (await stream.ResponseStream.MoveNext(ct))
        {
            var artifact = stream.ResponseStream.Current;

            if (!string.IsNullOrEmpty(artifact.GitUrl))
            {
                // Link spec → GitHub repo
                var existingLinks = CrossRefLinkRecord.SelectList(connection,
                    SourceType: "jira", SourceId: artifact.SpecKey);
                var exists = existingLinks.Any(l =>
                    l.TargetType == "github" && l.TargetId == artifact.GitUrl && l.LinkType == "spec_artifact");

                if (!exists)
                {
                    CrossRefLinkRecord.Insert(connection, new CrossRefLinkRecord
                    {
                        Id = CrossRefLinkRecord.GetIndex(),
                        SourceType = "jira",
                        SourceId = artifact.SpecKey,
                        TargetType = "github",
                        TargetId = artifact.GitUrl,
                        LinkType = "spec_artifact",
                        Context = $"{artifact.SpecName} ({artifact.Family})",
                        DiscoveredAt = DateTimeOffset.UtcNow,
                    }, insertPrimaryKey: true);
                    newLinks++;
                }
            }
        }

        logger.LogInformation("Structural linking complete: {NewLinks} new spec-artifact links", newLinks);
        return newLinks;
    }
}
