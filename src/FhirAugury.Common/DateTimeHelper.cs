namespace FhirAugury.Common;

/// <summary>
/// Shared date/time parsing helpers used across source projects.
/// </summary>
public static class DateTimeHelper
{
    /// <summary>Parses a date string, returning MinValue if null/empty/invalid.</summary>
    public static DateTimeOffset ParseDate(string? value) =>
        string.IsNullOrEmpty(value) ? DateTimeOffset.MinValue
        : DateTimeOffset.TryParse(value, out DateTimeOffset dt) ? dt
        : DateTimeOffset.MinValue;

    /// <summary>Parses a nullable date string, returning null if null/empty/invalid.</summary>
    public static DateTimeOffset? ParseNullableDate(string? value) =>
        string.IsNullOrEmpty(value) ? null
        : DateTimeOffset.TryParse(value, out global::System.DateTimeOffset dt) ? dt
        : null;
}
