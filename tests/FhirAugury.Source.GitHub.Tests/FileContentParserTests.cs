using System.Text;
using FhirAugury.Source.GitHub.Ingestion.Parsing;

namespace FhirAugury.Source.GitHub.Tests;

public class FileContentParserTests
{
    // ── XmlFileContentParser ────────────────────────────────────

    [Fact]
    public void Xml_ExtractsTextNodes()
    {
        string xml = "<root><title>Hello World</title><body>Some content here</body></root>";
        XmlFileContentParser parser = new();

        string? result = parser.ExtractText("test.xml", ToStream(xml), 64 * 1024);

        Assert.NotNull(result);
        Assert.Contains("Hello World", result);
        Assert.Contains("Some content here", result);
    }

    [Fact]
    public void Xml_FhirResource_ExtractsSemanticFields()
    {
        string xml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
                <name value="Patient"/>
                <title value="Patient Resource"/>
                <description value="Demographics and other information about a patient"/>
                <definition value="The patient definition"/>
                <text>
                    <status value="generated"/>
                    <div xmlns="http://www.w3.org/1999/xhtml">
                        <p>This is narrative text about the patient</p>
                    </div>
                </text>
            </StructureDefinition>
            """;

        XmlFileContentParser parser = new();
        string? result = parser.ExtractText("Patient.xml", ToStream(xml), 64 * 1024);

        Assert.NotNull(result);
        Assert.Contains("Patient", result);
        Assert.Contains("Patient Resource", result);
        Assert.Contains("Demographics", result);
        Assert.Contains("narrative text", result);
    }

    [Fact]
    public void Xml_MalformedXml_FallsBackToText()
    {
        string malformed = "<root><unclosed>some text";
        XmlFileContentParser parser = new();

        string? result = parser.ExtractText("test.xml", ToStream(malformed), 64 * 1024);

        // Should not throw; may return text via fallback
        // The fallback reads as plain text
        Assert.NotNull(result);
    }

    [Fact]
    public void Xml_RespectsMaxOutputLength()
    {
        string xml = "<root>" + new string('a', 1000) + "</root>";
        XmlFileContentParser parser = new();

        string? result = parser.ExtractText("test.xml", ToStream(xml), 50);

        Assert.NotNull(result);
        Assert.True(result.Length <= 50);
    }

    // ── JsonFileContentParser ───────────────────────────────────

    [Fact]
    public void Json_ExtractsStringValues()
    {
        string json = """{"key1": "value1", "key2": "value2", "num": 42}""";
        JsonFileContentParser parser = new();

        string? result = parser.ExtractText("test.json", ToStream(json), 64 * 1024);

        Assert.NotNull(result);
        Assert.Contains("value1", result);
        Assert.Contains("value2", result);
    }

    [Fact]
    public void Json_FhirResource_ExtractsSemanticFields()
    {
        string json = """
            {
                "resourceType": "ValueSet",
                "name": "AdministrativeGender",
                "title": "Administrative Gender",
                "description": "The gender of a person used for administrative purposes",
                "status": "active",
                "text": {
                    "status": "generated",
                    "div": "<div xmlns='http://www.w3.org/1999/xhtml'><p>Gender values</p></div>"
                }
            }
            """;

        JsonFileContentParser parser = new();
        string? result = parser.ExtractText("valueset.json", ToStream(json), 64 * 1024);

        Assert.NotNull(result);
        Assert.Contains("AdministrativeGender", result);
        Assert.Contains("Administrative Gender", result);
        Assert.Contains("administrative purposes", result);
        Assert.Contains("Gender values", result);
    }

    [Fact]
    public void Json_MalformedJson_FallsBackToText()
    {
        string malformed = """{"key": "unclosed""";
        JsonFileContentParser parser = new();

        string? result = parser.ExtractText("test.json", ToStream(malformed), 64 * 1024);
        // Should not throw
    }

    // ── MarkdownFileContentParser ───────────────────────────────

    [Fact]
    public void Markdown_ReturnsTextContent()
    {
        string md = "# Hello\n\nThis is a **markdown** document.\n";
        MarkdownFileContentParser parser = new();

        string? result = parser.ExtractText("test.md", ToStream(md), 64 * 1024);

        Assert.NotNull(result);
        Assert.Contains("Hello", result);
        Assert.Contains("markdown", result);
    }

    [Fact]
    public void Markdown_StripsHtmlTags()
    {
        string md = "# Title\n\n<div class='note'>Important info</div>\n<p>More text</p>";
        MarkdownFileContentParser parser = new();

        string? result = parser.ExtractText("test.md", ToStream(md), 64 * 1024);

        Assert.NotNull(result);
        Assert.Contains("Important info", result);
        Assert.DoesNotContain("<div", result);
        Assert.DoesNotContain("<p>", result);
    }

    // ── PlainTextFileContentParser ──────────────────────────────

    [Fact]
    public void PlainText_ReturnsRawContent()
    {
        string text = "Hello, this is plain text content.\nLine 2.";
        PlainTextFileContentParser parser = new("text");

        string? result = parser.ExtractText("test.txt", ToStream(text), 64 * 1024);

        Assert.NotNull(result);
        Assert.Contains("plain text content", result);
    }

    [Fact]
    public void PlainText_Code_ReturnsRawContent()
    {
        string code = "public class Foo { public int Bar { get; set; } }";
        PlainTextFileContentParser parser = new("code");

        Assert.Equal("code", parser.ParserType);
        string? result = parser.ExtractText("Foo.cs", ToStream(code), 64 * 1024);

        Assert.NotNull(result);
        Assert.Contains("public class Foo", result);
    }

    [Fact]
    public void PlainText_EmptyContent_ReturnsNull()
    {
        PlainTextFileContentParser parser = new("text");
        string? result = parser.ExtractText("empty.txt", ToStream(""), 64 * 1024);
        Assert.Null(result);
    }

    [Fact]
    public void PlainText_WhitespaceOnly_ReturnsNull()
    {
        PlainTextFileContentParser parser = new("text");
        string? result = parser.ExtractText("whitespace.txt", ToStream("   \n\n   "), 64 * 1024);
        Assert.Null(result);
    }

    // ── FallbackFileContentParser ───────────────────────────────

    [Fact]
    public void Fallback_TextFile_ReturnsContent()
    {
        string text = "This is a text file with an unknown extension.";
        FallbackFileContentParser parser = new();

        string? result = parser.ExtractText("test.xyz", ToStream(text), 64 * 1024);

        Assert.NotNull(result);
        Assert.Contains("unknown extension", result);
    }

    [Fact]
    public void Fallback_BinaryContent_ReturnsNull()
    {
        // Create binary content with many non-printable bytes
        byte[] binary = new byte[1000];
        Random rng = new Random(42);
        rng.NextBytes(binary);
        // Ensure enough are truly non-printable (bytes 0x00-0x08, 0x0E-0x1F, 0xF8-0xFF)
        for (int i = 0; i < binary.Length; i++)
            binary[i] = (byte)(i % 2 == 0 ? 0x00 : 0x01);

        FallbackFileContentParser parser = new();
        using MemoryStream ms = new MemoryStream(binary);
        string? result = parser.ExtractText("test.bin", ms, 64 * 1024);

        Assert.Null(result);
    }

    [Fact]
    public void Fallback_EmptyFile_ReturnsNull()
    {
        FallbackFileContentParser parser = new();
        string? result = parser.ExtractText("empty.xyz", ToStream(""), 64 * 1024);
        Assert.Null(result);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static MemoryStream ToStream(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }
}
