using System.Text.Json;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Database;

public static class ReplacementLineJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<string> lines) => JsonSerializer.Serialize(lines, Options);

    public static string[] Deserialize(string json) => JsonSerializer.Deserialize<string[]>(json, Options) ?? [];
}
