using FhirAugury.Sources.Confluence;

namespace FhirAugury.Sources.Tests;

public class ConfluenceContentParserTests
{
    [Fact]
    public void ToPlainText_StripsMacros()
    {
        var storage = "<p>Hello world</p><ac:structured-macro ac:name=\"info\"><ac:rich-text-body><p>Info text</p></ac:rich-text-body></ac:structured-macro>";
        var result = ConfluenceContentParser.ToPlainText(storage);
        Assert.Contains("Hello world", result);
        Assert.DoesNotContain("ac:structured-macro", result);
    }

    [Fact]
    public void ToPlainText_PreservesTableContent()
    {
        var storage = "<table><tr><td>Name</td><td>Value</td></tr></table>";
        var result = ConfluenceContentParser.ToPlainText(storage);
        Assert.Contains("Name", result);
        Assert.Contains("Value", result);
    }

    [Fact]
    public void ToPlainText_KeepsImageAltText()
    {
        var storage = "<ac:image alt=\"Patient diagram\"><ri:attachment ri:filename=\"patient.png\" /></ac:image>";
        var result = ConfluenceContentParser.ToPlainText(storage);
        Assert.Contains("Patient diagram", result);
    }

    [Fact]
    public void ToPlainText_HandlesNull()
    {
        Assert.Equal(string.Empty, ConfluenceContentParser.ToPlainText(null));
    }

    [Fact]
    public void ToPlainText_HandlesEmpty()
    {
        Assert.Equal(string.Empty, ConfluenceContentParser.ToPlainText(""));
    }

    [Fact]
    public void ToPlainText_HandlesWhitespace()
    {
        Assert.Equal(string.Empty, ConfluenceContentParser.ToPlainText("   "));
    }

    [Fact]
    public void ToPlainText_FallsBackForInvalidXml()
    {
        var invalid = "<p>Hello <b>world</p></b> <script>alert('xss')</script>";
        var result = ConfluenceContentParser.ToPlainText(invalid);
        Assert.Contains("Hello", result);
        Assert.Contains("world", result);
    }

    [Fact]
    public void ToPlainText_FromTestDataFile()
    {
        var storage = File.ReadAllText(Path.Combine("TestData", "sample-confluence-storage.xml"));
        var result = ConfluenceContentParser.ToPlainText(storage);

        Assert.Contains("Patient", result);
        Assert.Contains("identifier", result);
        Assert.Contains("FHIR-12345", result);
        // "normative" lives inside ac:rich-text-body which the parser skips (only plain-text-body is kept)
        Assert.Contains("Patient diagram", result);
    }
}
