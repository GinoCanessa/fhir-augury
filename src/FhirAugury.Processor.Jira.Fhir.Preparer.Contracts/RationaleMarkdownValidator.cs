using System.Text.RegularExpressions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

/// <summary>
/// Pure validator for the inline-only Markdown subset allowed in Topic /
/// Linked-Ticket-Group rationale prose.
/// <para>
/// Allowed: plain text, inline code spans (<c>`…`</c>), emphasis
/// (<c>*…*</c> and <c>_…_</c>), and links (<c>[label](url)</c>). Newlines
/// are allowed only as a single <c>\n</c> separating paragraphs (no
/// <c>\r</c>, no triple-newline runs).
/// </para>
/// <para>
/// Disallowed: any line starting with <c>#</c>, <c>&gt;</c>, <c>-</c>,
/// <c>*</c>, or <c>&lt;digit&gt;.</c>; fenced code blocks
/// (triple-backtick); HTML tags (<c>&lt;…&gt;</c>); images
/// (<c>![…](…)</c>); URLs whose scheme is not <c>http</c>, <c>https</c>,
/// or <c>mailto</c>; payload length &gt; 4 KiB.
/// </para>
/// </summary>
internal static partial class RationaleMarkdownValidator
{
    public const int MaxLengthBytes = 4 * 1024;

    public static bool IsValid(string? value, out string? reason)
    {
        if (value is null)
        {
            reason = "Rationale must be non-null.";
            return false;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(value) > MaxLengthBytes)
        {
            reason = $"Rationale exceeds {MaxLengthBytes}-byte limit.";
            return false;
        }

        if (value.Contains('\r'))
        {
            reason = "Rationale must not contain carriage returns.";
            return false;
        }

        if (value.Contains("\n\n\n", StringComparison.Ordinal))
        {
            reason = "Rationale must not contain triple-newline runs.";
            return false;
        }

        if (FencedCodeRegex().IsMatch(value))
        {
            reason = "Rationale must not contain fenced code blocks.";
            return false;
        }

        if (HtmlTagRegex().IsMatch(value))
        {
            reason = "Rationale must not contain HTML tags.";
            return false;
        }

        if (ImageRegex().IsMatch(value))
        {
            reason = "Rationale must not contain images.";
            return false;
        }

        foreach (Match link in LinkRegex().Matches(value))
        {
            string url = link.Groups[1].Value.Trim();
            if (!IsAllowedScheme(url))
            {
                reason = $"Rationale link uses disallowed scheme: '{url}'.";
                return false;
            }
        }

        string[] lines = value.Split('\n');
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }

            char first = trimmed[0];
            if (first is '#' or '>' or '-' or '*')
            {
                reason = $"Rationale must not contain block-level Markdown (line starts with '{first}').";
                return false;
            }

            if (OrderedListLeadRegex().IsMatch(trimmed))
            {
                reason = "Rationale must not contain ordered-list lines.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public static bool IsValid(string? value) => IsValid(value, out _);

    private static bool IsAllowedScheme(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex("```", RegexOptions.CultureInvariant)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex("<[A-Za-z/!?][^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("!\\[[^\\]]*\\]\\([^)]*\\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImageRegex();

    [GeneratedRegex("(?<!!)\\[[^\\]]*\\]\\(([^)]*)\\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    [GeneratedRegex("^\\d+\\.\\s", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedListLeadRegex();
}
