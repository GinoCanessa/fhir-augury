using FhirAugury.Processing.Common.Authoring;

namespace FhirAugury.Processing.Common.Tests.Authoring;

public sealed class AuthoringResultHasherTests
{
    [Fact]
    public void HashNormalizedUtf8_NormalizesLineEndingsAndUnicode()
    {
        string composed = "caf\u00e9\r\nline";
        string decomposed = "cafe\u0301\nline";

        Assert.Equal(
            AuthoringResultHasher.HashNormalizedUtf8(composed),
            AuthoringResultHasher.HashNormalizedUtf8(decomposed));
    }

    [Fact]
    public void HashNormalizedUtf8_ChangesWhenContentChanges()
    {
        Assert.NotEqual(
            AuthoringResultHasher.HashNormalizedUtf8("one"),
            AuthoringResultHasher.HashNormalizedUtf8("two"));
    }
}
