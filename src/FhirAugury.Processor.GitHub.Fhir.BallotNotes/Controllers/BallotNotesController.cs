using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Controllers;

/// <summary>
/// Read/query + prose write-back endpoints over the hydrated notes. Modelled on
/// the <c>prepared-ticket-*</c> controllers' read-signals / write-back rhythm.
/// </summary>
[ApiController]
[Route("api/v1/ballot-notes")]
[Produces("application/json")]
public sealed class BallotNotesController(
    BallotNotesDatabase database) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult List(
        [FromQuery] string? repo,
        [FromQuery] string? workGroup,
        [FromQuery] string? type,
        [FromQuery] string? needsNote,
        [FromQuery] string? status,
        [FromQuery] int? limit,
        [FromQuery] int? offset)
    {
        NoteQueryFilter filter = new()
        {
            Repo = repo,
            WorkGroupCode = workGroup,
            Type = type,
            NeedsNote = needsNote,
            Status = status,
            Limit = limit ?? 50,
            Offset = offset ?? 0,
        };

        IReadOnlyList<NoteListRow> notes = database.ListNotes(filter);
        return Ok(new BallotNoteListResponse { Total = notes.Count, Notes = notes });
    }

    [HttpGet("{slug}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get([FromRoute] string slug)
    {
        NoteDetail? detail = database.GetNote(slug);
        if (detail is null)
        {
            return NotFound(new { error = $"Note '{slug}' not found." });
        }
        BallotNoteDetailDto dto = BallotNoteDtoMapper.ToDetailDto(detail) with
        {
            CurrentHydrationExecutionId =
                detail.Note.CurrentHydrationExecutionId,
            CurrentEvidenceHash = detail.Note.CurrentEvidenceHash,
            CurrentEvidenceRevision = detail.Note.CurrentEvidenceRevision,
            ProseHydrationExecutionId =
                detail.Note.ProseHydrationExecutionId,
            ProseEvidenceRevision = detail.Note.ProseEvidenceRevision,
            ProseVerificationStatus = detail.Note.ProseVerificationStatus,
        };
        return Ok(dto);
    }

}
