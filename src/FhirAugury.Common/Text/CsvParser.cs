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

        var items = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>(items.Length);
        foreach (var item in items)
            result.Add(item.ToLowerInvariant());
        return result;
    }

    /// <summary>
    /// Adds each token from a comma-separated string into a protobuf repeated field, lowercased.
    /// </summary>
    public static void AddToRepeatedField(Google.Protobuf.Collections.RepeatedField<string> field, string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return;
        foreach (var item in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            field.Add(item.ToLowerInvariant());
    }

    /// <summary>
    /// Adds each token from a comma-separated string into a protobuf repeated field without case conversion.
    /// </summary>
    public static void AddItemsToRepeatedField(Google.Protobuf.Collections.RepeatedField<string> field, string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return;
        foreach (var item in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            field.Add(item);
    }
}
