using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class RationaleMarkdownValidatorTests
{
    [Theory]
    [InlineData("Plain prose without any markup.")]
    [InlineData("Has an `inline code` span.")]
    [InlineData("Has *emphasis* and _emphasis2_ markers.")]
    [InlineData("Refer to [the spec](https://hl7.org/fhir).")]
    [InlineData("Multi\nparagraph\n\nseparated by single newline pairs.")]
    [InlineData("Email link to [team](mailto:team@example.com).")]
    public void IsValid_AcceptsInlineSubset(string value)
    {
        Assert.True(RationaleMarkdownValidator.IsValid(value, out string? reason), reason);
    }

    [Theory]
    [InlineData("# Heading line is not allowed.")]
    [InlineData("> Block-quote line is not allowed.")]
    [InlineData("- Bulleted line is not allowed.")]
    [InlineData("* Star-bulleted line is not allowed.")]
    [InlineData("1. Numbered list line is not allowed.")]
    [InlineData("Fenced ```\ncode\n``` block.")]
    [InlineData("Contains <span>HTML</span> tag.")]
    [InlineData("Has an ![image alt](https://x/y.png) inline.")]
    [InlineData("Visit [bad](javascript:alert(1)).")]
    [InlineData("Triple\n\n\nnewlines disallowed.")]
    [InlineData("Has a \r carriage return.")]
    public void IsValid_RejectsBlockOrUnsafeMarkup(string value)
    {
        Assert.False(RationaleMarkdownValidator.IsValid(value, out string? reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void IsValid_RejectsOversizedPayload()
    {
        string oversized = new('a', RationaleMarkdownValidator.MaxLengthBytes + 1);
        Assert.False(RationaleMarkdownValidator.IsValid(oversized, out string? reason));
        Assert.Contains("limit", reason ?? string.Empty);
    }
}
