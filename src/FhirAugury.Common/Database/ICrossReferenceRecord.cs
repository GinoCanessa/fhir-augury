namespace FhirAugury.Common.Database;

/// <summary>
/// Common fields shared by all cross-reference record types.
/// Each target type extends this with domain-specific fields.
/// </summary>
public interface ICrossReferenceRecord
{
    int Id { get; set; }
    string ContentType { get; set; }
    string SourceId { get; set; }
    string LinkType { get; set; }
    string? Context { get; set; }

    /// <summary>Target system identifier (e.g., "jira", "zulip", "github", "confluence", "fhir").</summary>
    string TargetType { get; }

    /// <summary>
    /// Canonical target item ID for protobuf mapping and deduplication.
    /// Derived from target-specific fields.
    /// </summary>
    string TargetId { get; }
}
