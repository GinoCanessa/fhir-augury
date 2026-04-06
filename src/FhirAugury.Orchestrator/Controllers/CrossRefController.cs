using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1")]
public class CrossRefController(
    SourceHttpClient httpClient,
    ILoggerFactory loggerFactory) : ControllerBase
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("OrchestratorHttpApi");

    [HttpGet("xref/{source}/{id}")]
    public async Task<IActionResult> GetCrossReferences(
        [FromRoute] string source,
        [FromRoute] string id,
        [FromQuery] string? direction,
        CancellationToken ct)
    {
        string dir = direction?.ToLowerInvariant() ?? "both";
        List<object> results = [];

        List<Task<CrossReferenceResponse?>> tasks = [];
        foreach (string srcName in httpClient.GetEnabledSourceNames())
        {
            tasks.Add(httpClient.GetCrossReferencesAsync(srcName, id, dir, ct));
        }

        foreach (Task<CrossReferenceResponse?> task in tasks)
        {
            try
            {
                CrossReferenceResponse? result = await task;
                if (result is null) continue;
                results.AddRange(result.References.Select(r => (object)new
                {
                    r.SourceType, r.SourceId, r.TargetType, r.TargetId,
                    r.LinkType, r.Context, r.SourceContentType,
                    r.TargetTitle, r.TargetUrl,
                }));
            }
            catch (Exception ex)
            {
                if (ex.IsTransientHttpError(out string statusDescription))
                    _logger.LogWarning("GetCrossReferences failed for a source ({HttpStatus})", statusDescription);
                else
                    _logger.LogDebug(ex, "GetCrossReferences failed for a source");
            }
        }

        return Ok(new { source, id, direction = dir, references = results });
    }
}
