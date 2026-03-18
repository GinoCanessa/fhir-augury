using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Database.Records;

/// <summary>A cross-reference link between two items across sources.</summary>
[LdgSQLiteTable("xref_links")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(TargetType), nameof(TargetId))]
[LdgSQLiteIndex(nameof(LinkType))]
public partial record class CrossRefLinkRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>The source type (e.g., "jira", "zulip").</summary>
    public required string SourceType { get; set; }

    /// <summary>The source-specific identifier (e.g., "FHIR-43499").</summary>
    public required string SourceId { get; set; }

    /// <summary>The target type (e.g., "jira", "zulip", "github", "confluence").</summary>
    public required string TargetType { get; set; }

    /// <summary>The target-specific identifier (e.g., "FHIR-12345").</summary>
    public required string TargetId { get; set; }

    /// <summary>The type of link (e.g., "mention", "url").</summary>
    public required string LinkType { get; set; }

    /// <summary>Surrounding text context (~100 chars) where the reference was found.</summary>
    public required string? Context { get; set; }
}
