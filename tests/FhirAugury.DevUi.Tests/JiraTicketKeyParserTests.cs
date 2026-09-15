using FhirAugury.DevUi.Services;

namespace FhirAugury.DevUi.Tests;

public sealed class JiraTicketKeyParserTests
{
    [Fact]
    public void NormalizesPasteFriendlyInputAndPreservesFirstSeenOrder()
    {
        JiraTicketKeyParser parser = new();

        FhirAugury.DevUi.Models.JiraTicketKeyParseResult result =
            parser.Parse(
                " fhir-12, GF-3;\nFHIR-12\tabc1-9 ");

        Assert.Equal(
            ["FHIR-12", "GF-3", "ABC1-9"],
            result.ValidKeys);
        Assert.Empty(result.InvalidTokens);
    }

    [Fact]
    public void ReturnsInvalidTokensSeparatelyAndDeduplicatesThem()
    {
        JiraTicketKeyParser parser = new();

        FhirAugury.DevUi.Models.JiraTicketKeyParseResult result =
            parser.Parse("FHIR-1, nope,NoPe; X-2 bad/key");

        Assert.Equal(["FHIR-1", "X-2"], result.ValidKeys);
        Assert.Equal(["NOPE", "BAD/KEY"], result.InvalidTokens);
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ; \r\n")]
    public void EmptyInputProducesNoKeys(string? input)
    {
        JiraTicketKeyParser parser = new();

        FhirAugury.DevUi.Models.JiraTicketKeyParseResult result =
            parser.Parse(input);

        Assert.Empty(result.ValidKeys);
        Assert.Empty(result.InvalidTokens);
        Assert.False(result.IsValid);
    }
}
