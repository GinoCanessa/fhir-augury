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
public sealed class BallotNotesController(BallotNotesDatabase database) : ControllerBase
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
        return detail is null
            ? NotFound(new { error = $"Note '{slug}' not found." })
            : Ok(BallotNoteDtoMapper.ToDetailDto(detail));
    }

    [HttpPut("{slug}/note")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult PutNote([FromRoute] string slug, [FromBody] BallotNoteProsePutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        BallotNoteProse prose = new()
        {
            NeedsNote = NormalizeNeedsNote(request.NeedsNote),
            ProposedBallotNoteHtml = request.ProposedBallotNoteHtml ?? string.Empty,
            RollupSummaryMarkdown = request.RollupSummaryMarkdown ?? string.Empty,
            NotesForReviewerMarkdown = request.NotesForReviewerMarkdown ?? string.Empty,
            SourceFilesNote = request.SourceFilesNote ?? string.Empty,
        };

        bool updated = database.UpdateNoteProse(slug, prose, DateTimeOffset.UtcNow);
        return updated
            ? Ok(new BallotNoteProseSaveResultDto { NoteId = slug, Status = "authored" })
            : NotFound(new { error = $"Note '{slug}' was never hydrated; prose cannot attach to a non-existent unit." });
    }

    private static string NormalizeNeedsNote(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" => "yes",
        "no" or "false" => "no",
        _ => "unknown",
    };
}
