using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAugury.Tools.PreparerSite;

internal sealed class MetaFilterSet
{
    [JsonPropertyName("filters")]
    public MetaFilters Filters { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }
}

internal sealed class MetaFilters
{
    [JsonPropertyName("spec")]
    public string? Spec { get; set; }

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("wg")]
    public string? Wg { get; set; }
}

internal static class OutputDirGuard
{
    public const string MarkerFileName = ".preparer-site.meta";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MetaFilterSet? TryReadExistingMarker(string outDir)
    {
        string markerPath = Path.Combine(outDir, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }
        try
        {
            string json = File.ReadAllText(markerPath);
            return JsonSerializer.Deserialize<MetaFilterSet>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void WriteMarker(string outDir, ResolvedFilters filters, DateTimeOffset createdAt)
    {
        MetaFilterSet payload = new()
        {
            Filters = new MetaFilters
            {
                Spec = filters.Specification,
                Project = filters.Project,
                Wg = filters.WorkGroup,
            },
            CreatedAt = createdAt.ToString("O"),
        };
        string json = JsonSerializer.Serialize(payload, WriteOptions);
        File.WriteAllText(Path.Combine(outDir, MarkerFileName), json);
    }

    public static bool FilterSetsMatch(MetaFilterSet? existing, ResolvedFilters incoming)
    {
        if (existing is null)
        {
            return false;
        }
        MetaFilters existingFilters = existing.Filters ?? new MetaFilters();
        return string.Equals(existingFilters.Spec, incoming.Specification, StringComparison.Ordinal)
            && string.Equals(existingFilters.Project, incoming.Project, StringComparison.Ordinal)
            && string.Equals(existingFilters.Wg, incoming.WorkGroup, StringComparison.Ordinal);
    }
}
