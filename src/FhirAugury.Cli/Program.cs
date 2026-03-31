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

        // Serialize and output
        JsonSerializerOptions opts = pretty ? PrettyJson : CompactJson;
        string output = JsonSerializer.Serialize(result, opts);
        await WriteOutputAsync(output, outputFile);

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
                ["usage"] = "fhir-augury --json '{\"command\":\"<name>\", ...}' [--pretty] [--output <file>]\n"
                          + "fhir-augury --input <file> [--pretty] [--output <file>]\n"
                          + "fhir-augury --help [command] [--pretty] [--output <file>]\n"
                          + "fhir-augury --json @file.json [--pretty] [--output <file>]\n"
                          + "fhir-augury --json @- [--pretty] [--output <file>]",
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

    private static async Task WriteOutputAsync(string output, string? outputFile)
    {
        if (outputFile is not null)
        {
            await File.WriteAllTextAsync(outputFile, output);
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

        if (outputFile is not null)
        {
            await File.WriteAllTextAsync(outputFile, output);
        }
        else
        {
            await Console.Error.WriteLineAsync(output);
        }

        return 1;
    }
}
