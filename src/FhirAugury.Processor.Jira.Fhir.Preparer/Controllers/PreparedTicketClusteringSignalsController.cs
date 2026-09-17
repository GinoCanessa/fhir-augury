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
/// Read-only projection over the analytic / clustering inputs the
/// <c>topic-groupings</c> skill needs to bucket tickets into Topics
/// and Linked Ticket Groups. Returns per-ticket summary text from
/// <c>prepared_tickets</c>, partition / display fields from the
/// <c>prepared_jira_hydration</c> self-row, and every
/// <c>prepared_ticket_related_jira</c> edge for the workgroup's
/// tickets. The endpoint is <c>GET</c>-only and does not gate on
/// <c>HydrationStatus</c>, matching the read-only behaviour of the
/// existing grouping / hydration controllers.
/// </summary>
[ApiController]
[Route("api/v1/prepared-ticket-clustering-signals")]
[Produces("application/json")]
public sealed class PreparedTicketClusteringSignalsController : ControllerBase
{
    private readonly PreparerDatabase _database;
    private readonly PreparedTicketCorpusView _corpusView;
    private readonly PreparedTicketGroupingDeltaDispatcher
        _groupingDeltaDispatcher;

    public PreparedTicketClusteringSignalsController(
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
    /// Returns the per-ticket clustering signals for the requested
    /// workgroup, ordered by <c>TicketKey</c> ascending. Returns
    /// <c>404</c> when the workgroup has zero hydrated self-rows so
    /// callers can distinguish "no clustering input" from "empty
    /// catalog" without inspecting an envelope (this mirrors the
    /// grouping controller's <c>GetWorkGroup</c> behaviour, which also
    /// 404s when the workgroup is empty).
    /// <paramref name="workGroupClean"/> may arrive in any of
    /// <c>name</c> / <c>nameClean</c> form — the controller normalises
    /// it via <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/>
    /// defensively. The <c>code</c> form requires pre-resolution at the
    /// orchestrator / CLI / MCP layer.
    /// </summary>
    [HttpGet("{workGroupClean}")]
    [ProducesResponseType(typeof(PreparedTicketClusteringSignalsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreparedTicketClusteringSignalsDto>> GetWorkGroup(
        string workGroupClean,
        CancellationToken ct)
    {
        string cleaned = Hl7WorkGroupNameCleaner.Clean(workGroupClean);
        string canonical = string.IsNullOrEmpty(cleaned) ? workGroupClean : cleaned;
        PreparedTicketClusteringSignals? signals =
            await _database.GetClusteringSignalsAsync(canonical, ct);
        if (signals is null)
        {
            return NotFound();
        }

        return Ok(PreparedTicketClusteringSignalsDtoMapper.ToDto(signals));
    }

    /// <summary>
    /// Returns one publication-reconciliation partition from its immutable
    /// candidate overlay. The durable stage lease and candidate fingerprint
    /// must match the exact partition being read.
    /// </summary>
    [HttpGet("{workGroupClean}/{specification}/{type}")]
    [ProducesResponseType(
        typeof(PreparedTicketClusteringSignalsDto),
        StatusCodes.Status200OK)]
    public async Task<ActionResult<PreparedTicketClusteringSignalsDto>>
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
        string cleaned = Hl7WorkGroupNameCleaner.Clean(workGroupClean);
        string canonical = string.IsNullOrEmpty(cleaned)
            ? workGroupClean
            : cleaned;
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
        PreparedTicketClusteringSignal[] signals = workItem.TicketKeys
            .Select(ticketKey =>
            {
                PreparedTicketPublicationCorpusTicket ticket =
                    tickets.TryGetValue(ticketKey, out var found)
                        ? found
                        : throw new InvalidOperationException(
                            $"Candidate overlay ticket '{ticketKey}' is missing.");
                PreparedJiraHydrationRow self = GetSelfRow(
                    ticket,
                    workItem);
                return new PreparedTicketClusteringSignal(
                    ticket.TicketKey,
                    self.Title,
                    self.Status,
                    self.Specification,
                    self.Type,
                    ticket.Payload.RequestSummary,
                    ticket.Payload.CommentSummary,
                    ticket.Payload.LinkedTicketSummary,
                    ticket.Payload.RelatedTicketSummary,
                    ticket.Payload.RelatedZulipSummary,
                    ticket.Payload.RelatedGitHubSummary,
                    HasPreparedTicket: true,
                    ticket.Payload.RelatedJiraTickets
                        .Select(link => new PreparedTicketClusteringLink(
                            link.AssociatedTicketKey,
                            link.LinkType,
                            link.Justification))
                        .ToArray());
            })
            .ToArray();
        return Ok(PreparedTicketClusteringSignalsDtoMapper.ToDto(
            new PreparedTicketClusteringSignals(
                workItem.WorkGroupClean,
                workItem.WorkGroupDisplay,
                signals)));
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
