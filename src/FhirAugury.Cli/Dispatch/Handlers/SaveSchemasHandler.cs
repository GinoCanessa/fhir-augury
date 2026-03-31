using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class SaveSchemasHandler
{
    public static Task<object> HandleAsync(SaveSchemasRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            throw new ArgumentException("outputDirectory is required for save-schemas command.");

        Dictionary<string, object> schemas = Schemas.SchemaGenerator.GenerateAll();
        string outputDir = request.OutputDirectory;

        List<string> filesWritten = [];

        // Write top-level schemas
        WriteSchemaFile(outputDir, "input-schema.json", schemas["input-schema"], filesWritten);
        WriteSchemaFile(outputDir, "output-schema.json", schemas["output-schema"], filesWritten);

        // Write per-command schemas
        string commandsDir = Path.Combine(outputDir, "commands");
        Directory.CreateDirectory(commandsDir);

        foreach ((string key, object value) in schemas)
        {
            if (key.StartsWith("commands/", StringComparison.Ordinal))
            {
                string fileName = key["commands/".Length..] + ".json";
                WriteSchemaFile(commandsDir, fileName, value, filesWritten);
            }
        }

        return Task.FromResult<object>(new
        {
            outputDirectory = outputDir,
            filesWritten,
        });
    }

    private static void WriteSchemaFile(string dir, string fileName, object content, List<string> filesWritten)
    {
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, fileName);
        string json = System.Text.Json.JsonSerializer.Serialize(content,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
        filesWritten.Add(fileName);
    }
}
