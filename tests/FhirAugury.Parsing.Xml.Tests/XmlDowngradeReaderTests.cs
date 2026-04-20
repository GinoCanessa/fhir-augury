using System.Text;
using System.Xml;
using System.Xml.Linq;
using FhirAugury.Parsing.Xml;

namespace FhirAugury.Parsing.Xml.Tests;

public class XmlDowngradeReaderTests
{
    private static XDocument LoadFromString(string xml)
    {
        // Round-trip via UTF-8 bytes so the wrapper exercises its Stream path.
        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        using MemoryStream ms = new(bytes);
        using XmlReader reader = XmlDowngradeReader.Create(ms);
        return XDocument.Load(reader);
    }

    [Fact]
    public void Test01_Xml11DeclarationDowngraded()
    {
        XDocument doc = LoadFromString("<?xml version=\"1.1\" encoding=\"UTF-8\" ?><root/>");
        Assert.Equal("1.0", doc.Declaration?.Version);
        Assert.Equal("root", doc.Root?.Name.LocalName);
    }

    [Fact]
    public void Test02_Xml10DeclarationPassesThrough()
    {
        XDocument doc = LoadFromString("<?xml version=\"1.0\" encoding=\"UTF-8\" ?><root/>");
        Assert.Equal("1.0", doc.Declaration?.Version);
        Assert.Equal("UTF-8", doc.Declaration?.Encoding);
    }

    [Fact]
    public void Test03_NoDeclarationParsesUnchanged()
    {
        XDocument doc = LoadFromString("<root><c/></root>");
        Assert.Null(doc.Declaration);
        Assert.Equal("root", doc.Root?.Name.LocalName);
    }

    [Fact]
    public void Test04_NelInTextBecomesCrLf()
    {
        XDocument doc = LoadFromString("<r>a\u0085b</r>");
        Assert.Contains("\n", doc.Root?.Value);
    }

    [Fact]
    public void Test05_LineSeparatorInTextBecomesCrLf()
    {
        XDocument doc = LoadFromString("<r>a\u2028b</r>");
        Assert.Contains("\n", doc.Root?.Value);
    }

    [Fact]
    public void Test06_C0ControlCharStripped()
    {
        XDocument doc = LoadFromString("<r>a\u0001b</r>");
        Assert.Equal("ab", doc.Root?.Value);
    }

    [Fact]
    public void Test07_C1ControlCharStripped()
    {
        XDocument doc = LoadFromString("<r>a\u0090b</r>");
        Assert.Equal("ab", doc.Root?.Value);
    }

    [Fact]
    public void Test08_IllegalCharInElementNameStripped()
    {
        XDocument doc = LoadFromString("<foo\u0001bar>x</foo\u0001bar>");
        Assert.Equal("foobar", doc.Root?.Name.LocalName);
    }

    [Fact]
    public void Test09_IllegalCharInAttributeNameStripped()
    {
        XDocument doc = LoadFromString("<r at\u0001tr=\"v\"/>");
        XAttribute? a = doc.Root?.Attribute("attr");
        Assert.NotNull(a);
        Assert.Equal("v", a!.Value);
    }

    [Fact]
    public void Test10_EmptyNameFallsBackToUnderscore()
    {
        XDocument doc = LoadFromString("<\u0001>x</\u0001>");
        Assert.Equal("_", doc.Root?.Name.LocalName);
    }

    [Fact]
    public void Test11_EncodingPreservedNonAscii()
    {
        XDocument doc = LoadFromString("<?xml version=\"1.0\" encoding=\"UTF-8\"?><r>café</r>");
        Assert.Equal("café", doc.Root?.Value);
    }

    [Fact]
    public void Test13_WellFormedXml10Unaffected()
    {
        string source = "<?xml version=\"1.0\"?><a x=\"1\"><b>hello</b><c/></a>";
        XDocument viaWrapper = LoadFromString(source);
        XDocument direct = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
        Assert.Equal(direct.ToString(), viaWrapper.ToString());
    }

    [Fact]
    public void Test14_StandalonePreservedDuringDowngrade()
    {
        XDocument doc = LoadFromString(
            "<?xml version=\"1.1\" encoding=\"UTF-8\" standalone=\"yes\"?><r/>");
        Assert.Equal("1.0", doc.Declaration?.Version);
        Assert.Equal("yes", doc.Declaration?.Standalone);
    }

    [Fact]
    public void Test15_StandaloneAfterVersionPreserved()
    {
        // Verifies version+encoding+standalone ordering survives downgrade.
        XDocument doc = LoadFromString(
            "<?xml version=\"1.1\" encoding=\"UTF-8\" standalone=\"no\"?><r/>");
        Assert.Equal("1.0", doc.Declaration?.Version);
        Assert.Equal("UTF-8", doc.Declaration?.Encoding);
        Assert.Equal("no", doc.Declaration?.Standalone);
    }

    [Fact]
    public void Test16_LoneHighSurrogateDropped()
    {
        // Bypass UTF-8 round-trip by going through TextReader (lone surrogate is invalid UTF-8).
        string xml = "<r>a\uD800b</r>";
        using StringReader sr = new(xml);
        using XmlReader xr = XmlDowngradeReader.Create(sr);
        XDocument doc = XDocument.Load(xr);
        Assert.Equal("ab", doc.Root?.Value);
    }

    [Fact]
    public void Test17_SupplementaryCodepointPreserved()
    {
        // U+1F600 grinning face emoji.
        string xml = "<r>face=\U0001F600</r>";
        XDocument doc = LoadFromString(xml);
        Assert.Equal("face=\U0001F600", doc.Root?.Value);
    }

    [Fact]
    public void Test18_BomDoesNotThrow()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] body = Encoding.UTF8.GetBytes("<r/>");
        using MemoryStream ms = new([.. bom, .. body]);
        using XmlReader xr = XmlDowngradeReader.Create(ms);
        XDocument doc = XDocument.Load(xr);
        Assert.Equal("r", doc.Root?.Name.LocalName);
    }

    [Fact]
    public void Test19_HonorsCallerSettings()
    {
        XmlReaderSettings settings = new()
        {
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Ignore,
        };
        using StringReader sr = new("<r>  <c/>  </r>");
        using XmlReader xr = XmlDowngradeReader.Create(sr, settings);
        XDocument doc = XDocument.Load(xr);
        // With IgnoreWhitespace, no insignificant text node siblings.
        Assert.Single(doc.Root!.Nodes());
    }

    [Fact]
    public void Test20_DisposingDoesNotCloseCallerStream()
    {
        // Matches XmlReader.Create(Stream) semantics: the caller-owned Stream
        // is left open so the caller can re-read or re-position it.
        TrackingStream ts = new(Encoding.UTF8.GetBytes("<r/>"));
        XmlReader xr = XmlDowngradeReader.Create(ts);
        XDocument.Load(xr);
        xr.Dispose();
        Assert.False(ts.WasDisposed);
        Assert.True(ts.CanRead);
    }

    [Fact]
    public void OtherHspMarketFixtureParses()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "OTHER-hsp-market.xml");
        Assert.True(File.Exists(fixturePath), $"Fixture missing: {fixturePath}");

        using XmlReader xr = XmlDowngradeReader.Create(fixturePath);
        XDocument doc = XDocument.Load(xr);

        Assert.Equal("specification", doc.Root?.Name.LocalName);
        Assert.Equal("hsp-market", (string?)doc.Root?.Attribute("key"));
    }

    private sealed class TrackingStream : Stream
    {
        private readonly MemoryStream _inner;
        public bool WasDisposed { get; private set; }
        public TrackingStream(byte[] data) { _inner = new MemoryStream(data); }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
