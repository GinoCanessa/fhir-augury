using FhirAugury.Common.Api;

namespace FhirAugury.Source.Zulip.Api;

/// <summary>The existing thread content contract, including zero-message responses.</summary>
public sealed record ZulipThreadResponse(
    string Stream,
    int? StreamId,
    string Topic,
    int Total,
    string Url,
    int? MessageCount,
    DateTimeOffset? FirstMessageAt,
    DateTimeOffset? LastMessageAt,
    string? FirstMessageExcerpt,
    IReadOnlyList<ZulipThreadMessage> Messages,
    IReadOnlyList<ZulipReferenceDiagnosticCode> Diagnostics);

public sealed record ZulipThreadMessage(
    int Id,
    string Sender,
    string? Content,
    string? ContentHtml,
    DateTimeOffset? Timestamp,
    IReadOnlyList<ZulipReferenceDiagnosticCode> Diagnostics);
