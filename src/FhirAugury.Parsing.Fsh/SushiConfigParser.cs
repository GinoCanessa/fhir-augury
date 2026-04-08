namespace FhirAugury.Parsing.Fsh;

/// <summary>
/// Parses sushi-config.yaml files for IG metadata.
/// Uses simple line-by-line parsing — no YAML library dependency.
/// </summary>
public static class SushiConfigParser
{
    public static SushiConfig? TryParse(string filePath)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            return TryParseContent(content);
        }
        catch
        {
            return null;
        }
    }

    public static SushiConfig? TryParseContent(string content)
    {
        try
        {
            string? id = null;
            string? canonical = null;
            string? name = null;
            string? title = null;
            string? fhirVersion = null;
            string? status = null;
            List<string> pathResource = [];
            List<string> additionalResource = [];

            string[] lines = content.Split('\n');
            string? currentSection = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');

                // Skip comments and empty lines
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    continue;

                // Detect top-level keys (no indentation)
                if (!char.IsWhiteSpace(line[0]) && line.Contains(':'))
                {
                    int colonIndex = line.IndexOf(':');
                    string key = line[..colonIndex].Trim();
                    string value = line[(colonIndex + 1)..].Trim();

                    currentSection = key;

                    switch (key)
                    {
                        case "id": id = value; break;
                        case "canonical": canonical = value; break;
                        case "name": name = value; break;
                        case "title": title = UnquoteYamlString(value); break;
                        case "fhirVersion" when !string.IsNullOrEmpty(value): fhirVersion = value; break;
                        case "status": status = value; break;
                        case "path-resource": pathResource = ParseInlineList(value); break;
                        case "additional-resource": additionalResource = ParseInlineList(value); break;
                    }
                }
                else if (line.TrimStart().StartsWith("- "))
                {
                    // List item under current section
                    string listValue = line.TrimStart()[2..].Trim();
                    switch (currentSection)
                    {
                        case "path-resource": pathResource.Add(listValue); break;
                        case "additional-resource": additionalResource.Add(listValue); break;
                        case "fhirVersion": fhirVersion ??= listValue; break;
                    }
                }
            }

            return new SushiConfig(id, canonical, name, title, fhirVersion, status, pathResource, additionalResource);
        }
        catch
        {
            return null;
        }
    }

    private static string UnquoteYamlString(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];
        return value;
    }

    private static List<string> ParseInlineList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        // Handle YAML inline list: [item1, item2]
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            return value[1..^1]
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        // Single value
        return [value];
    }
}
