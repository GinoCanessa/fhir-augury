using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Durable progress for a single repo's history backfill, persisted as JSON in the
/// <c>LastCursor</c> column of the <c>backfill-progress:&lt;repo&gt;</c> sync-state row.
/// </summary>
/// <remarks>
/// The backfill walks each phase (issues, then PRs) in <b>descending</b> item-number
/// order, so a single descending watermark plus a small set of known failures describes
/// the whole of what remains. This is deliberately cheaper than a per-item completion
/// table: one row per repo answers what 4,285 rows would.
/// </remarks>
public sealed record GitHubBackfillCursor
{
    /// <summary>
    /// Every item numbered &gt;= this value has been FULLY processed, except those listed
    /// in <see cref="PendingRetry"/>. Null means nothing has been completed yet.
    /// </summary>
    public int? IssuesCompletedAbove { get; init; }

    /// <inheritdoc cref="IssuesCompletedAbove" />
    public int? PrsCompletedAbove { get; init; }

    /// <summary>
    /// True only when the issues phase enumerated to exhaustion, was not cancelled, and
    /// the returned count was below <c>BackfillLimit</c> (an equal count means the list
    /// was truncated and later items would never be reached).
    /// </summary>
    public bool IssuesPhaseComplete { get; init; }

    /// <inheritdoc cref="IssuesPhaseComplete" />
    public bool PrsPhaseComplete { get; init; }

    /// <summary>Item numbers whose detail fetch failed; re-attempted on the next pass.</summary>
    public int[] PendingRetry { get; init; } = [];

    /// <summary>Consecutive passes that did not shrink <see cref="PendingRetry"/>.</summary>
    public int StalledRepairPasses { get; init; }

    /// <summary>True when both phases are exhausted and nothing is awaiting repair.</summary>
    [JsonIgnore]
    public bool IsComplete => IssuesPhaseComplete && PrsPhaseComplete && PendingRetry.Length == 0;

    /// <summary>Serializes this cursor for the <c>LastCursor</c> column.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, CursorJsonContext.Default.GitHubBackfillCursor);

    /// <summary>
    /// Parses a persisted cursor. Returns <see langword="null"/> for null, blank, or
    /// malformed input and never throws — a corrupt cursor must degrade to "start from
    /// the top" rather than crash ingestion.
    /// </summary>
    public static GitHubBackfillCursor? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize(json, CursorJsonContext.Default.GitHubBackfillCursor);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(GitHubBackfillCursor))]
internal sealed partial class CursorJsonContext : JsonSerializerContext;
