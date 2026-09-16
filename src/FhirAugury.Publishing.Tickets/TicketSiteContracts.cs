using System.Text.Json;
using FhirAugury.Processing.Client;

namespace FhirAugury.Publishing.Tickets;

public enum TicketSiteKind
{
    Discussion,
    Applying,
}

public sealed record TicketSiteFilters(
    string? Specification = null,
    string? Project = null,
    string? WorkGroup = null)
{
    public static TicketSiteFilters None { get; } = new();
}

public sealed record TicketSitePublishRequest(
    VerifiedAuthoringSnapshotPair SnapshotPair,
    TicketSiteKind SiteKind,
    string OutputRoot,
    string Title,
    TicketSiteFilters? Filters = null,
    bool Force = false);

public enum TicketSitePublishOutcome
{
    Published,
    Idempotent,
}

public sealed record TicketSitePublishResult(
    TicketSitePublishOutcome Outcome,
    TicketSiteManifest Manifest,
    string OutputRoot,
    string SiteOutputPath,
    TicketSiteFilters ResolvedFilters,
    IReadOnlyList<string> Warnings);

public enum TicketSitePublishFailure
{
    InvalidRequest,
    ServiceMismatch,
    SnapshotValidation,
    FilterValidation,
    Publication,
}

public sealed class TicketSitePublishException(
    TicketSitePublishFailure failure,
    string message,
    Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public TicketSitePublishFailure Failure { get; } = failure;
}

public interface ITicketSitePublisher
{
    Task<TicketSitePublishResult> PublishAsync(
        TicketSitePublishRequest request,
        CancellationToken ct = default);
}

internal sealed record TicketSiteEmbeddedDatabaseBuild(
    byte[] DatabaseBytes,
    long IncludedItemCount,
    IReadOnlyDictionary<string, long> TableCounts,
    TicketSitePresentation? DiscussionPresentation);

internal static class TicketSitePresentationJson
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static string Serialize(TicketSitePresentation presentation)
        => JsonSerializer.Serialize(presentation, SerializerOptions);

    public static string Serialize(DiscussionPublicationReadiness readiness)
        => JsonSerializer.Serialize(readiness, SerializerOptions);

    public static string Serialize(DiscussionCorpusSummary summary)
        => JsonSerializer.Serialize(summary, SerializerOptions);

    public static TicketSitePresentation Deserialize(string json)
        => JsonSerializer.Deserialize<TicketSitePresentation>(
            json,
            SerializerOptions)
        ?? throw new InvalidOperationException(
            "The discussion presentation payload is empty.");

    public static DiscussionPublicationReadiness DeserializeReadiness(
        string json)
        => JsonSerializer.Deserialize<DiscussionPublicationReadiness>(
            json,
            SerializerOptions)
        ?? throw new InvalidOperationException(
            "The discussion publication-readiness payload is empty.");

    public static DiscussionCorpusSummary DeserializeCorpusSummary(string json)
        => JsonSerializer.Deserialize<DiscussionCorpusSummary>(
            json,
            SerializerOptions)
        ?? throw new InvalidOperationException(
            "The discussion corpus-summary payload is empty.");
}
