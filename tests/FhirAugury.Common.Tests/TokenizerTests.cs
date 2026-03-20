using FhirAugury.Common.Text;

namespace FhirAugury.Common.Tests;

public class TokenizerTests
{
    [Fact]
    public void Tokenize_ExtractsFhirPaths()
    {
        var tokens = Tokenizer.Tokenize("See Patient.name.given for details");
        Assert.Contains("patient.name.given", tokens);
        Assert.Contains("patient", tokens);
        Assert.Contains("name", tokens);
        Assert.Contains("given", tokens);
    }

    [Fact]
    public void Tokenize_ExtractsFhirOperations()
    {
        var tokens = Tokenizer.Tokenize("Use $validate to check");
        Assert.Contains("$validate", tokens);
    }

    [Fact]
    public void Tokenize_ReturnsEmptyForNull()
    {
        var tokens = Tokenizer.Tokenize("");
        Assert.Empty(tokens);
    }
}
