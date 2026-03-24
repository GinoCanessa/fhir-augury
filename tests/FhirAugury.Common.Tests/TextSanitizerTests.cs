using FhirAugury.Common.Text;

namespace FhirAugury.Common.Tests;

public class TextSanitizerTests
{
    [Fact]
    public void StripHtml_RemovesTags()
    {
        string result = TextSanitizer.StripHtml("<p>Hello <b>World</b></p>");
        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void StripHtml_DecodesEntities()
    {
        string result = TextSanitizer.StripHtml("&amp; &lt; &gt;");
        Assert.Equal("& < >", result);
    }

    [Fact]
    public void StripHtml_ReturnsEmptyForNull()
    {
        Assert.Equal(string.Empty, TextSanitizer.StripHtml(null));
    }

    [Fact]
    public void StripMarkdown_RemovesHeaders()
    {
        string result = TextSanitizer.StripMarkdown("# Title\nContent");
        Assert.Contains("Title", result);
        Assert.Contains("Content", result);
    }

    [Fact]
    public void StripMarkdown_RemovesLinks()
    {
        string result = TextSanitizer.StripMarkdown("[text](http://example.com)");
        Assert.Contains("text", result);
        Assert.DoesNotContain("http://example.com", result);
    }
}
