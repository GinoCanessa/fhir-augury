using System.Text.Json.Serialization;

namespace FhirAugury.Cli.Models;

/// <summary>
/// Base envelope for all CLI JSON requests. The "command" field drives dispatch.
/// </summary>
public class CliRequest
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("orchestrator")]
    public string? Orchestrator { get; set; }

    [JsonPropertyName("verbose")]
    public bool Verbose { get; set; }
}

public sealed class SearchRequest : CliRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("values")]
    public List<string>? Values { get; set; }

    [JsonPropertyName("sources")]
    public string[]? Sources { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 20;
}

public sealed class GetRequest : CliRequest
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("includeContent")]
    public bool IncludeContent { get; set; }

    [JsonPropertyName("includeComments")]
    public bool IncludeComments { get; set; } = true;

    [JsonPropertyName("includeSnapshot")]
    public bool IncludeSnapshot { get; set; }
}

public sealed class ListRequest : CliRequest
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 20;

    [JsonPropertyName("sortBy")]
    public string SortBy { get; set; } = "updated_at";

    [JsonPropertyName("sortOrder")]
    public string SortOrder { get; set; } = "desc";

    [JsonPropertyName("filters")]
    public Dictionary<string, string>? Filters { get; set; }
}

public sealed class QueryJiraRequest : CliRequest
{
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("statuses")]
    public string[]? Statuses { get; set; }

    [JsonPropertyName("workGroups")]
    public string[]? WorkGroups { get; set; }

    [JsonPropertyName("specifications")]
    public string[]? Specifications { get; set; }

    [JsonPropertyName("types")]
    public string[]? Types { get; set; }

    [JsonPropertyName("priorities")]
    public string[]? Priorities { get; set; }

    [JsonPropertyName("labels")]
    public string[]? Labels { get; set; }

    [JsonPropertyName("assignees")]
    public string[]? Assignees { get; set; }

    [JsonPropertyName("sortBy")]
    public string SortBy { get; set; } = "updated_at";

    [JsonPropertyName("sortOrder")]
    public string SortOrder { get; set; } = "desc";

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 20;

    [JsonPropertyName("updatedAfter")]
    public string? UpdatedAfter { get; set; }
}

public sealed class QueryZulipRequest : CliRequest
{
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("streams")]
    public string[]? Streams { get; set; }

    [JsonPropertyName("topic")]
    public string? Topic { get; set; }

    [JsonPropertyName("topicKeyword")]
    public string? TopicKeyword { get; set; }

    [JsonPropertyName("senders")]
    public string[]? Senders { get; set; }

    [JsonPropertyName("sortBy")]
    public string SortBy { get; set; } = "timestamp";

    [JsonPropertyName("sortOrder")]
    public string SortOrder { get; set; } = "desc";

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 20;

    [JsonPropertyName("after")]
    public string? After { get; set; }

    [JsonPropertyName("before")]
    public string? Before { get; set; }
}

public sealed class IngestRequest : CliRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("sources")]
    public string[]? Sources { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "incremental";

    [JsonPropertyName("indexType")]
    public string IndexType { get; set; } = "all";
}

public sealed class ServicesRequest : CliRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";
}

public sealed class VersionRequest : CliRequest;

public sealed class ShowSchemasRequest : CliRequest;

public sealed class SaveSchemasRequest : CliRequest
{
    [JsonPropertyName("outputDirectory")]
    public string OutputDirectory { get; set; } = "";
}

public sealed class RefersToRequest : CliRequest
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("sourceType")]
    public string? SourceType { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public sealed class ReferredByRequest : CliRequest
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("sourceType")]
    public string? SourceType { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public sealed class CrossReferencedRequest : CliRequest
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("sourceType")]
    public string? SourceType { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public sealed class KeywordsRequest : CliRequest
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("keywordType")]
    public string? KeywordType { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public sealed class RelatedByKeywordRequest : CliRequest
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("minScore")]
    public double? MinScore { get; set; }

    [JsonPropertyName("keywordType")]
    public string? KeywordType { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}
