namespace FhirAugury.Source.GitHub.Ingestion.Parsing;

/// <summary>Extracts searchable text content from a file.</summary>
public interface IFileContentParser
{
    /// <summary>Parser type identifier: "xml", "json", "markdown", "text", "code".</summary>
    string ParserType { get; }

    /// <summary>
    /// Extracts text content from the given file.
    /// Returns null if the file cannot be parsed or contains no useful text.
    /// </summary>
    string? ExtractText(string filePath, Stream content, int maxOutputLength);
}
