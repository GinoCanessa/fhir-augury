namespace FhirAugury.Server.Terminology.Models;

/// <summary>
/// Response envelope for <c>POST /api/v1/terminology/check</c>.
/// </summary>
public sealed record OverlapCheckResult
{
    public OverlapCandidate[] Candidates { get; init; } = [];
    public RequestSummary Summary { get; init; } = new();
    public long ElapsedMs { get; init; }
}

/// <summary>
/// Echoes the effective request parameters back to the caller so it
/// can confirm what the service actually applied.
/// </summary>
public sealed record RequestSummary
{
    public string Mode { get; init; } = string.Empty;
    public int Limit { get; init; }
    public double MinScore { get; init; }

    /// <summary>Submission canonical URL (if present in the resource).</summary>
    public string? SubmissionUrl { get; init; }

    /// <summary>"CodeSystem" or "ValueSet".</summary>
    public string? SubmissionKind { get; init; }

    /// <summary>Concept count after flattening.</summary>
    public int SubmissionConceptCount { get; init; }

    /// <summary>"R4" or "R5".</summary>
    public string? SubmissionFhirVersion { get; init; }
}
