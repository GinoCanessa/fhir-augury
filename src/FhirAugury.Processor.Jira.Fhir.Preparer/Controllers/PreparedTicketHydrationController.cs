using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;

/// <summary>
/// Read-only projection over <c>prepared_jira_hydration</c> self-rows
/// for a workgroup. Companion to
/// <see cref="PreparedTicketGroupingsController"/>: the grouping
/// controller provides the
/// <c>(WorkGroup, Specification, Type) → Topic → Linked Ticket Group</c>
/// decomposition, while this controller provides the per-ticket display
/// fields (<c>Title</c>, <c>Status</c>, <c>Type</c>, <c>Specification</c>,
/// <c>WorkGroup</c>, <c>Url</c>, <c>UpdatedAt</c>) keyed by ticket key.
/// Consumed by the <c>index-prepared-db</c> skill.
/// </summary>
[ApiController]
[Route("api/v1/prepared-ticket-hydration")]
[Produces("application/json")]
public sealed class PreparedTicketHydrationController(PreparerDatabase database) : ControllerBase
{
    /// <summary>
    /// Lists the prepared-ticket display projection for every self-row
    /// in <c>prepared_jira_hydration</c> (<c>JiraKey = TicketKey</c>)
    /// whose <c>WorkGroup</c> matches <paramref name="workGroupClean"/>
    /// under the <c>REPLACE(IFNULL(WorkGroup, ''), ' ', '')</c>
    /// convention used by the grouping query. Returns an empty
    /// <c>Items</c> list (200 OK) when no rows match — callers
    /// (the <c>index-prepared-db</c> skill) do not need to distinguish
    /// "unknown workgroup" from "no hydrated tickets" to render an
    /// empty README.
    /// </summary>
    [HttpGet("{workGroupClean}")]
    [ProducesResponseType(typeof(PreparedJiraHydrationListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PreparedJiraHydrationListResponse>> GetWorkGroup(
        string workGroupClean,
        CancellationToken ct)
    {
        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.ListJiraHydrationDisplayForWorkGroupAsync(workGroupClean, ct);
        string? display = await database.ResolveWorkGroupDisplayNameAsync(workGroupClean, ct);
        PreparedJiraHydrationDisplayDto[] items =
            rows.Select(PreparedJiraHydrationDisplayDtoMapper.ToDto).ToArray();
        return Ok(new PreparedJiraHydrationListResponse(workGroupClean, display, items));
    }
}
