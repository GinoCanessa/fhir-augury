using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAugury.Cli.Models;

/// <summary>
/// Standard JSON output envelope for all CLI command responses.
/// </summary>
public sealed class OutputEnvelope
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErrorInfo? Error { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetadataInfo? Metadata { get; set; }

    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Warnings { get; set; }

    public static OutputEnvelope Ok(string command, object data, MetadataInfo? metadata = null, List<string>? warnings = null) =>
        new()
        {
            Success = true,
            Command = command,
            Data = data,
            Metadata = metadata,
            Warnings = warnings is { Count: > 0 } ? warnings : null,
        };

    public static OutputEnvelope Fail(string command, string code, string message, string? details = null, MetadataInfo? metadata = null) =>
        new()
        {
            Success = false,
            Command = command,
            Error = new ErrorInfo { Code = code, Message = message, Details = details },
            Metadata = metadata,
        };
}

public sealed class ErrorInfo
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; set; }
}

public sealed class MetadataInfo
{
    [JsonPropertyName("elapsedMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("orchestrator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Orchestrator { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }
}
