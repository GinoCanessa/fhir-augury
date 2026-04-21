using System.Text.Json;
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

    [JsonPropertyName("reporters")]
    public string[]? Reporters { get; set; }

    [JsonPropertyName("createdAfter")]
    public string? CreatedAfter { get; set; }

    [JsonPropertyName("createdBefore")]
    public string? CreatedBefore { get; set; }

    [JsonPropertyName("updatedBefore")]
    public string? UpdatedBefore { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }
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

    [JsonPropertyName("jiraProject")]
    public string? JiraProject { get; set; }
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

public sealed class ListJiraDimensionRequest : CliRequest
{
    [JsonPropertyName("dimension")]
    public string Dimension { get; set; } = "";

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public sealed class SourcesRequest : CliRequest;

public sealed class CommandsRequest : CliRequest
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("refresh")] public bool Refresh { get; set; }
}

public sealed class SchemaRequest : CliRequest
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("operation")] public string Operation { get; set; } = "";
    [JsonPropertyName("refresh")] public bool Refresh { get; set; }
}

public sealed class CallRequest : CliRequest
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("operation")] public string Operation { get; set; } = "";

    /// <summary>Path/query/header parameter values, keyed by parameter name as defined in the OpenAPI document.</summary>
    [JsonPropertyName("params")] public Dictionary<string, string>? Params { get; set; }

    /// <summary>Request body. May be a JSON object/array, or a string of the form "@/path/to/file.json" or "@-" for stdin.</summary>
    [JsonPropertyName("body")] public JsonElement? Body { get; set; }

    [JsonPropertyName("refresh")] public bool Refresh { get; set; }
    [JsonPropertyName("raw")] public bool Raw { get; set; }
    [JsonPropertyName("timeoutSeconds")] public int? TimeoutSeconds { get; set; }
}

// ── Phase D new request types ────────────────────────────────────────────

public sealed class JiraItemsRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
    [JsonPropertyName("includeContent")] public bool? IncludeContent { get; set; }
    [JsonPropertyName("includeComments")] public bool? IncludeComments { get; set; }
    [JsonPropertyName("includeRefs")] public bool? IncludeRefs { get; set; }
    [JsonPropertyName("seedSource")] public string? SeedSource { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
}

public sealed class JiraDimensionRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("project")] public string? Project { get; set; }
    [JsonPropertyName("spec")] public string? Spec { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
}

public sealed class JiraWorkGroupRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("groupCode")] public string? GroupCode { get; set; }
}

public sealed class JiraProjectRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("body")] public JsonElement? Body { get; set; }
}

public sealed class JiraLocalProcessingRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("body")] public JsonElement? Body { get; set; }
}

public sealed class ZulipItemsRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
    [JsonPropertyName("includeContent")] public bool? IncludeContent { get; set; }
    [JsonPropertyName("seedSource")] public string? SeedSource { get; set; }
    [JsonPropertyName("seedId")] public string? SeedId { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
}

public sealed class ZulipMessagesRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("id")] public int? Id { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
}

public sealed class ZulipStreamsRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("zulipStreamId")] public int? ZulipStreamId { get; set; }
    [JsonPropertyName("streamName")] public string? StreamName { get; set; }
    [JsonPropertyName("body")] public JsonElement? Body { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
}

public sealed class ZulipThreadsRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("streamName")] public string? StreamName { get; set; }
    [JsonPropertyName("topic")] public string? Topic { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
}

public sealed class ConfluencePagesRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("pageId")] public string? PageId { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("spaceKey")] public string? SpaceKey { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
    [JsonPropertyName("direction")] public string? Direction { get; set; }
}

public sealed class ConfluenceItemsRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
    [JsonPropertyName("includeContent")] public bool? IncludeContent { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
}

public sealed class GitHubItemsRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
    [JsonPropertyName("includeContent")] public bool? IncludeContent { get; set; }
    [JsonPropertyName("includeComments")] public bool? IncludeComments { get; set; }
    [JsonPropertyName("includeRefs")] public bool? IncludeRefs { get; set; }
    [JsonPropertyName("seedSource")] public string? SeedSource { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
}

public sealed class GitHubReposRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("owner")] public string? Owner { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class JiraSpecsRequest : CliRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("specKey")] public string? SpecKey { get; set; }
    [JsonPropertyName("family")] public string? Family { get; set; }
    [JsonPropertyName("workgroup")] public string? Workgroup { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("artifactKey")] public string? ArtifactKey { get; set; }
    [JsonPropertyName("pageKey")] public string? PageKey { get; set; }
}
