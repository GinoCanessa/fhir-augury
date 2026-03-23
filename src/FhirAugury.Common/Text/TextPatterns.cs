using System.Text.RegularExpressions;

namespace FhirAugury.Common.Text;

/// <summary>
/// Shared regex patterns used by multiple text processing classes.
/// </summary>
public static partial class TextPatterns
{
    /// <summary>Matches markdown-style fenced code blocks.</summary>
    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Singleline)]
    public static partial Regex CodeBlockRegex();
}
