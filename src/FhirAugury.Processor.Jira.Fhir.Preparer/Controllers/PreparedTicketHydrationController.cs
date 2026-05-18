using FhirAugury.Common.WorkGroups;
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
    /// whose stored <c>WorkGroupClean</c> column matches the canonical
    /// slug derived from <paramref name="workGroupClean"/>.
    /// <paramref name="workGroupClean"/> may arrive in any of
    /// <c>name</c> / <c>nameClean</c> form — the controller normalises
    /// it via <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/>
    /// defensively, so callers may submit either form interchangeably.
    /// The <c>code</c> form (e.g. <c>"oo"</c>) requires pre-resolution
    /// at the orchestrator / CLI / MCP layer where the HL7 catalog is
    /// available. Returns an empty <c>Items</c> list (200 OK) when no
    /// rows match — callers (the <c>index-prepared-db</c> skill) do not
    /// need to distinguish "unknown workgroup" from "no hydrated
    /// tickets" to render an empty README.
    /// </summary>
    [HttpGet("{workGroupClean}")]
    [ProducesResponseType(typeof(PreparedJiraHydrationListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PreparedJiraHydrationListResponse>> GetWorkGroup(
        string workGroupClean,
        CancellationToken ct)
    {
        string canonical = CanonicaliseWorkGroupSlug(workGroupClean);
        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.ListJiraHydrationDisplayForWorkGroupAsync(canonical, ct);
        string? display = await database.ResolveWorkGroupDisplayNameAsync(canonical, ct);
        PreparedJiraHydrationDisplayDto[] items =
            rows.Select(PreparedJiraHydrationDisplayDtoMapper.ToDto).ToArray();
        return Ok(new PreparedJiraHydrationListResponse(canonical, display, items));
    }

    /// <summary>
    /// Routes any of <c>name</c> / <c>nameClean</c> through
    /// <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/>. Idempotent —
    /// a value that is already canonical round-trips unchanged. Empty
    /// cleaner output (e.g. for code-style inputs that contain no ASCII
    /// alphanumerics after cleaning) falls through to the raw input.
    /// </summary>
    internal static string CanonicaliseWorkGroupSlug(string raw)
    {
        string cleaned = Hl7WorkGroupNameCleaner.Clean(raw);
        return string.IsNullOrEmpty(cleaned) ? raw : cleaned;
    }
}
