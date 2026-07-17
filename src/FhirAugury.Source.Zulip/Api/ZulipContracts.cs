namespace FhirAugury.Source.Zulip.Api;

/// <summary>Request body for updating a Zulip stream's mutable properties.</summary>
public record ZulipStreamUpdateRequest(bool IncludeStream, int? BaselineValue = null);

/// <summary>
/// Comment entry shape returned by <c>GET items/{id}/comments</c>. Zulip has
/// no first-class comment concept (every reply is itself a top-level message
/// in the same stream/topic), so this endpoint always returns an empty list;
/// the type exists purely for response-shape parity with the Jira equivalent
/// (<c>FhirAugury.Source.Jira.Api.JiraCommentEntry</c>).
/// </summary>
public record ZulipCommentEntry(string Id, string ItemId, string Author, string Body, DateTimeOffset CreatedAt);

/// <summary>
/// Link entry shape returned by <c>GET items/{id}/links</c>. Zulip messages
/// do not expose typed inter-item links, so this endpoint always returns an
/// empty list; the type exists purely for response-shape parity with the
/// Jira equivalent (<c>FhirAugury.Source.Jira.Api.JiraIssueLinkEntry</c>).
/// </summary>
public record ZulipItemLinkEntry(string SourceId, string TargetId, string LinkType);