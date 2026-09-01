using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Confluence.Database.Records;

/// <summary>An attachment on a Confluence page.</summary>
/// <remarks>
/// The row exists whether or not the bytes were downloaded. An attachment
/// excluded by <c>AttachmentMaxBytes</c> still records its size and download
/// URL with a null <see cref="CacheKey"/>, so it stays discoverable and
/// fetchable by hand.
/// </remarks>
[LdgSQLiteTable("confluence_attachments")]
[LdgSQLiteIndex(nameof(PageId))]
[LdgSQLiteIndex(nameof(ConfluencePageId))]
public partial record class ConfluenceAttachmentRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required int PageId { get; set; }
    public required string ConfluencePageId { get; set; }

    [LdgSQLiteUnique]
    public required string ConfluenceAttachmentId { get; set; }

    public required string FileName { get; set; }
    public required string? MediaType { get; set; }
    public required long? FileSizeBytes { get; set; }
    public required int VersionNumber { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string? DownloadUrl { get; set; }

    /// <summary>Cache key of the downloaded bytes; null when they were skipped by policy or not yet fetched.</summary>
    public required string? CacheKey { get; set; }
}
