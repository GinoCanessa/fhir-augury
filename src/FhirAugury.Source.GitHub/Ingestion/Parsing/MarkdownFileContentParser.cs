using System.Text;
using System.Text.RegularExpressions;

namespace FhirAugury.Source.GitHub.Ingestion.Parsing;

/// <summary>
/// Reads markdown files as-is, stripping any embedded HTML tags.
/// </summary>
public partial class MarkdownFileContentParser : IFileContentParser
{
    public string ParserType => "markdown";

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagPattern();

    public string? ExtractText(string filePath, Stream content, int maxOutputLength)
    {
        using StreamReader reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        char[] buffer = new char[maxOutputLength];
        int read = reader.ReadBlock(buffer, 0, maxOutputLength);

        if (read == 0)
            return null;

        string text = new string(buffer, 0, read);

        // Strip embedded HTML tags
        text = HtmlTagPattern().Replace(text, " ");

        string result = text.Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
