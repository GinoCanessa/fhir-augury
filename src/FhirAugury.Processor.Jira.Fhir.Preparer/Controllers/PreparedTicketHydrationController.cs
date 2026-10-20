using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
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
/// <c>WorkGroup</c>, <c>Url</c>, <c>UpdatedAt</c>) keyed by ticket key for
/// reviewer-facing API clients.
/// </summary>
[ApiController]
[Route("api/v1/prepared-ticket-hydration")]
[Produces("application/json")]
public sealed class PreparedTicketHydrationController : ControllerBase
{
    private readonly PreparerDatabase _database;
    private readonly PreparedTicketCorpusView _corpusView;
    private readonly PreparedTicketGroupingDeltaDispatcher
        _groupingDeltaDispatcher;

    public PreparedTicketHydrationController(
        PreparerDatabase database,
        PreparedTicketCorpusView? corpusView = null,
        PreparedTicketGroupingDeltaDispatcher? groupingDeltaDispatcher = null)
    {
        _database = database;
        _corpusView = corpusView ?? new PreparedTicketCorpusView(database);
        _groupingDeltaDispatcher = groupingDeltaDispatcher ??
            new PreparedTicketGroupingDeltaDispatcher(database);
    }

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
    /// available. Returns an empty <c>Items</c> list (200 OK) when no rows
    /// match, preserving the endpoint's compatibility semantics for callers.
    /// </summary>
    [HttpGet("{workGroupClean}")]
    [ProducesResponseType(typeof(PreparedJiraHydrationListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PreparedJiraHydrationListResponse>> GetWorkGroup(
        string workGroupClean,
        CancellationToken ct)
    {
        string canonical = CanonicaliseWorkGroupSlug(workGroupClean);
        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await _database.ListJiraHydrationDisplayForWorkGroupAsync(
                canonical,
                ct);
        string? display =
            await _database.ResolveWorkGroupDisplayNameAsync(canonical, ct);
        PreparedJiraHydrationDisplayDto[] items =
            rows.Select(PreparedJiraHydrationDisplayDtoMapper.ToDto).ToArray();
        return Ok(new PreparedJiraHydrationListResponse(canonical, display, items));
    }

    /// <summary>
    /// Returns one publication-reconciliation partition from the same
    /// candidate overlay used to derive its grouping membership.
    /// </summary>
    [HttpGet("{workGroupClean}/{specification}/{type}")]
    [ProducesResponseType(
        typeof(PreparedJiraHydrationListResponse),
        StatusCodes.Status200OK)]
    public async Task<ActionResult<PreparedJiraHydrationListResponse>>
        GetReconciliationPartition(
            string workGroupClean,
            string specification,
            string type,
            [FromQuery] string runId,
            [FromQuery] string stageId,
            [FromQuery] string stageLeaseId,
            [FromQuery] string inputFingerprint,
            CancellationToken ct)
    {
        string canonical = CanonicaliseWorkGroupSlug(workGroupClean);
        string partitionKey = PreparerDatabase.GetPartitionKey(
            canonical,
            specification,
            type);
        PreparedTicketPublicationGroupingWorkItem workItem =
            await _groupingDeltaDispatcher.GetStageWorkItemAsync(
                runId,
                stageId,
                stageLeaseId,
                partitionKey,
                inputFingerprint,
                ct);
        PreparedTicketPublicationCorpusOverlay overlay =
            await _corpusView.GetAsync(runId, ct);
        Dictionary<string, PreparedTicketPublicationCorpusTicket> tickets =
            overlay.Tickets.ToDictionary(
                value => value.TicketKey,
                StringComparer.OrdinalIgnoreCase);
        PreparedJiraHydrationDisplayDto[] items = workItem.TicketKeys
            .Select(ticketKey =>
            {
                PreparedTicketPublicationCorpusTicket ticket =
                    tickets.TryGetValue(ticketKey, out var found)
                        ? found
                        : throw new InvalidOperationException(
                            $"Candidate overlay ticket '{ticketKey}' is missing.");
                return PreparedJiraHydrationDisplayDtoMapper.ToDto(
                    GetSelfRow(ticket, workItem));
            })
            .ToArray();
        return Ok(new PreparedJiraHydrationListResponse(
            workItem.WorkGroupClean,
            workItem.WorkGroupDisplay,
            items));
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

    private static PreparedJiraHydrationRow GetSelfRow(
        PreparedTicketPublicationCorpusTicket ticket,
        PreparedTicketPublicationGroupingWorkItem workItem)
    {
        PreparedJiraHydrationRow self = ticket.Hydration.JiraRows.Single(
            row => string.Equals(
                row.JiraKey,
                ticket.TicketKey,
                StringComparison.OrdinalIgnoreCase));
        string workGroupClean = Hl7WorkGroupNameCleaner.Clean(
            self.WorkGroup);
        string specification = string.IsNullOrWhiteSpace(self.Specification)
            ? "Unspecified"
            : self.Specification.Trim();
        string type = self.Type?.Trim() ?? string.Empty;
        if (!string.Equals(
                PreparerDatabase.GetPartitionKey(
                    workGroupClean,
                    specification,
                    type),
                workItem.PartitionKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Candidate overlay ticket '{ticket.TicketKey}' is outside grouping partition '{workItem.PartitionKey}'.");
        }
        return self;
    }
}
