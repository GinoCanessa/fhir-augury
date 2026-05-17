using System.Text.RegularExpressions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Hydration.Internal;

internal static partial class JiraKeys
{
    [GeneratedRegex(@"^[A-Z]+-\d+$", RegexOptions.Compiled)]
    private static partial Regex KeyRegex();

    public static Regex Pattern { get; } = KeyRegex();

    public static bool IsKey(string? value) => !string.IsNullOrEmpty(value) && Pattern.IsMatch(value);
}
