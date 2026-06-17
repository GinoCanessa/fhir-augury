using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Controllers;

/// <summary>
/// On-demand hydration trigger and status poll. <c>POST</c> validates the clone
/// + since-commit synchronously (surfacing a missing clone / unreachable git as
/// <c>503</c>), creates a <c>running</c> run row, fires the commit-window walk
/// fire-and-forget, and returns <c>202 Accepted</c>. <c>orchestrate-notes</c>
/// polls the status endpoint until the run is <c>completed</c>/<c>failed</c>.
/// </summary>
[ApiController]
[Route("api/v1/ballot-notes/hydrate")]
[Produces("application/json")]
public sealed class BallotNotesHydrationController(
    BallotNotesDatabase database,
    BallotNotesHydrator hydrator,
    IOptions<BallotNotesHydrationOptions> hydrationOptions,
    ILogger<BallotNotesHydrationController> logger,
    IHostApplicationLifetime lifetime) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Hydrate([FromBody] HydrateRequest request, CancellationToken ct)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.RepoOwner)
            || string.IsNullOrWhiteSpace(request.RepoName)
            || string.IsNullOrWhiteSpace(request.SinceSha))
        {
            return BadRequest(new { error = "repoOwner, repoName, and sinceSha are required." });
        }

        string owner = request.RepoOwner.Trim();
        string name = request.RepoName.Trim();
        string clonePath = Path.Combine(hydrationOptions.Value.CloneRoot, $"{owner}_{name}", "clone");

        if (!Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Clone unavailable",
                detail: $"No git clone found at {clonePath}. Ensure the GitHub source has cloned {owner}/{name}.");
        }

        string sinceFull, headSha;
        try
        {
            GitRunner.GitResult sinceCheck = await GitRunner.TryRunAsync(
                clonePath, ["rev-parse", "--verify", $"{request.SinceSha.Trim()}^{{commit}}"], ct);
            if (sinceCheck.ExitCode != 0)
            {
                return Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Since-commit unresolvable",
                    detail: $"Could not resolve since-commit '{request.SinceSha}' in {clonePath}.");
            }

            sinceFull = sinceCheck.StdOut.Trim();
            headSha = (await GitRunner.RunAsync(clonePath, ["rev-parse", "HEAD"], ct)).Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "git unavailable while validating hydrate request for {Owner}/{Name}", owner, name);
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "git unavailable",
                detail: ex.Message);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string runKey = $"{owner}/{name}@{sinceFull}..{headSha}";

        database.BeginRun(new NotesRunRecord
        {
            RunKey = runKey,
            RepoOwner = owner,
            RepoName = name,
            RepoCategory = request.RepoCategory?.Trim() ?? string.Empty,
            SinceSha = sinceFull,
            SinceShortSha = ShortSha(sinceFull),
            HeadSha = headSha,
            HeadShortSha = ShortSha(headSha),
            Status = "running",
            UnitsTotal = 0,
            StartedAt = now,
            RunAt = now,
        });

        BallotNotesHydrationRequest hydrationRequest = new()
        {
            RepoOwner = owner,
            RepoName = name,
            SinceSha = sinceFull,
            RunKey = runKey,
            RepoCategory = request.RepoCategory?.Trim() ?? string.Empty,
            WorkGroupHint = request.WorkGroupHint,
        };

        // Fire-and-forget; the lifetime token cancels the walk on shutdown. The
        // hydrator never throws past FinishRun(failed), but attach a fault log
        // to avoid leaking an unobserved exception.
        Task run = Task.Run(() => hydrator.HydrateAsync(hydrationRequest, lifetime.ApplicationStopping), lifetime.ApplicationStopping);
        _ = run.ContinueWith(
            t => logger.LogError(t.Exception, "BallotNotes hydration task faulted for {RunKey}", runKey),
            TaskContinuationOptions.OnlyOnFaulted);

        return Accepted(new HydrateAcceptedDto { RunKey = runKey, Status = "running", UnitsTotal = 0 });
    }

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Status([FromQuery] string? runKey)
    {
        // runKey contains '/' (owner/name@since..head), so it is a query
        // parameter, not a path segment. Omit it to read the latest run.
        NotesRunRecord? run = string.IsNullOrWhiteSpace(runKey)
            ? database.GetLatestRun()
            : database.GetRun(runKey);

        return run is null
            ? NotFound(new { error = runKey is null ? "No hydration run has been recorded." : $"Run '{runKey}' not found." })
            : Ok(BallotNoteDtoMapper.ToStatusDto(run));
    }

    private static string ShortSha(string fullSha)
    {
        string full = fullSha.Trim();
        return full.Length > 12 ? full[..12] : full;
    }
}
