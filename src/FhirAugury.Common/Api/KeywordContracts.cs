namespace FhirAugury.Common.Api;

/// <summary>A single keyword extracted from an item, with its BM25 score.</summary>
public record KeywordEntry
{
    /// <summary>The keyword text (e.g., "patient", "Patient.identifier", "$validate").</summary>
    public required string Keyword { get; init; }

    /// <summary>Classification of the keyword: "word", "fhir_path", or "fhir_operation".</summary>
    public required string KeywordType { get; init; }

    /// <summary>Number of times the keyword appears in the item.</summary>
    public required int Count { get; init; }

    /// <summary>BM25 relevance score for this keyword in the item.</summary>
    public required double Bm25Score { get; init; }
}

/// <summary>Response containing extracted keywords for a specific item.</summary>
public record KeywordListResponse
{
    /// <summary>Source system that owns the item (e.g., "github", "jira").</summary>
    public required string Source { get; init; }

    /// <summary>Item ID within the source.</summary>
    public required string SourceId { get; init; }

    /// <summary>Content type of the item (e.g., "issue", "message", "page").</summary>
    public string ContentType { get; init; } = "";

    /// <summary>The extracted keywords, sorted by BM25 score descending.</summary>
    public required List<KeywordEntry> Keywords { get; init; }

    /// <summary>Warnings from partial failures.</summary>
    public List<string>? Warnings { get; init; }
}

/// <summary>An item related to a source item by shared keywords.</summary>
public record RelatedByKeywordItem
{
    /// <summary>Source system that owns the related item.</summary>
    public required string Source { get; init; }

    /// <summary>Item ID within the source.</summary>
    public required string SourceId { get; init; }

    /// <summary>Content type of the related item.</summary>
    public string ContentType { get; init; } = "";

    /// <summary>Title of the related item.</summary>
    public required string Title { get; init; }

    /// <summary>Similarity score based on shared keyword BM25 vectors.</summary>
    public required double Score { get; init; }

    /// <summary>Keywords shared between the source and related item.</summary>
    public required List<string> SharedKeywords { get; init; }
}

/// <summary>Response containing items related to a source item by keyword similarity.</summary>
public record RelatedByKeywordResponse
{
    /// <summary>Source system of the queried item.</summary>
    public required string Source { get; init; }

    /// <summary>Item ID that was queried.</summary>
    public required string SourceId { get; init; }

    /// <summary>Items related by keyword similarity, sorted by score descending.</summary>
    public required List<RelatedByKeywordItem> RelatedItems { get; init; }

    /// <summary>Warnings from partial failures.</summary>
    public List<string>? Warnings { get; init; }
}
