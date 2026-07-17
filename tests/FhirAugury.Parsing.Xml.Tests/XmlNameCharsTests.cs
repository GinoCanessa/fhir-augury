using FhirAugury.Parsing.Xml;

namespace FhirAugury.Parsing.Xml.Tests;

public class XmlNameCharsTests
{
    [Theory]
    [InlineData('A')]
    [InlineData('z')]
    [InlineData('_')]
    [InlineData(':')]
    [InlineData((int)'\u00C0')]
    public void NameStartChar_AllowsLegalChars(int cp)
    {
        Assert.True(XmlNameChars.IsNameStartChar(cp));
    }

    [Theory]
    [InlineData('-')]
    [InlineData('.')]
    [InlineData('0')]
    [InlineData(' ')]
    [InlineData('\u0001')]
    public void NameStartChar_RejectsIllegalChars(int cp)
    {
        Assert.False(XmlNameChars.IsNameStartChar(cp));
    }

    [Theory]
    [InlineData('A')]
    [InlineData('-')]
    [InlineData('.')]
    [InlineData('0')]
    public void NameChar_AllowsLegalChars(int cp)
    {
        Assert.True(XmlNameChars.IsNameChar(cp));
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('<')]
    [InlineData('\u0001')]
    public void NameChar_RejectsIllegalChars(int cp)
    {
        Assert.False(XmlNameChars.IsNameChar(cp));
    }

    [Theory]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x20)]
    [InlineData(0x41)]
    [InlineData(0xD7FF)]
    [InlineData(0xE000)]
    [InlineData(0xFFFD)]
    [InlineData(0x10000)]
    [InlineData(0x10FFFF)]
    public void Xml10Char_AllowsLegalChars(int cp)
    {
        Assert.True(XmlNameChars.IsXml10Char(cp));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x08)]
    [InlineData(0x0B)]
    [InlineData(0x0C)]
    [InlineData(0x0E)]
    [InlineData(0x1F)]
    [InlineData(0x85)]   // NEL — illegal in 1.0 (we substitute earlier)
    [InlineData(0x90)]   // C1
    [InlineData(0xD800)] // surrogate
    [InlineData(0xDFFF)] // surrogate
    [InlineData(0xFFFE)]
    [InlineData(0xFFFF)]
    public void Xml10Char_RejectsIllegalChars(int cp)
    {
        Assert.False(XmlNameChars.IsXml10Char(cp));
    }
}
