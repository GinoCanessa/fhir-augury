using FhirAugury.Common.Text;

namespace FhirAugury.Common.Tests;

public class PublicDisplayNamePolicyTests
{
    [Fact]
    public void Normalize_TrimsSafeDisplayName()
    {
        string? result = PublicDisplayNamePolicy.Normalize(
            "  Alice Example  ",
            "alice.account");

        Assert.Equal("Alice Example", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_RejectsBlankValues(string? value)
    {
        Assert.Null(PublicDisplayNamePolicy.Normalize(value));
    }

    [Theory]
    [InlineData("alice.account", "alice.account")]
    [InlineData("ALICE.ACCOUNT", "alice.account")]
    [InlineData("  Alice.Account  ", " alice.account ")]
    [InlineData("alice@example.org", null)]
    [InlineData("Alice Example <alice@example.org>", null)]
    [InlineData("Contact alice+chair@example.co.uk for details", null)]
    [InlineData("(alice@example.org)", null)]
    public void Normalize_RejectsBoundUsernameAndEmailTokens(
        string value,
        string? boundAccountUsername)
    {
        Assert.Null(PublicDisplayNamePolicy.Normalize(
            value,
            boundAccountUsername));
    }

    [Fact]
    public void Normalize_DoesNotTreatSeparatedAtSignAsEmailToken()
    {
        Assert.Equal(
            "Alice @ HL7",
            PublicDisplayNamePolicy.Normalize(" Alice @ HL7 "));
    }
}
