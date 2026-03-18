using FhirAugury.Indexing;

namespace FhirAugury.Sources.Tests;

public class TextSanitizerTests
{
    [Fact]
    public void StripHtml_RemovesTags()
    {
        var html = "<p>Hello <b>world</b></p>";
        var result = TextSanitizer.StripHtml(html);
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void StripHtml_DecodesEntities()
    {
        var html = "Hello &amp; world &lt;3&gt;";
        var result = TextSanitizer.StripHtml(html);
        Assert.Equal("Hello & world <3>", result);
    }

    [Fact]
    public void StripHtml_NormalizesWhitespace()
    {
        var html = "<p>Hello</p>   <p>world</p>\n\n\n  test";
        var result = TextSanitizer.StripHtml(html);
        Assert.DoesNotContain("  ", result);
    }

    [Fact]
    public void StripHtml_HandlesNull()
    {
        var result = TextSanitizer.StripHtml(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void StripHtml_HandlesEmpty()
    {
        var result = TextSanitizer.StripHtml(string.Empty);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void StripMarkdown_RemovesHeaders()
    {
        var md = "# Header\n## Sub-header\nRegular text";
        var result = TextSanitizer.StripMarkdown(md);
        Assert.DoesNotContain("#", result);
        Assert.Contains("Regular text", result);
    }

    [Fact]
    public void StripMarkdown_RemovesBold()
    {
        var md = "This is **bold** and __also bold__";
        var result = TextSanitizer.StripMarkdown(md);
        Assert.Contains("bold", result);
        Assert.DoesNotContain("**", result);
        Assert.DoesNotContain("__", result);
    }

    [Fact]
    public void StripMarkdown_RemovesLinks()
    {
        var md = "See [FHIR spec](http://hl7.org/fhir) for details";
        var result = TextSanitizer.StripMarkdown(md);
        Assert.Contains("FHIR spec", result);
        Assert.DoesNotContain("http://", result);
    }

    [Fact]
    public void StripMarkdown_HandlesNull()
    {
        var result = TextSanitizer.StripMarkdown(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void NormalizeUnicode_PerformsNfc()
    {
        // é as e + combining accent vs. precomposed é
        var decomposed = "e\u0301"; // e + combining acute accent
        var result = TextSanitizer.NormalizeUnicode(decomposed);
        Assert.Equal("\u00e9", result); // precomposed é
    }

    [Fact]
    public void NormalizeUnicode_HandlesNull()
    {
        var result = TextSanitizer.NormalizeUnicode(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ExtractPlainText_DispatchesHtml()
    {
        var html = "<p>Hello</p>";
        var result = TextSanitizer.ExtractPlainText(html, ContentFormat.Html);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void ExtractPlainText_DispatchesMarkdown()
    {
        var md = "**bold** text";
        var result = TextSanitizer.ExtractPlainText(md, ContentFormat.Markdown);
        Assert.Contains("bold", result);
        Assert.DoesNotContain("**", result);
    }

    [Fact]
    public void ExtractPlainText_PlainTextPassthrough()
    {
        var text = "Just plain text";
        var result = TextSanitizer.ExtractPlainText(text, ContentFormat.PlainText);
        Assert.Equal("Just plain text", result);
    }
}
