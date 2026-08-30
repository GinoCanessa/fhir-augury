using FhirAugury.Source.Confluence.Database.Records;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Confluence.Controllers;

/// <summary>
/// The operator control plane for the ingestion gate: see the block, and clear
/// it once the browser challenge has been solved.
/// </summary>
[ApiController]
[Route("api/v1")]
public class IngestionBlockController(ConfluenceIngestionGate gate) : ControllerBase
{
    /// <summary>Reports whether Confluence ingestion is currently blocked.</summary>
    /// <response code="200">The current block state; <c>blocked</c> is false when ingestion is free to run.</response>
    [HttpGet("ingestion-block")]
    public IActionResult GetIngestionBlock()
    {
        ConfluenceIngestionBlockRecord? block = gate.Current;

        return Ok(new IngestionBlockResponse(
            Blocked: gate.IsBlocked,
            BlockedAt: block?.Blocked == true ? block.BlockedAt : null,
            Reason: block?.Reason,
            HttpStatus: block?.HttpStatus,
            ReasonPhrase: block?.ReasonPhrase,
            Fingerprint: block?.Fingerprint,
            RequestUrl: block?.RequestUrl,
            ClearedAt: block?.ClearedAt,
            ClearedBy: block?.ClearedBy,
            Remediation: ConfluenceHumanInterventionRequiredException.RemediationText));
    }

    /// <summary>
    /// Clears the block so ingestion can resume. Clearing an open gate is not an
    /// error; the response says whether anything was actually standing.
    /// </summary>
    /// <param name="clearedBy">Optional operator name, recorded on the row.</param>
    /// <response code="200">The gate is open. <c>wasBlocked</c> reports whether it had been closed.</response>
    [HttpPost("ingestion-block/clear")]
    public IActionResult ClearIngestionBlock([FromQuery] string? clearedBy)
    {
        bool wasBlocked = gate.Clear(clearedBy);

        return Ok(new IngestionBlockClearResponse(
            WasBlocked: wasBlocked,
            Blocked: gate.IsBlocked,
            ClearedAt: gate.Current?.ClearedAt,
            ClearedBy: gate.Current?.ClearedBy));
    }
}

/// <summary>The current state of the Confluence ingestion gate.</summary>
public record IngestionBlockResponse(
    bool Blocked,
    DateTimeOffset? BlockedAt,
    string? Reason,
    int? HttpStatus,
    string? ReasonPhrase,
    string? Fingerprint,
    string? RequestUrl,
    DateTimeOffset? ClearedAt,
    string? ClearedBy,
    string Remediation);

/// <summary>The outcome of a clear request.</summary>
public record IngestionBlockClearResponse(
    bool WasBlocked,
    bool Blocked,
    DateTimeOffset? ClearedAt,
    string? ClearedBy);
