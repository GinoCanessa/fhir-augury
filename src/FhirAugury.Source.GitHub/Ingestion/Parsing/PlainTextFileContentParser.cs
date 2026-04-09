using System.Text;

namespace FhirAugury.Source.GitHub.Ingestion.Parsing;

/// <summary>
/// Reads raw UTF-8 text content. Covers both "text" and "code" parser types.
/// </summary>
public class PlainTextFileContentParser : IFileContentParser
{
    public string ParserType { get; }

    public PlainTextFileContentParser(string parserType = "text")
    {
        ParserType = parserType;
    }

    public string? ExtractText(string filePath, Stream content, int maxOutputLength)
    {
        using StreamReader reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        char[] buffer = new char[maxOutputLength];
        int read = reader.ReadBlock(buffer, 0, maxOutputLength);

        if (read == 0)
            return null;

        string result = new string(buffer, 0, read).Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
