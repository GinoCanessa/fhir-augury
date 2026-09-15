using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Jira.Controllers;

/// <summary>Explicit source maintenance; item reads never start this operation.</summary>
[ApiController]
[Route("api/v1/public-people")]
public sealed class PublicPeopleController(JiraPublicPeopleBackfillService service) : ControllerBase
{
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] JiraPublicPeoplePreviewRequest request, CancellationToken ct)
    {
        JiraPublicPeoplePreviewResponse response = await service.PreviewAsync(request, ct);
        return StatusCode(JiraPublicPeopleCodes.HttpStatus(response.Code), response);
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] JiraPublicPeopleApplyRequest request, CancellationToken ct)
    {
        JiraPublicPeopleApplyResponse response = await service.ApplyAsync(request, ct);
        return StatusCode(JiraPublicPeopleCodes.HttpStatus(response.Code), response);
    }
}
