using FhirAugury.Cli.Dispatch;

namespace FhirAugury.Cli.Schemas;

/// <summary>
/// Generates JSON Schema descriptions for all CLI commands.
/// Used by --help, show-schemas, and save-schemas.
/// </summary>
public static class SchemaGenerator
{
    public static Dictionary<string, object> GenerateAll()
    {
        Dictionary<string, object> result = new()
        {
            ["input-schema"] = GenerateInputSchema(),
            ["output-schema"] = GenerateOutputSchema(),
        };

        foreach ((string name, CommandSchema schema) in CommandSchemas)
            result[$"commands/{name}"] = schema;

        return result;
    }

    public static Dictionary<string, object> GenerateForCommand(string commandName)
    {
        Dictionary<string, object> result = new()
        {
            ["input-schema"] = GenerateInputSchema(),
            ["output-schema"] = GenerateOutputSchema(),
        };

        if (CommandSchemas.TryGetValue(commandName, out CommandSchema? schema))
            result[$"commands/{commandName}"] = schema;

        return result;
    }

    public static string[] AvailableCommands => [.. CommandSchemas.Keys];

    private static object GenerateInputSchema() => new
    {
        schema = "https://json-schema.org/draft/2020-12/schema",
        title = "FhirAuguryCliInput",
        description = "Top-level input envelope for fhir-augury CLI",
        type = "object",
        required = new[] { "command" },
        properties = new Dictionary<string, object>
        {
            ["command"] = new
            {
                type = "string",
                enumValues = CommandDispatcher.KnownCommands,
                description = "The command to execute",
            },
            ["orchestrator"] = new
            {
                type = "string",
                description = "Orchestrator HTTP address",
                defaultValue = "http://localhost:5150",
            },
            ["verbose"] = new
            {
                type = "boolean",
                description = "Include timing metadata in response",
                defaultValue = false,
            },
        },
    };

    private static object GenerateOutputSchema() => new
    {
        schema = "https://json-schema.org/draft/2020-12/schema",
        title = "FhirAuguryCliOutput",
        description = "Output envelope for all fhir-augury CLI responses",
        type = "object",
        required = new[] { "success", "command" },
        properties = new Dictionary<string, object>
        {
            ["success"] = new { type = "boolean" },
            ["command"] = new { type = "string" },
            ["data"] = new { description = "Command-specific response payload (present on success)" },
            ["error"] = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["code"] = new { type = "string" },
                    ["message"] = new { type = "string" },
                    ["details"] = new { type = "string" },
                },
                description = "Error details (present on failure)",
            },
            ["metadata"] = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["elapsedMs"] = new { type = "number" },
                    ["orchestrator"] = new { type = "string" },
                    ["version"] = new { type = "string" },
                },
            },
            ["warnings"] = new
            {
                type = "array",
                items = new { type = "string" },
            },
        },
    };

    private static readonly Dictionary<string, CommandSchema> CommandSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search"] = new(
            "Unified search across all FHIR community sources",
            InputSchema(["command", "query"], new()
            {
                ["command"] = Const("search"),
                ["query"] = Prop("string", "Search query text"),
                ["sources"] = ArrayProp("string", "Source filter (default: all)"),
                ["limit"] = IntProp("Maximum results to return", 20),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["query"] = Prop("string", "The search query"),
                    ["totalResults"] = Prop("integer", "Total number of matches"),
                    ["results"] = ArrayProp("object", "Search result items"),
                },
            }
        ),

        ["get"] = new(
            "Get full details of an item",
            InputSchema(["command", "source", "id"], new()
            {
                ["command"] = Const("get"),
                ["source"] = Prop("string", "Source system (jira, zulip, confluence, github)"),
                ["id"] = Prop("string", "Item identifier"),
                ["includeComments"] = BoolProp("Include comments", true),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["source"] = Prop("string", "Source system"),
                    ["id"] = Prop("string", "Item identifier"),
                    ["title"] = Prop("string", "Item title"),
                    ["content"] = Prop("string", "Full item content"),
                    ["url"] = Prop("string", "Item URL"),
                    ["createdAt"] = Prop("string", "Creation timestamp (ISO 8601)"),
                    ["updatedAt"] = Prop("string", "Last update timestamp (ISO 8601)"),
                    ["metadata"] = Prop("object", "Key-value metadata"),
                    ["comments"] = ArrayProp("object", "Item comments"),
                },
            }
        ),

        ["snapshot"] = new(
            "Markdown snapshot of an item",
            InputSchema(["command", "source", "id"], new()
            {
                ["command"] = Const("snapshot"),
                ["source"] = Prop("string", "Source system"),
                ["id"] = Prop("string", "Item identifier"),
                ["includeComments"] = BoolProp("Include comments", true),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["id"] = Prop("string", "Item identifier"),
                    ["source"] = Prop("string", "Source system"),
                    ["markdown"] = Prop("string", "Rendered markdown content"),
                    ["url"] = Prop("string", "Item URL"),
                },
            }
        ),

        ["related"] = new(
            "Find related items across sources",
            InputSchema(["command", "source", "id"], new()
            {
                ["command"] = Const("related"),
                ["source"] = Prop("string", "Seed source system"),
                ["id"] = Prop("string", "Seed item identifier"),
                ["targetSources"] = ArrayProp("string", "Restrict target sources"),
                ["limit"] = IntProp("Maximum results to return", 20),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["seedSource"] = Prop("string", "Source of the seed item"),
                    ["seedId"] = Prop("string", "ID of the seed item"),
                    ["seedTitle"] = Prop("string", "Title of the seed item"),
                    ["items"] = ArrayProp("object", "Related items"),
                },
            }
        ),

        ["xref"] = new(
            "Get cross-references for an item",
            InputSchema(["command", "source", "id"], new()
            {
                ["command"] = Const("xref"),
                ["source"] = Prop("string", "Source system (jira, zulip, confluence, github, fhir)"),
                ["id"] = Prop("string", "Item identifier"),
                ["direction"] = new { type = "string", description = "Direction: outgoing, incoming, or both", defaultValue = "both" },
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["references"] = ArrayProp("object", "Cross-reference entries"),
                },
            }
        ),

        ["list"] = new(
            "List items from a source service",
            InputSchema(["command", "source"], new()
            {
                ["command"] = Const("list"),
                ["source"] = Prop("string", "Source system"),
                ["limit"] = IntProp("Maximum results to return", 20),
                ["sortBy"] = new { type = "string", description = "Sort by field", defaultValue = "updated_at" },
                ["sortOrder"] = new { type = "string", description = "Sort order: asc or desc", defaultValue = "desc" },
                ["filters"] = new { type = "object", description = "Key-value filters", additionalProperties = new { type = "string" } },
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["items"] = ArrayProp("object", "Listed items"),
                },
            }
        ),

        ["query-jira"] = new(
            "Structured Jira query with filters",
            InputSchema(["command"], new()
            {
                ["command"] = Const("query-jira"),
                ["query"] = Prop("string", "Text search query"),
                ["statuses"] = ArrayProp("string", "Filter by statuses"),
                ["workGroups"] = ArrayProp("string", "Filter by work groups"),
                ["specifications"] = ArrayProp("string", "Filter by specifications"),
                ["types"] = ArrayProp("string", "Filter by issue types"),
                ["priorities"] = ArrayProp("string", "Filter by priorities"),
                ["labels"] = ArrayProp("string", "Filter by labels"),
                ["assignees"] = ArrayProp("string", "Filter by assignees"),
                ["reporters"] = ArrayProp("string", "Filter by reporters"),
                ["sortBy"] = new { type = "string", description = "Sort by field", defaultValue = "updated_at" },
                ["sortOrder"] = new { type = "string", description = "Sort order", defaultValue = "desc" },
                ["limit"] = IntProp("Maximum results", 20),
                ["offset"] = IntProp("Pagination offset", 0),
                ["updatedAfter"] = Prop("string", "Only issues updated after this date (ISO 8601)"),
                ["updatedBefore"] = Prop("string", "Only issues updated before this date (ISO 8601)"),
                ["createdAfter"] = Prop("string", "Only issues created after this date (ISO 8601)"),
                ["createdBefore"] = Prop("string", "Only issues created before this date (ISO 8601)"),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["results"] = ArrayProp("object", "Matching Jira issues"),
                },
            }
        ),

        ["query-zulip"] = new(
            "Structured Zulip query with filters",
            InputSchema(["command"], new()
            {
                ["command"] = Const("query-zulip"),
                ["query"] = Prop("string", "Text search query"),
                ["streams"] = ArrayProp("string", "Filter by stream names"),
                ["topic"] = Prop("string", "Exact topic name match"),
                ["topicKeyword"] = Prop("string", "Partial topic match"),
                ["senders"] = ArrayProp("string", "Filter by sender names"),
                ["sortBy"] = new { type = "string", description = "Sort by field", defaultValue = "timestamp" },
                ["sortOrder"] = new { type = "string", description = "Sort order", defaultValue = "desc" },
                ["limit"] = IntProp("Maximum results", 20),
                ["after"] = Prop("string", "Only messages after this date (ISO 8601)"),
                ["before"] = Prop("string", "Only messages before this date (ISO 8601)"),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["results"] = ArrayProp("object", "Matching Zulip messages"),
                },
            }
        ),

        ["list-jira-workgroups"] = new(
            "List all Jira work groups with issue counts and canonical HL7 fields (code, nameClean, definition, retired)",
            InputSchema(["command"], new()
            {
                ["command"] = Const("list-jira-workgroups"),
                ["limit"] = IntProp("Maximum results to return", 0),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["dimension"] = Prop("string", "The dimension name"),
                    ["items"] = new
                    {
                        type = "array",
                        description = "Work groups joined with the canonical HL7 catalog. "
                                    + "Canonical fields (code/nameClean/definition) are null for free-text Jira "
                                    + "work groups with no HL7 match.",
                        items = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["name"] = Prop("string", "Free-text work-group name as stored on jira_issues.WorkGroup"),
                                ["code"] = Prop("string", "Canonical HL7 work-group code (e.g. 'oo', 'fhir')"),
                                ["nameClean"] = Prop("string", "PascalCase slug derived by Hl7WorkGroupNameCleaner; safe for URLs and folder names"),
                                ["definition"] = Prop("string", "HL7 work-group definition text"),
                                ["retired"] = Prop("boolean", "True if the HL7 work group is retired"),
                                ["issueCount"] = Prop("integer", "Total Jira issue count attributed to this work group"),
                            },
                        },
                    },
                },
            }
        ),

        ["list-jira-specifications"] = new(
            "List all Jira specifications with issue counts",
            InputSchema(["command"], new()
            {
                ["command"] = Const("list-jira-specifications"),
                ["limit"] = IntProp("Maximum results to return", 0),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["dimension"] = Prop("string", "The dimension name"),
                    ["items"] = ArrayProp("object", "Specifications with name and issueCount"),
                },
            }
        ),

        ["list-jira-labels"] = new(
            "List all Jira labels with issue counts",
            InputSchema(["command"], new()
            {
                ["command"] = Const("list-jira-labels"),
                ["limit"] = IntProp("Maximum results to return", 0),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["dimension"] = Prop("string", "The dimension name"),
                    ["items"] = ArrayProp("object", "Labels with name and issueCount"),
                },
            }
        ),

        ["list-jira-statuses"] = new(
            "List all Jira statuses with issue counts",
            InputSchema(["command"], new()
            {
                ["command"] = Const("list-jira-statuses"),
                ["limit"] = IntProp("Maximum results to return", 0),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["dimension"] = Prop("string", "The dimension name"),
                    ["items"] = ArrayProp("object", "Statuses with name and issueCount"),
                },
            }
        ),

        ["ingest"] = new(
            "Ingestion management (trigger, status, reingest, reindex)",
            InputSchema(["command", "action"], new()
            {
                ["command"] = Const("ingest"),
                ["action"] = new { type = "string", description = "Action: trigger, status, reingest, reindex", enumValues = new[] { "trigger", "status", "reingest", "reindex" } },
                ["sources"] = ArrayProp("string", "Source filter (default: all)"),
                ["type"] = new { type = "string", description = "Sync type (for trigger)", defaultValue = "incremental" },
                ["indexType"] = new { type = "string", description = "Index type (for reindex action)", defaultValue = "all" },
                ["jiraProject"] = new { type = "string", description = "Restrict a Jira run to a single project key (forwarded only to the Jira leg of the fan-out; surfaced over HTTP as ?jira-project=)" },
            }),
            new
            {
                type = "object",
                description = "Response varies by action (statuses, services, or results)",
            }
        ),

        ["services"] = new(
            "Service management (status, stats)",
            InputSchema(["command", "action"], new()
            {
                ["command"] = Const("services"),
                ["action"] = new { type = "string", description = "Action: status or stats", enumValues = new[] { "status", "stats" } },
            }),
            new
            {
                type = "object",
                description = "Response varies by action (services or sources)",
            }
        ),

        ["version"] = new(
            "Show CLI version",
            InputSchema(["command"], new()
            {
                ["command"] = Const("version"),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["version"] = Prop("string", "CLI version string"),
                },
            }
        ),

        ["show-schemas"] = new(
            "Output JSON schemas for all commands",
            InputSchema(["command"], new()
            {
                ["command"] = Const("show-schemas"),
            }),
            new { description = "Full schema catalog (input-schema, output-schema, and per-command schemas)" }
        ),

        ["save-schemas"] = new(
            "Save JSON schemas to a directory on disk",
            InputSchema(["command", "outputDirectory"], new()
            {
                ["command"] = Const("save-schemas"),
                ["outputDirectory"] = Prop("string", "Directory to write schema files into"),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["outputDirectory"] = Prop("string", "Directory where files were written"),
                    ["filesWritten"] = ArrayProp("string", "List of files written"),
                },
            }
        ),

        ["keywords"] = new(
            "Get extracted keywords for an item, sorted by BM25 score",
            InputSchema(["command", "source", "id"], new()
            {
                ["command"] = Const("keywords"),
                ["source"] = Prop("string", "Source system (e.g., github, jira, zulip, confluence)"),
                ["id"] = Prop("string", "Item ID within the source"),
                ["keywordType"] = Prop("string", "Filter by keyword type: word, fhir_path, fhir_operation"),
                ["limit"] = IntProp("Maximum keywords to return", 50),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["source"] = Prop("string", "Source system"),
                    ["sourceId"] = Prop("string", "Item ID"),
                    ["contentType"] = Prop("string", "Content type"),
                    ["total"] = Prop("integer", "Total keyword count"),
                    ["keywords"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object>
                            {
                                ["keyword"] = Prop("string", "The keyword text"),
                                ["keywordType"] = Prop("string", "Keyword classification"),
                                ["count"] = Prop("integer", "Occurrence count"),
                                ["bm25Score"] = Prop("number", "BM25 relevance score"),
                            },
                        },
                        ["description"] = "List of extracted keywords",
                    },
                },
            }
        ),

        ["related-by-keyword"] = new(
            "Find items related to a given item by shared keyword similarity",
            InputSchema(["command", "source", "id"], new()
            {
                ["command"] = Const("related-by-keyword"),
                ["source"] = Prop("string", "Source system (e.g., github, jira, zulip, confluence)"),
                ["id"] = Prop("string", "Item ID within the source"),
                ["minScore"] = Prop("number", "Minimum similarity score threshold (default 0.1)"),
                ["keywordType"] = Prop("string", "Restrict to a specific keyword type"),
                ["limit"] = IntProp("Maximum related items", 20),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["source"] = Prop("string", "Source system"),
                    ["sourceId"] = Prop("string", "Item ID"),
                    ["total"] = Prop("integer", "Number of related items"),
                    ["relatedItems"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object>
                            {
                                ["source"] = Prop("string", "Source system of related item"),
                                ["sourceId"] = Prop("string", "Related item ID"),
                                ["contentType"] = Prop("string", "Content type"),
                                ["title"] = Prop("string", "Related item title"),
                                ["score"] = Prop("number", "Similarity score"),
                                ["sharedKeywords"] = new Dictionary<string, object>
                                {
                                    ["type"] = "array",
                                    ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                                    ["description"] = "Keywords shared between the items",
                                },
                            },
                        },
                        ["description"] = "List of related items",
                    },
                },
            }
        ),

        ["sources"] = new(
            "List enabled source services known to the orchestrator",
            InputSchema(["command"], new()
            {
                ["command"] = Const("sources"),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["sources"] = ArrayProp("string", "Enabled source service names"),
                },
            }
        ),

        ["commands"] = new(
            "List operations available across the orchestrator and all enabled sources, derived from the merged OpenAPI document",
            InputSchema(["command"], new()
            {
                ["command"] = Const("commands"),
                ["source"] = Prop("string", "Filter to a single source (e.g., jira, zulip, orchestrator)"),
                ["tag"] = Prop("string", "Filter to operations whose tag equals or starts with this value"),
                ["refresh"] = BoolProp("Force a refresh of the cached OpenAPI document", false),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["commands"] = ArrayProp("object", "Discovered operations with operationId, method, path, summary, source, tags"),
                },
            }
        ),

        ["schema"] = new(
            "Get the request and response JSON schemas for a single operation",
            InputSchema(["command", "source", "operation"], new()
            {
                ["command"] = Const("schema"),
                ["source"] = Prop("string", "Source name; combined with operation to form operationId '{source}.{operation}'"),
                ["operation"] = Prop("string", "Operation name (the part after the source prefix in operationId)"),
                ["refresh"] = BoolProp("Force a refresh of the cached OpenAPI document", false),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["operationId"] = Prop("string", "Resolved operationId"),
                    ["method"] = Prop("string", "HTTP method"),
                    ["path"] = Prop("string", "URL path template"),
                    ["summary"] = Prop("string", "Operation summary"),
                    ["parameters"] = ArrayProp("object", "Path/query/header parameters with name, in, required, schema"),
                    ["requestBody"] = Prop("object", "Request body schema (or null)"),
                    ["response"] = Prop("object", "First successful (2xx) JSON response schema (or null)"),
                },
            }
        ),

        ["call"] = new(
            "Invoke any operation discovered via the merged OpenAPI document",
            InputSchema(["command", "source", "operation"], new()
            {
                ["command"] = Const("call"),
                ["source"] = Prop("string", "Source name; combined with operation to resolve operationId '{source}.{operation}'"),
                ["operation"] = Prop("string", "Operation name (operationId tail)"),
                ["params"] = Prop("object", "Path/query/header parameter values keyed by parameter name"),
                ["body"] = Prop("object", "Request body. May be a JSON value, '@/path/to/file.json', or '@-' for stdin"),
                ["refresh"] = BoolProp("Force a refresh of the cached OpenAPI document", false),
                ["raw"] = BoolProp("Return the response body unwrapped", false),
                ["timeoutSeconds"] = IntProp("HTTP timeout in seconds", 60),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["httpStatus"] = Prop("integer", "HTTP status code"),
                    ["contentType"] = Prop("string", "Response content type"),
                    ["data"] = Prop("object", "Parsed JSON response body (when application/json)"),
                    ["bodyText"] = Prop("string", "Raw response body (when not JSON)"),
                },
            }
        ),
        ["prepared-ticket-write"] = new(
            "Persist structured ticket-prep output into a preparer SQLite database",
            InputSchema(["command", "dbPath", "payload"], new()
            {
                ["command"] = Const("prepared-ticket-write"),
                ["dbPath"] = Prop("string", "Path to the preparer SQLite database"),
                ["payload"] = Prop("object", "PreparedTicketPayload with structured ticket-prep fields"),
            }),
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["key"] = Prop("string", "Jira ticket key written"),
                    ["preparedTicketRows"] = Prop("integer", "Parent rows written"),
                    ["repoRows"] = Prop("integer", "Related repo rows written"),
                    ["relatedJiraRows"] = Prop("integer", "Related Jira rows written"),
                    ["relatedZulipRows"] = Prop("integer", "Related Zulip rows written"),
                    ["relatedGitHubRows"] = Prop("integer", "Related GitHub rows written"),
                },
            }
        ),
    };

    // Schema builder helpers
    private static object InputSchema(string[] required, Dictionary<string, object> properties) =>
        new { type = "object", required, properties };

    private static object Const(string value) =>
        new { constValue = value };

    private static object Prop(string type, string description) =>
        new { type, description };

    private static object ArrayProp(string itemType, string description) =>
        new { type = "array", items = new { type = itemType }, description };

    private static object IntProp(string description, int defaultValue) =>
        new { type = "integer", description, defaultValue };

    private static object BoolProp(string description, bool defaultValue) =>
        new { type = "boolean", description, defaultValue };

    public sealed record CommandSchema(string Description, object InputSchema, object OutputSchema);
}

