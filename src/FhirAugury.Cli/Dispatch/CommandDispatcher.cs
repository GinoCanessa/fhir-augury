using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using FhirAugury.Cli.Dispatch.Handlers;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch;

/// <summary>
/// Parses a JSON command request, routes to the correct handler, and wraps the
/// result in an <see cref="OutputEnvelope"/>.
/// </summary>
public static class CommandDispatcher
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Dictionary<string, Func<JsonElement, CliRequest>> Parsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search"] = j => j.Deserialize<SearchRequest>(DeserializeOptions)!,
        ["get"] = j => j.Deserialize<GetRequest>(DeserializeOptions)!,
        ["refers-to"] = j => j.Deserialize<RefersToRequest>(DeserializeOptions)!,
        ["referred-by"] = j => j.Deserialize<ReferredByRequest>(DeserializeOptions)!,
        ["cross-referenced"] = j => j.Deserialize<CrossReferencedRequest>(DeserializeOptions)!,
        ["list"] = j => j.Deserialize<ListRequest>(DeserializeOptions)!,
        ["query-jira"] = j => j.Deserialize<QueryJiraRequest>(DeserializeOptions)!,
        ["query-zulip"] = j => j.Deserialize<QueryZulipRequest>(DeserializeOptions)!,
        ["ingest"] = j => j.Deserialize<IngestRequest>(DeserializeOptions)!,
        ["services"] = j => j.Deserialize<ServicesRequest>(DeserializeOptions)!,
        ["version"] = j => j.Deserialize<VersionRequest>(DeserializeOptions)!,
        ["show-schemas"] = j => j.Deserialize<ShowSchemasRequest>(DeserializeOptions)!,
        ["save-schemas"] = j => j.Deserialize<SaveSchemasRequest>(DeserializeOptions)!,
        ["keywords"] = j => j.Deserialize<KeywordsRequest>(DeserializeOptions)!,
        ["related-by-keyword"] = j => j.Deserialize<RelatedByKeywordRequest>(DeserializeOptions)!,
    };

    public static string[] KnownCommands => [.. Parsers.Keys];

    public static async Task<OutputEnvelope> ExecuteAsync(string json, CancellationToken ct = default)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException ex)
        {
            return OutputEnvelope.Fail("", "INVALID_JSON", "Failed to parse input JSON.", ex.Message);
        }

        if (!root.TryGetProperty("command", out JsonElement commandProp) &&
            !root.TryGetProperty("Command", out commandProp))
        {
            return OutputEnvelope.Fail("", "MISSING_COMMAND", "Input JSON must contain a \"command\" field.");
        }

        string commandName = commandProp.GetString() ?? "";
        if (!Parsers.TryGetValue(commandName, out Func<JsonElement, CliRequest>? parser))
        {
            return OutputEnvelope.Fail(commandName, "UNKNOWN_COMMAND",
                $"Unknown command: {commandName}",
                $"Available commands: {string.Join(", ", Parsers.Keys)}");
        }

        CliRequest request;
        try
        {
            request = parser(root);
        }
        catch (JsonException ex)
        {
            return OutputEnvelope.Fail(commandName, "INVALID_REQUEST", "Failed to deserialize command request.", ex.Message);
        }

        string orchestratorAddr = request.Orchestrator
            ?? Environment.GetEnvironmentVariable("FHIR_AUGURY_ORCHESTRATOR")
            ?? "http://localhost:5150";

        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";

        Stopwatch? sw = request.Verbose ? Stopwatch.StartNew() : null;

        try
        {
            object data = await DispatchAsync(request, orchestratorAddr, ct);
            sw?.Stop();

            MetadataInfo metadata = new()
            {
                Orchestrator = orchestratorAddr,
                Version = version,
            };
            if (sw is not null)
                metadata.ElapsedMs = sw.ElapsedMilliseconds;

            List<string>? warnings = (data as IHasWarnings)?.TakeWarnings();

            object responseData = data is IHasWarnings hw ? hw.GetData() : data;
            return OutputEnvelope.Ok(commandName, responseData, metadata, warnings);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            return OutputEnvelope.Fail(commandName, "CONNECTION_FAILED",
                $"Cannot connect to orchestrator at {orchestratorAddr}",
                ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return OutputEnvelope.Fail(commandName, "HTTP_ERROR",
                $"HTTP request failed with status {ex.StatusCode}",
                ex.Message);
        }
        catch (ArgumentException ex)
        {
            return OutputEnvelope.Fail(commandName, "INVALID_ARGUMENT", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return OutputEnvelope.Fail(commandName, "INTERNAL_ERROR", ex.Message, ex.ToString());
        }
    }

    private static Task<object> DispatchAsync(CliRequest request, string orchestratorAddr, CancellationToken ct) =>
        request switch
        {
            SearchRequest r => SearchHandler.HandleAsync(r, orchestratorAddr, ct),
            GetRequest r => GetHandler.HandleAsync(r, orchestratorAddr, ct),
            RefersToRequest r => RefersToHandler.HandleAsync(r, orchestratorAddr, ct),
            ReferredByRequest r => ReferredByHandler.HandleAsync(r, orchestratorAddr, ct),
            CrossReferencedRequest r => CrossReferencedHandler.HandleAsync(r, orchestratorAddr, ct),
            ListRequest r => ListHandler.HandleAsync(r, orchestratorAddr, ct),
            QueryJiraRequest r => QueryJiraHandler.HandleAsync(r, orchestratorAddr, ct),
            QueryZulipRequest r => QueryZulipHandler.HandleAsync(r, orchestratorAddr, ct),
            IngestRequest r => IngestHandler.HandleAsync(r, orchestratorAddr, ct),
            ServicesRequest r => ServicesHandler.HandleAsync(r, orchestratorAddr, ct),
            VersionRequest => VersionHandler.HandleAsync(),
            ShowSchemasRequest => Task.FromResult<object>(Schemas.SchemaGenerator.GenerateAll()),
            SaveSchemasRequest r => SaveSchemasHandler.HandleAsync(r),
            KeywordsRequest r => KeywordsHandler.HandleAsync(r, orchestratorAddr, ct),
            RelatedByKeywordRequest r => RelatedByKeywordHandler.HandleAsync(r, orchestratorAddr, ct),
            _ => throw new InvalidOperationException($"No handler for {request.GetType().Name}"),
        };
}

/// <summary>
/// Marker interface for results that carry warnings alongside the main data.
/// </summary>
public interface IHasWarnings
{
    List<string>? TakeWarnings();
    object GetData();
}
