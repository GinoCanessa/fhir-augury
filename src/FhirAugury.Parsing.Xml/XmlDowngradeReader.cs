using System.Text;
using System.Xml;

namespace FhirAugury.Parsing.Xml;

/// <summary>
/// Factory that creates an <see cref="XmlReader"/> wrapping an XML 1.1
/// (or otherwise malformed-by-1.0-rules) input stream and exposing it as
/// XML 1.0 to downstream consumers such as <c>XDocument.Load</c>.
/// </summary>
/// <remarks>
/// The wrapper performs four character-stream transformations:
/// <list type="number">
/// <item>Rewrites <c>&lt;?xml version="1.1" ... ?&gt;</c> to <c>version="1.0"</c>.</item>
/// <item>Replaces U+0085 (NEL) and U+2028 (LS) with CR+LF.</item>
/// <item>Strips code points outside the XML 1.0 <c>Char</c> production.</item>
/// <item>Sanitizes element/attribute names to <c>NameStartChar</c>/<c>NameChar</c>.</item>
/// </list>
/// </remarks>
public static class XmlDowngradeReader
{
    /// <summary>
    /// Creates an XmlReader over <paramref name="input"/>. The underlying
    /// <see cref="Stream"/> is <em>not</em> closed when the returned reader is
    /// disposed, matching <see cref="XmlReader.Create(Stream)"/>.
    /// </summary>
    public static XmlReader Create(Stream input, XmlReaderSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        StreamReader reader = new(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        return CreateCore(reader, ownsInner: true, settings);
    }

    /// <summary>Creates an XmlReader over <paramref name="input"/>.</summary>
    public static XmlReader Create(TextReader input, XmlReaderSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return CreateCore(input, ownsInner: false, settings);
    }

    /// <summary>Creates an XmlReader over the file at <paramref name="filePath"/>.</summary>
    public static XmlReader Create(string filePath, XmlReaderSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FileStream fs = File.OpenRead(filePath);
        StreamReader reader = new(fs, detectEncodingFromByteOrderMarks: true);
        return CreateCore(reader, ownsInner: true, settings);
    }

    private static XmlReader CreateCore(TextReader inner, bool ownsInner, XmlReaderSettings? settings)
    {
        XmlReaderSettings effective = settings is null
            ? new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }
            : settings.Clone();

        // Always own the downgrading reader; pass-through to inner is governed by ownsInner.
        effective.CloseInput = true;

        DowngradingTextReader downgrading = new(inner, ownsInner);
        return XmlReader.Create(downgrading, effective);
    }
}
