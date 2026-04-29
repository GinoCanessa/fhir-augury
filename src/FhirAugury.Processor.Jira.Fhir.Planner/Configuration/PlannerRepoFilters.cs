using System.Text.Json;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Configuration;

public static class PlannerRepoFilters
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly char[] ForbiddenCharacters = ['*', '?', '[', ']'];

    public static IEnumerable<string> Validate(PlannerOptions options)
    {
        if (options.RepoFilters is null)
        {
            yield break;
        }

        foreach (string? value in options.RepoFilters)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return "Processing:Planner:RepoFilters values must be non-empty owner/repo strings.";
                continue;
            }

            if (value.IndexOfAny(ForbiddenCharacters) >= 0)
            {
                yield return $"Processing:Planner:RepoFilters value '{value}' cannot contain wildcards or character classes.";
            }

            string[] parts = value.Split('/');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                yield return $"Processing:Planner:RepoFilters value '{value}' must have exactly one '/' with non-blank owner and repo segments.";
            }
        }
    }

    public static IReadOnlyList<string> Normalize(List<string>? repoFilters)
    {
        if (repoFilters is null || repoFilters.Count == 0)
        {
            return [];
        }

        Dictionary<string, string> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach (string value in repoFilters)
        {
            string trimmed = value.Trim();
            string[] parts = trimmed.Split('/');
            string canonical = $"{parts[0].Trim()}/{parts[1].Trim()}";
            normalized.TryAdd(canonical, canonical);
        }

        return normalized.Values.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string RenderJson(List<string>? repoFilters)
    {
        string json = JsonSerializer.Serialize(Normalize(repoFilters), JsonOptions);
        return json.Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    public static bool MatchesRepositoryFullName(string repoFullName, List<string>? repoFilters)
    {
        IReadOnlyList<string> normalized = Normalize(repoFilters);
        return normalized.Count == 0 || normalized.Contains(repoFullName, StringComparer.OrdinalIgnoreCase);
    }
}
