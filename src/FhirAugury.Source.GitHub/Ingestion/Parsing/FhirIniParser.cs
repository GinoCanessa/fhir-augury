namespace FhirAugury.Source.GitHub.Ingestion.Parsing;

/// <summary>
/// Represents a FHIR artifact extracted from a fhir.ini section.
/// </summary>
public record ArtifactEntry(
    string Name,
    string DirectoryKey,
    string Category,
    HashSet<string> Modifiers);

/// <summary>
/// Parses a fhir.ini file and builds a unified artifact registry.
/// Handles [types], [infrastructure], [resources], [draft-resources],
/// [logical], [removed-resources], and [resource-infrastructure] sections.
/// </summary>
public class FhirIniParser
{
    private static readonly HashSet<string> KnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "types", "infrastructure", "resources", "draft-resources",
        "logical", "removed-resources", "resource-infrastructure"
    };

    /// <summary>
    /// Parses the given fhir.ini file and returns a unified list of artifact entries.
    /// Entries from multiple sections are merged by directory key.
    /// </summary>
    public List<ArtifactEntry> Parse(string iniFilePath)
    {
        string[] lines = File.ReadAllLines(iniFilePath);
        return ParseLines(lines);
    }

    /// <summary>
    /// Parses INI content from lines (for testability).
    /// </summary>
    public List<ArtifactEntry> ParseLines(string[] lines)
    {
        // Collect raw entries per section
        Dictionary<string, List<RawEntry>> sectionEntries = [];
        string? currentSection = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (string.IsNullOrEmpty(line) || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string sectionName = line[1..^1].Trim();
                currentSection = KnownSections.Contains(sectionName) ? sectionName.ToLowerInvariant() : null;
                continue;
            }

            if (currentSection is null)
                continue;

            string? key = null;
            string? value = null;

            int eqIndex = line.IndexOf('=');
            if (eqIndex >= 0)
            {
                key = line[..eqIndex].Trim();
                value = line[(eqIndex + 1)..].Trim();
            }
            else
            {
                key = line;
            }

            if (!sectionEntries.TryGetValue(currentSection, out List<RawEntry>? entries))
            {
                entries = [];
                sectionEntries[currentSection] = entries;
            }

            entries.Add(new RawEntry(key, value));
        }

        // Build unified artifact registry, merging by directory key
        Dictionary<string, ArtifactEntry> registry = new(StringComparer.OrdinalIgnoreCase);

        ProcessTypes(sectionEntries, registry);
        ProcessInfrastructure(sectionEntries, registry);
        ProcessResources(sectionEntries, registry);
        ProcessResourceInfrastructure(sectionEntries, registry);
        ProcessLogical(sectionEntries, registry);
        ProcessDraftResources(sectionEntries, registry);
        ProcessRemovedResources(sectionEntries, registry);

        return [.. registry.Values];
    }

    private static void ProcessTypes(Dictionary<string, List<RawEntry>> sections, Dictionary<string, ArtifactEntry> registry)
    {
        if (!sections.TryGetValue("types", out List<RawEntry>? entries))
            return;

        foreach (RawEntry entry in entries)
        {
            string name = entry.Key;
            string dirKey = name.ToLowerInvariant();

            if (!registry.ContainsKey(dirKey))
                registry[dirKey] = new ArtifactEntry(name, dirKey, "type", []);
        }
    }

    private static void ProcessInfrastructure(Dictionary<string, List<RawEntry>> sections, Dictionary<string, ArtifactEntry> registry)
    {
        if (!sections.TryGetValue("infrastructure", out List<RawEntry>? entries))
            return;

        foreach (RawEntry entry in entries)
        {
            string name = entry.Key;
            string dirKey = name.ToLowerInvariant();

            // Infrastructure items keep infrastructure category only (no type tag)
            registry[dirKey] = new ArtifactEntry(name, dirKey, "infrastructure", []);
        }
    }

    private static void ProcessResources(Dictionary<string, List<RawEntry>> sections, Dictionary<string, ArtifactEntry> registry)
    {
        if (!sections.TryGetValue("resources", out List<RawEntry>? entries))
            return;

        foreach (RawEntry entry in entries)
        {
            // Format: lowercase-key=ProperName
            string dirKey = entry.Key.ToLowerInvariant();
            string name = entry.Value ?? entry.Key;

            registry[dirKey] = new ArtifactEntry(name, dirKey, "resource", []);
        }
    }

    private static void ProcessResourceInfrastructure(Dictionary<string, List<RawEntry>> sections, Dictionary<string, ArtifactEntry> registry)
    {
        if (!sections.TryGetValue("resource-infrastructure", out List<RawEntry>? entries))
            return;

        foreach (RawEntry entry in entries)
        {
            // Format: lowercase-key=modifier,ProperName
            string dirKey = entry.Key.ToLowerInvariant();

            if (entry.Value is not null)
            {
                string[] parts = entry.Value.Split(',', 2);
                string name = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim();
                // Modifiers (abstract/interface/concrete) deferred to future iteration
                registry[dirKey] = new ArtifactEntry(name, dirKey, "resource", []);
            }
            else
            {
                registry[dirKey] = new ArtifactEntry(entry.Key, dirKey, "resource", []);
            }
        }
    }

    private static void ProcessLogical(Dictionary<string, List<RawEntry>> sections, Dictionary<string, ArtifactEntry> registry)
    {
        if (!sections.TryGetValue("logical", out List<RawEntry>? entries))
            return;

        foreach (RawEntry entry in entries)
        {
            string name = entry.Key;
            string dirKey = name.ToLowerInvariant();

            if (!registry.ContainsKey(dirKey))
                registry[dirKey] = new ArtifactEntry(name, dirKey, "logical-model", []);
        }
    }

    private static void ProcessDraftResources(Dictionary<string, List<RawEntry>> sections, Dictionary<string, ArtifactEntry> registry)
    {
        if (!sections.TryGetValue("draft-resources", out List<RawEntry>? entries))
            return;

        foreach (RawEntry entry in entries)
        {
            // Format: ProperName=1
            string name = entry.Key;
            string dirKey = name.ToLowerInvariant();

            if (registry.TryGetValue(dirKey, out ArtifactEntry? existing))
            {
                // Merge: add draft modifier to existing entry
                existing.Modifiers.Add("draft");
            }
            else
            {
                // Standalone draft resource (not in [resources])
                registry[dirKey] = new ArtifactEntry(name, dirKey, "resource", ["draft"]);
            }
        }
    }

    private static void ProcessRemovedResources(Dictionary<string, List<RawEntry>> sections, Dictionary<string, ArtifactEntry> registry)
    {
        if (!sections.TryGetValue("removed-resources", out List<RawEntry>? entries))
            return;

        foreach (RawEntry entry in entries)
        {
            string name = entry.Key;
            string dirKey = name.ToLowerInvariant();

            if (registry.TryGetValue(dirKey, out ArtifactEntry? existing))
            {
                existing.Modifiers.Add("removed");
            }
            else
            {
                registry[dirKey] = new ArtifactEntry(name, dirKey, "resource", ["removed"]);
            }
        }
    }

    private record RawEntry(string Key, string? Value);
}
