namespace FhirAugury.Server.Terminology.Models;

/// <summary>
/// One ranked overlap candidate returned by the matching pipeline.
/// </summary>
public sealed record OverlapCandidate
{
    public required string CanonicalUrl { get; init; }
    public string? Version { get; init; }
    public string? Title { get; init; }

    /// <summary>"CodeSystem" or "ValueSet".</summary>
    public required string Kind { get; init; }

    /// <summary>"R4" or "R5".</summary>
    public required string FhirVersion { get; init; }

    /// <summary>"metadata" | "content" | "both" — which signal lit up.</summary>
    public required string MatchCategory { get; init; }

    /// <summary>Composite score (0..1 after normalization).</summary>
    public double Score { get; init; }

    /// <summary>Per-signal scores (e.g. metadata_bm25, content_bm25, code_jaccard).</summary>
    public IReadOnlyDictionary<string, double> SubScores { get; init; } =
        new Dictionary<string, double>();

    /// <summary>Human-readable explanations driven by signal thresholds.</summary>
    public string[] Reasons { get; init; } = [];

    /// <summary>Up to N concept overlaps (code + display).</summary>
    public CodeDisplay[] SampleConcepts { get; init; } = [];

    /// <summary>True when this candidate's FHIR version differs from the submission's.</summary>
    public bool CrossVersion { get; init; }
}

/// <summary>Lightweight (code, display) pair for sample concepts.</summary>
public sealed record CodeDisplay(string Code, string? Display, string? System);
