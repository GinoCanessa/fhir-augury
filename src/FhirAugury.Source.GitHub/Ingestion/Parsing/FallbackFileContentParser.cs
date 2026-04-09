using System.Text;

namespace FhirAugury.Source.GitHub.Ingestion.Parsing;

/// <summary>
/// Fallback parser for unrecognized file extensions. Applies binary detection heuristic:
/// if the first 8KB contains &gt;10% non-printable characters, the file is classified as
/// binary and skipped. Otherwise, indexes as text.
/// </summary>
public class FallbackFileContentParser : IFileContentParser
{
    public string ParserType => "text";

    private const int BinaryDetectionSize = 8 * 1024;
    private const double BinaryThreshold = 0.10;

    public string? ExtractText(string filePath, Stream content, int maxOutputLength)
    {
        // Read the first chunk for binary detection
        byte[] detectionBuffer = new byte[BinaryDetectionSize];
        int bytesRead = content.Read(detectionBuffer, 0, BinaryDetectionSize);

        if (bytesRead == 0)
            return null;

        if (IsBinary(detectionBuffer.AsSpan(0, bytesRead)))
            return null;

        // Reset and read as text
        content.Position = 0;
        using StreamReader reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        char[] buffer = new char[maxOutputLength];
        int read = reader.ReadBlock(buffer, 0, maxOutputLength);

        if (read == 0)
            return null;

        string result = new string(buffer, 0, read).Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool IsBinary(ReadOnlySpan<byte> data)
    {
        int nonPrintable = 0;

        foreach (byte b in data)
        {
            // Allow tab, newline, carriage return, and printable ASCII
            if (b == 0x09 || b == 0x0A || b == 0x0D)
                continue;

            if (b >= 0x20 && b <= 0x7E)
                continue;

            // Allow UTF-8 continuation bytes (0x80-0xBF) and leading bytes (0xC0-0xF7)
            if (b >= 0x80 && b <= 0xF7)
                continue;

            nonPrintable++;
        }

        return (double)nonPrintable / data.Length > BinaryThreshold;
    }
}
