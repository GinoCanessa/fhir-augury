using System.Text;
using FhirAugury.Parsing.Xml;

namespace FhirAugury.Parsing.Xml.Tests;

public class DowngradingTextReaderTests
{
    private static string Process(string input)
    {
        using StringReader sr = new(input);
        using DowngradingTextReader d = new(sr, ownsInner: false);
        return d.ReadToEnd();
    }

    [Fact]
    public void NoDeclarationPassesThrough()
    {
        Assert.Equal("<r>x</r>", Process("<r>x</r>"));
    }

    [Fact]
    public void Xml11DeclarationRewritten()
    {
        string output = Process("<?xml version=\"1.1\" encoding=\"UTF-8\"?><r/>");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", output);
    }

    [Fact]
    public void NelInTextBecomesCrLf()
    {
        string output = Process("<r>a\u0085b</r>");
        Assert.Equal("<r>a\r\nb</r>", output);
    }

    [Fact]
    public void LineSeparatorBecomesCrLf()
    {
        string output = Process("<r>a\u2028b</r>");
        Assert.Equal("<r>a\r\nb</r>", output);
    }

    [Fact]
    public void IllegalControlStrippedFromText()
    {
        string output = Process("<r>a\u0001\u0090b</r>");
        Assert.Equal("<r>ab</r>", output);
    }

    [Fact]
    public void IllegalCharStrippedFromTagName()
    {
        string output = Process("<foo\u0001bar/>");
        Assert.Equal("<foobar/>", output);
    }

    [Fact]
    public void IllegalCharStrippedFromAttrName()
    {
        string output = Process("<r at\u0001tr=\"v\"/>");
        Assert.Equal("<r attr=\"v\"/>", output);
    }

    [Fact]
    public void EmptyNameFallsBackToUnderscore()
    {
        string output = Process("<\u0001>x</\u0001>");
        Assert.Equal("<_>x</_>", output);
    }

    [Fact]
    public void GreaterThanInsideAttributeValuePreserved()
    {
        string output = Process("<r a=\"x>y\"><c/></r>");
        Assert.Equal("<r a=\"x>y\"><c/></r>", output);
    }

    [Fact]
    public void CDataContentPreservedIncludingMarkupChars()
    {
        string output = Process("<r><![CDATA[<not-an-element>&]]></r>");
        Assert.Equal("<r><![CDATA[<not-an-element>&]]></r>", output);
    }

    [Fact]
    public void CommentContentPreserved()
    {
        string output = Process("<r><!-- a < b ? --></r>");
        Assert.Equal("<r><!-- a < b ? --></r>", output);
    }

    [Fact]
    public void ProcessingInstructionPreserved()
    {
        string output = Process("<r><?xml-stylesheet href=\"x\"?></r>");
        Assert.Equal("<r><?xml-stylesheet href=\"x\"?></r>", output);
    }

    [Fact]
    public void DoctypePreservedWithInternalSubset()
    {
        string output = Process("<!DOCTYPE r [<!ELEMENT r EMPTY>]><r/>");
        Assert.Equal("<!DOCTYPE r [<!ELEMENT r EMPTY>]><r/>", output);
    }

    [Fact]
    public void SelfClosingTagPreserved()
    {
        string output = Process("<a><b/></a>");
        Assert.Equal("<a><b/></a>", output);
    }

    [Fact]
    public void EndTagWithWhitespacePreserved()
    {
        string output = Process("<r></r >");
        Assert.Equal("<r></r >", output);
    }

    [Fact]
    public void XmlDeclarationOrderPreserved()
    {
        string output = Process("<?xml encoding=\"UTF-8\" version=\"1.1\" standalone=\"yes\"?><r/>");
        Assert.StartsWith(
            "<?xml encoding=\"UTF-8\" version=\"1.0\" standalone=\"yes\"?>",
            output);
    }
}
