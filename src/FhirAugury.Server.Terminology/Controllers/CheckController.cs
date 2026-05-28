using System.Diagnostics;
using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Ingestion;
using FhirAugury.Server.Terminology.Matching;
using FhirAugury.Server.Terminology.Models;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirAugury.Server.Terminology.Controllers;

/// <summary>
/// Primary HTTP surface: accepts a FHIR <c>CodeSystem</c> or
/// <c>ValueSet</c> and returns the ranked set of THO artifacts that
/// the submission appears to overlap with.
/// </summary>
[ApiController]
[Route("api/v1/terminology")]
public class CheckController : ControllerBase
{
    private static readonly string[] AcceptedContentTypes =
    [
        "application/fhir+json",
        "application/json",
        "text/json",
    ];

    private readonly TerminologyResourceParser _parser;
    private readonly SubmissionNormalizer _normalizer;
    private readonly MatcherSelector _selector;
    private readonly TerminologyServiceOptions _options;
    private readonly ILogger<CheckController> _logger;

    public CheckController(
        TerminologyResourceParser parser,
        SubmissionNormalizer normalizer,
        MatcherSelector selector,
        IOptions<TerminologyServiceOptions> options,
        ILogger<CheckController> logger)
    {
        _parser = parser;
        _normalizer = normalizer;
        _selector = selector;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost("check")]
    public async Task<IActionResult> Check(
        [FromQuery] int? limit,
        [FromQuery] double? minScore,
        [FromQuery] string? mode,
        CancellationToken ct)
    {
        // ── 1. Validate Content-Type ────────────────────────────
        string? ctype = Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(ctype) || !AcceptedContentTypes.Contains(ctype))
        {
            Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return new JsonResult(new
            {
                error = "unsupported_media_type",
                accepted = AcceptedContentTypes,
            });
        }

        // ── 2. Read body as string ──────────────────────────────
        string body;
        using (StreamReader sr = new(Request.Body, leaveOpen: false))
        {
            body = await sr.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return BadRequest(new
            {
                error = "empty_body",
                details = "Request body must contain a FHIR CodeSystem or ValueSet JSON resource.",
            });
        }

        // ── 3. R5-then-R4 deserialization with non-Firely fault handling ─
        Resource? parsed = null;
        string? versionLabel = null;
        try
        {
            parsed = _parser.TryParse(body, FhirMajorVersion.R5);
            if (parsed is not null) versionLabel = "R5";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "R5 deserialization threw; falling back to R4.");
        }

        if (parsed is null)
        {
            try
            {
                parsed = _parser.TryParse(body, FhirMajorVersion.R4);
                if (parsed is not null) versionLabel = "R4";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "R4 deserialization threw.");
            }
        }

        if (parsed is null || versionLabel is null)
        {
            return BadRequest(new
            {
                error = "invalid_fhir_resource",
                details = "Body could not be deserialized as a CodeSystem or ValueSet under R5 or R4.",
                attempted_versions = new[] { "R5", "R4" },
            });
        }

        // ── 4. Normalize submission (may throw SubmissionTooLargeException) ─
        NormalizedSubmission submission;
        try
        {
            submission = parsed switch
            {
                CodeSystem cs => _normalizer.Normalize(cs, versionLabel),
                ValueSet vs => _normalizer.Normalize(vs, versionLabel),
                _ => throw new InvalidOperationException($"Unsupported resource type '{parsed.TypeName}'."),
            };
        }
        catch (SubmissionTooLargeException ex)
        {
            Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return new JsonResult(new
            {
                error = "too_many_concepts",
                cap = ex.Cap,
                submitted = ex.Submitted,
            });
        }

        // ── 5. Resolve effective parameters ─────────────────────
        string effectiveMode = (mode ?? _options.Defaults.Mode).Trim().ToLowerInvariant();
        int effectiveLimit = limit ?? _options.Defaults.Limit;
        double effectiveMinScore = minScore ?? _options.Defaults.MinScore;

        // Embeddings/hybrid require the provider to be enabled.
        if ((effectiveMode == "embeddings" || effectiveMode == "hybrid") && !_options.Embeddings.Enabled)
        {
            string errorCode = effectiveMode == "hybrid"
                ? "embeddings_unavailable_for_hybrid"
                : "mode_unavailable";
            return BadRequest(new
            {
                error = errorCode,
                requested = effectiveMode,
                details = "Embeddings are disabled in this deployment.",
                enabled_modes = EnabledModes(),
            });
        }

        // ── 6. Dispatch to matcher ──────────────────────────────
        if (!_selector.TryResolve(effectiveMode, out ITerminologyMatcher matcher))
        {
            return BadRequest(new
            {
                error = "mode_unavailable",
                requested = effectiveMode,
                details = "No matcher registered for the requested mode.",
                enabled_modes = EnabledModes(),
            });
        }

        OverlapCheckRequest req = new()
        {
            Mode = effectiveMode,
            Limit = effectiveLimit,
            MinScore = effectiveMinScore,
        };

        Stopwatch sw = Stopwatch.StartNew();
        IReadOnlyList<OverlapCandidate> candidates = await matcher.MatchAsync(submission, req, ct);
        sw.Stop();

        // ── 7. Build response envelope ──────────────────────────
        OverlapCheckResult result = new()
        {
            Candidates = candidates.ToArray(),
            Summary = new RequestSummary
            {
                Mode = effectiveMode,
                Limit = effectiveLimit,
                MinScore = effectiveMinScore,
                SubmissionUrl = submission.CanonicalUrl,
                SubmissionKind = submission.Kind,
                SubmissionConceptCount = submission.Concepts.Count,
                SubmissionFhirVersion = submission.FhirVersion,
            },
            ElapsedMs = sw.ElapsedMilliseconds,
        };

        return Ok(result);
    }

    private string[] EnabledModes()
    {
        List<string> modes = ["lexical"];
        if (_options.Embeddings.Enabled)
        {
            modes.Add("embeddings");
            modes.Add("hybrid");
        }
        return modes.ToArray();
    }
}
