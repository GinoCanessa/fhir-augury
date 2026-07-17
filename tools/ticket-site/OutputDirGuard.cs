using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAugury.Tools.TicketSite;

internal sealed class MetaFilterSet
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

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
    public const string MarkerFileName = ".ticket-site.meta";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MetaFilterSet? TryReadExistingMarker(string subSiteOut)
    {
        string markerPath = Path.Combine(subSiteOut, MarkerFileName);
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

    public static void WriteMarker(string subSiteOut, string kind, ResolvedFilters filters, DateTimeOffset createdAt)
    {
        MetaFilterSet payload = new()
        {
            Kind = kind,
            Filters = new MetaFilters
            {
                Spec = filters.Specification,
                Project = filters.Project,
                Wg = filters.WorkGroup,
            },
            CreatedAt = createdAt.ToString("O"),
        };
        string json = JsonSerializer.Serialize(payload, WriteOptions);
        Directory.CreateDirectory(subSiteOut);
        File.WriteAllText(Path.Combine(subSiteOut, MarkerFileName), json);
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

    public static bool KindMatches(MetaFilterSet? existing, string incomingKind)
    {
        if (existing is null) return true; // first build into this folder
        return string.IsNullOrEmpty(existing.Kind) || string.Equals(existing.Kind, incomingKind, StringComparison.Ordinal);
    }
}
