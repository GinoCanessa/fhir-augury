using FhirAugury.Indexing.Bm25;

namespace FhirAugury.Indexing.Tests;

public class TokenizerTests
{
    [Fact]
    public void Tokenize_SimpleText_SplitsIntoWords()
    {
        var tokens = Tokenizer.Tokenize("Hello world test");

        Assert.Contains("hello", tokens);
        Assert.Contains("world", tokens);
        Assert.Contains("test", tokens);
    }

    [Fact]
    public void Tokenize_FhirPath_PreservesFullPathAndComponents()
    {
        var tokens = Tokenizer.Tokenize("The element Patient.name.given is required.");

        Assert.Contains("patient.name.given", tokens);
        Assert.Contains("patient", tokens);
        Assert.Contains("name", tokens);
        Assert.Contains("given", tokens);
    }

    [Fact]
    public void Tokenize_FhirOperation_PreservesOperation()
    {
        var tokens = Tokenizer.Tokenize("Use $validate to check resources.");

        Assert.Contains("$validate", tokens);
    }

    [Fact]
    public void Tokenize_StripsUrls()
    {
        var tokens = Tokenizer.Tokenize("Visit https://example.com/page for info.");

        // URL parts should not appear as tokens
        Assert.DoesNotContain("https", tokens);
        Assert.DoesNotContain("example", tokens);
    }

    [Fact]
    public void Tokenize_StripsEmails()
    {
        var tokens = Tokenizer.Tokenize("Contact user@example.com for help.");

        Assert.DoesNotContain("user@example.com", tokens);
    }

    [Fact]
    public void Tokenize_StripsCodeBlocks()
    {
        var tokens = Tokenizer.Tokenize("Before ```code block content``` after");

        Assert.DoesNotContain("code", tokens);
        Assert.DoesNotContain("block", tokens);
        Assert.Contains("before", tokens);
        Assert.Contains("after", tokens);
    }

    [Fact]
    public void Tokenize_LowercasesAllTokens()
    {
        var tokens = Tokenizer.Tokenize("UPPERCASE Mixed CASE");

        Assert.All(tokens, t => Assert.Equal(t, t.ToLowerInvariant()));
    }

    [Fact]
    public void Tokenize_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(Tokenizer.Tokenize(""));
        Assert.Empty(Tokenizer.Tokenize("   "));
    }

    [Fact]
    public void Tokenize_MultipleFhirPaths_ExtractsAll()
    {
        var tokens = Tokenizer.Tokenize("Compare Patient.name and Observation.value");

        Assert.Contains("patient.name", tokens);
        Assert.Contains("observation.value", tokens);
    }
}
