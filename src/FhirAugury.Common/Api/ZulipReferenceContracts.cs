using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAugury.Common.Api;

[JsonConverter(typeof(ZulipReferenceEnumConverter<ZulipReferenceKind>))]
public enum ZulipReferenceKind
{
    Message,
    Thread,
}

[JsonConverter(typeof(ZulipReferenceEnumConverter<ZulipReferenceLookupOutcome>))]
public enum ZulipReferenceLookupOutcome
{
    Resolved,
    InvalidReference,
    UnsupportedReference,
    NotFound,
    AmbiguousReference,
    InvalidSourceContext,
    SourceUnavailable,
    AuthenticationFailed,
    TransientFailure,
    Timeout,
    InvalidEnvelope,
    InvalidJson,
    HttpFailure,
}

/// <summary>Safe codes, never raw source values or response bodies.</summary>
[JsonConverter(typeof(ZulipReferenceEnumConverter<ZulipReferenceDiagnosticCode>))]
public enum ZulipReferenceDiagnosticCode
{
    InvalidTimestamp,
    MissingStreamContext,
    InvalidSourceUrl,
    MalformedOutcomeMetadata,
    UnknownOutcomeMetadataVersion,
}

/// <summary>
/// Source-owned resolution of an accepted message or legacy thread reference.
/// StreamId is the public Zulip stream ID, not a local database foreign key.
/// Nullable members permit consumers to distinguish an invalid envelope from
/// JSON that could not be parsed at all.
/// </summary>
public sealed record ZulipReferenceResolutionResponse
{
    public string? Reference { get; init; }
    public ZulipReferenceLookupOutcome? Outcome { get; init; }
    public ZulipReferenceKind? Kind { get; init; }
    public string? Url { get; init; }
    public int? MessageId { get; init; }
    public int? StreamId { get; init; }
    public string? StreamName { get; init; }
    public string? Topic { get; init; }
    public int? MessageCount { get; init; }
    public string? FirstMessageExcerpt { get; init; }
    public DateTimeOffset? FirstMessageAt { get; init; }
    public DateTimeOffset? LastMessageAt { get; init; }
    public IReadOnlyList<ZulipReferenceDiagnosticCode> Diagnostics { get; init; } = [];
}

public static class ZulipReferenceContract
{
    public static bool TryGetMessageId(string reference, out int messageId) =>
        int.TryParse(reference.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out messageId)
        && messageId > 0;

    public static bool IsCoherentResolution(ZulipReferenceResolutionResponse response, string reference)
    {
        if (!string.Equals(response.Reference, reference, StringComparison.Ordinal)
            || response.Outcome != ZulipReferenceLookupOutcome.Resolved
            || string.IsNullOrWhiteSpace(response.StreamName)
            || string.IsNullOrWhiteSpace(response.Topic)
            || response.StreamId is <= 0
            || response.MessageCount is <= 0
            || response.FirstMessageAt > response.LastMessageAt
            || response.Diagnostics is null
            || response.Diagnostics.Any(code => code != ZulipReferenceDiagnosticCode.InvalidTimestamp)
            || !IsSafeUrl(response.Url))
        {
            return false;
        }

        string fragment = $"#narrow/stream/{Uri.EscapeDataString(response.StreamName)}/topic/{Uri.EscapeDataString(response.Topic)}";
        switch (response.Kind)
        {
            case ZulipReferenceKind.Message:
                if (!TryGetMessageId(reference, out int messageId) || response.MessageId != messageId)
                    return false;
                fragment += $"/near/{messageId.ToString(CultureInfo.InvariantCulture)}";
                break;
            case ZulipReferenceKind.Thread:
                if (response.MessageId is not null
                    || !string.Equals(reference, $"{response.StreamName}:{response.Topic}", StringComparison.Ordinal))
                    return false;
                break;
            default:
                return false;
        }

        Uri url = new(response.Url!, UriKind.Absolute);
        return string.Equals(url.Fragment, fragment, StringComparison.Ordinal);
    }

    public static bool IsSafeUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Any(char.IsControl)
        && Uri.TryCreate(value, UriKind.Absolute, out Uri? url)
        && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrEmpty(url.Host)
        && string.IsNullOrEmpty(url.UserInfo)
        && string.IsNullOrEmpty(url.Query);

    public static int HttpStatus(ZulipReferenceLookupOutcome outcome) => outcome switch
    {
        ZulipReferenceLookupOutcome.Resolved => 200,
        ZulipReferenceLookupOutcome.InvalidReference or ZulipReferenceLookupOutcome.UnsupportedReference => 400,
        ZulipReferenceLookupOutcome.NotFound => 404,
        ZulipReferenceLookupOutcome.AmbiguousReference => 409,
        ZulipReferenceLookupOutcome.SourceUnavailable or ZulipReferenceLookupOutcome.InvalidSourceContext => 503,
        _ => 500,
    };
}

/// <summary>Backing survives failed lookups independently of the latest outcome.</summary>
[JsonConverter(typeof(ZulipReferenceEnumConverter<ZulipReferenceBacking>))]
public enum ZulipReferenceBacking
{
    None,
    Unverified,
    TypedResolver,
    LegacyIndexedContext,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ZulipReferenceHydrationOutcome
{
    public required ZulipReferenceBacking Backing { get; init; }
    public required ZulipReferenceLookupOutcome LatestOutcome { get; init; }
    public required IReadOnlyList<ZulipReferenceDiagnosticCode> Diagnostics { get; init; }
}

public sealed record ZulipReferenceHydrationReasonReadResult(
    ZulipReferenceHydrationOutcome? Metadata,
    string? LegacyReason,
    ZulipReferenceDiagnosticCode? MetadataFailure)
{
    public bool HasSourceBacking => MetadataFailure is null
        && Metadata?.Backing is ZulipReferenceBacking.TypedResolver or ZulipReferenceBacking.LegacyIndexedContext;
}

/// <summary>
/// Versioned metadata in the existing HydrationReason column. Legacy text is
/// readable but is not backing evidence. A tagged value must validate in full.
/// </summary>
public static class ZulipReferenceHydrationReason
{
    public const string Prefix = "zulip-reference-v1:";
    private const string ReservedPrefix = "zulip-reference-";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(ZulipReferenceHydrationOutcome metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!IsValid(metadata))
            throw new ArgumentException("Invalid Zulip reference outcome metadata.", nameof(metadata));

        return Prefix + JsonSerializer.Serialize(metadata, JsonOptions);
    }

    public static ZulipReferenceHydrationReasonReadResult Read(string? reason)
    {
        if (reason is null || !reason.StartsWith(ReservedPrefix, StringComparison.Ordinal))
            return new(null, reason, null);
        if (!reason.StartsWith(Prefix, StringComparison.Ordinal))
            return new(null, null, ZulipReferenceDiagnosticCode.UnknownOutcomeMetadataVersion);

        try
        {
            using JsonDocument document = JsonDocument.Parse(reason[Prefix.Length..]);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Malformed();

            HashSet<string> members = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty member in document.RootElement.EnumerateObject())
            {
                if (!members.Add(member.Name))
                    return Malformed();
            }

            ZulipReferenceHydrationOutcome? metadata =
                document.RootElement.Deserialize<ZulipReferenceHydrationOutcome>(JsonOptions);
            return metadata is not null && IsValid(metadata)
                ? new(metadata, null, null)
                : Malformed();
        }
        catch (JsonException)
        {
            return Malformed();
        }
    }

    private static bool IsValid(ZulipReferenceHydrationOutcome metadata) =>
        Enum.IsDefined(metadata.Backing)
        && Enum.IsDefined(metadata.LatestOutcome)
        && metadata.Diagnostics is not null
        && metadata.Diagnostics.All(Enum.IsDefined)
        && (metadata.LatestOutcome != ZulipReferenceLookupOutcome.Resolved
            || metadata.Backing is ZulipReferenceBacking.TypedResolver or ZulipReferenceBacking.LegacyIndexedContext);

    private static ZulipReferenceHydrationReasonReadResult Malformed() =>
        new(null, null, ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata);
}

internal sealed class ZulipReferenceEnumConverter<T> : JsonStringEnumConverter<T> where T : struct, Enum
{
    public ZulipReferenceEnumConverter() : base(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) { }
}
