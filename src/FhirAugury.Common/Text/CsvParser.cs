namespace FhirAugury.Common.Text;

/// <summary>
/// Shared helpers for parsing comma-separated source lists and other CSV tokens
/// used across CLI, MCP, and HTTP entry points.
/// </summary>
public static class CsvParser
{
    /// <summary>
    /// Splits a comma-separated string, trims whitespace, removes empties, and lowercases each token.
    /// Returns null if the input is null or whitespace-only.
    /// </summary>
    public static List<string>? ParseSourceList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return null;

        string[] items = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> result = new List<string>(items.Length);
        foreach (string item in items)
            result.Add(item.ToLowerInvariant());
        return result;
    }

    /// <summary>
    /// Adds each token from a comma-separated string into a list, lowercased.
    /// </summary>
    public static void AddToList(List<string> list, string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return;
        foreach (string item in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(item.ToLowerInvariant());
    }

    /// <summary>
    /// Adds each token from a comma-separated string into a list without case conversion.
    /// </summary>
    public static void AddItemsToList(List<string> list, string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return;
        foreach (string item in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(item);
    }
}
