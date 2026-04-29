namespace FhirAugury.Common.Filtering;

/// <summary>
/// Helpers for Source service list filters that follow the null-as-default,
/// empty-as-explicit-all convention documented in docs/source-filter-conventions.md.
/// </summary>
public static class SourceFilterListExtensions
{
    /// <summary>
    /// Returns true when the caller omitted the list and the field's documented default behavior should apply.
    /// </summary>
    public static bool IsDefaultFilter(this IReadOnlyCollection<string>? values) => values is null;

    /// <summary>
    /// Returns true when the caller explicitly supplied an empty list, meaning no restriction for query filters.
    /// </summary>
    public static bool IsExplicitNoRestriction(this IReadOnlyCollection<string>? values) => values is { Count: 0 };

    /// <summary>
    /// Returns true when the caller supplied one or more values that should restrict the query or selection.
    /// </summary>
    public static bool HasExplicitRestriction(this IReadOnlyCollection<string>? values) => values is { Count: > 0 };

    /// <summary>
    /// Returns a safe enumerable view for consumers that need to enumerate without changing list-filter semantics.
    /// </summary>
    public static IReadOnlyCollection<string> OrEmpty(this IReadOnlyCollection<string>? values) => values ?? [];
}
