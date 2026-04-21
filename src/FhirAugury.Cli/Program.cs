using System.Text.Json;
using FhirAugury.Cli.Dispatch;
using FhirAugury.Cli.Models;
using FhirAugury.Cli.Schemas;

namespace FhirAugury.Cli;

public static class Program
{
    private static readonly JsonSerializerOptions CompactJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> Main(string[] args)
    {
        string? jsonInput = null;
        string? inputFile = null;
        string? outputFile = null;
        string? bodyInput = null;
        bool pretty = false;
        bool help = false;
        string? helpCommand = null;

        // Parse CLI arguments
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json" when i + 1 < args.Length:
                    jsonInput = args[++i];
                    break;
                case "--input" when i + 1 < args.Length:
                    inputFile = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    outputFile = args[++i];
                    break;
                case "--body" when i + 1 < args.Length:
                    bodyInput = args[++i];
                    break;
                case "--pretty":
                    pretty = true;
                    break;
                case "--help":
                    help = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        helpCommand = args[++i];
                    break;
                default:
                    return await WriteErrorAsync(
                        $"Unknown argument: {args[i]}. Use --help for usage.",
                        outputFile, pretty);
            }
        }

        // --help mode: output schemas directly (no envelope)
        if (help)
        {
            return await HandleHelpAsync(helpCommand, outputFile, pretty);
        }

        // Validate mutual exclusivity
        if (jsonInput is not null && inputFile is not null)
        {
            return await WriteErrorAsync(
                "--json and --input are mutually exclusive.", outputFile, pretty);
        }

        // Must have --json or --input
        if (jsonInput is null && inputFile is null)
        {
            return await WriteErrorAsync(
                "Provide --json or --input to execute a command, or --help for usage.", outputFile, pretty);
        }

        // Resolve JSON input
        string json;
        if (jsonInput is not null)
        {
            json = await ResolveJsonInputAsync(jsonInput);
        }
        else
        {
            if (!File.Exists(inputFile))
            {
                return await WriteErrorAsync($"Input file not found: {inputFile}", outputFile, pretty);
            }
            json = await File.ReadAllTextAsync(inputFile);
        }

        // Inject --body into the envelope, if supplied. The body string is
        // resolved with the same @file / @- / inline-JSON semantics as
        // --json, then parsed as a JSON value (not a string-wrapped
        // string) and merged under "body". Conflicts with an existing
        // envelope-supplied body are a usage error.
        if (bodyInput is not null)
        {
            try
            {
                json = await InjectBodyAsync(json, bodyInput);
            }
            catch (InvalidOperationException ex)
            {
                return await WriteErrorAsync(ex.Message, outputFile, pretty);
            }
            catch (FileNotFoundException ex)
            {
                return await WriteErrorAsync(ex.Message, outputFile, pretty);
            }
            catch (JsonException ex)
            {
                return await WriteErrorAsync(
                    $"--body did not parse as JSON: {ex.Message}", outputFile, pretty);
            }
        }

        // Execute command (with Ctrl+C cancellation support)
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        OutputEnvelope result;
        try
        {
            result = await CommandDispatcher.ExecuteAsync(json, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = OutputEnvelope.Fail("", "CANCELLED", "Operation was cancelled.");
        }

        // Serialize and output. On failure, write the envelope to stderr
        // when no --output was supplied so consumers piping stdout get a
        // clean signal.
        JsonSerializerOptions opts = pretty ? PrettyJson : CompactJson;
        string output = JsonSerializer.Serialize(result, opts);
        await WriteOutputAsync(output, outputFile, isError: !result.Success);

        return result.Success ? 0 : 1;
    }

    private static async Task<int> HandleHelpAsync(string? commandName, string? outputFile, bool pretty)
    {
        object helpData;

        if (commandName is not null)
        {
            // Validate command name
            if (!SchemaGenerator.AvailableCommands.Contains(commandName, StringComparer.OrdinalIgnoreCase))
            {
                helpData = new
                {
                    error = $"Unknown command: {commandName}",
                    availableCommands = CommandDispatcher.KnownCommands,
                };
                JsonSerializerOptions errOpts = pretty ? PrettyJson : CompactJson;
                await WriteOutputAsync(JsonSerializer.Serialize(helpData, errOpts), outputFile);
                return 1;
            }

            Dictionary<string, object> commandSchemas = SchemaGenerator.GenerateForCommand(commandName);
            string exampleJson = $"{{\"command\":\"{commandName}\", ...}}";
            helpData = new Dictionary<string, object>(commandSchemas)
            {
                ["usage"] = $"fhir-augury --json '{exampleJson}' [--pretty] [--output <file>]",
            };
        }
        else
        {
            Dictionary<string, object> allSchemas = SchemaGenerator.GenerateAll();
            helpData = new Dictionary<string, object>(allSchemas)
            {
                ["usage"] = string.Join('\n',
                [
                    "fhir-augury [INPUT] [OPTIONS]",
                    "",
                    "INPUT (one of):",
                    "  --json '<json>'           Inline request envelope.",
                    "  --json @file.json         Read the envelope from a file.",
                    "  --json @-                 Read the envelope from stdin.",
                    "  --input <file>            Read the envelope from a file.",
                    "",
                    "OPTIONS:",
                    "  --body <value|@file|@->   Inject a JSON body into the envelope under \"body\".",
                    "                            Resolves with the same @file / @- semantics as --json.",
                    "                            Errors if the envelope already supplies \"body\".",
                    "  --output <file>           Write the response envelope to <file> instead of stdout.",
                    "  --pretty                  Pretty-print JSON output.",
                    "  --help [command]          Show schemas for all commands or one specific command.",
                    "",
                    "OUTPUT:",
                    "  Success envelopes go to stdout (or --output).",
                    "  Failure envelopes go to stderr (or --output) with exit code 1.",
                ]),
            };
        }

        JsonSerializerOptions opts = pretty ? PrettyJson : CompactJson;
        await WriteOutputAsync(JsonSerializer.Serialize(helpData, opts), outputFile);
        return 0;
    }

    private static async Task<string> ResolveJsonInputAsync(string input)
    {
        if (input == "@-")
        {
            return await Console.In.ReadToEndAsync();
        }

        if (input.StartsWith('@'))
        {
            string filePath = input[1..];
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Input file not found: {filePath}");
            return await File.ReadAllTextAsync(filePath);
        }

        return input;
    }

    /// <summary>
    /// Injects a CLI-supplied <c>--body</c> value into the request envelope
    /// under the <c>"body"</c> property. The body string is resolved with
    /// the same <c>@file</c> / <c>@-</c> / inline-JSON semantics as
    /// <c>--json</c>, then parsed as a JSON value (not a quoted string) so
    /// it round-trips through the envelope without double-encoding.
    /// Throws <see cref="InvalidOperationException"/> when the envelope
    /// already carries a <c>body</c> property.
    /// </summary>
    internal static async Task<string> InjectBodyAsync(string envelopeJson, string bodyInput)
    {
        string bodyText = await ResolveJsonInputAsync(bodyInput);

        using JsonDocument bodyDoc = JsonDocument.Parse(bodyText);
        using JsonDocument envelopeDoc = JsonDocument.Parse(envelopeJson);
        if (envelopeDoc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "--body requires the request envelope to be a JSON object.");
        }

        Dictionary<string, JsonElement> merged = new(StringComparer.Ordinal);
        foreach (JsonProperty prop in envelopeDoc.RootElement.EnumerateObject())
        {
            if (string.Equals(prop.Name, "body", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "--body conflicts with a 'body' field already present in the envelope.");
            }
            merged[prop.Name] = prop.Value.Clone();
        }
        merged["body"] = bodyDoc.RootElement.Clone();

        return JsonSerializer.Serialize(merged, CompactJson);
    }

    private static async Task WriteOutputAsync(string output, string? outputFile, bool isError = false)
    {
        if (outputFile is not null)
        {
            await File.WriteAllTextAsync(outputFile, output);
            return;
        }

        if (isError)
        {
            await Console.Error.WriteLineAsync(output);
        }
        else
        {
            Console.WriteLine(output);
        }
    }

    private static async Task<int> WriteErrorAsync(string message, string? outputFile, bool pretty)
    {
        OutputEnvelope envelope = OutputEnvelope.Fail("", "USAGE_ERROR", message);
        JsonSerializerOptions opts = pretty ? PrettyJson : CompactJson;
        string output = JsonSerializer.Serialize(envelope, opts);

        await WriteOutputAsync(output, outputFile, isError: true);

        return 1;
    }
}
