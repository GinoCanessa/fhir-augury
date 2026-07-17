namespace FhirAugury.Parsing.Xml;

/// <summary>
/// Predicates for the XML 1.0 (Fifth Edition) <c>NameStartChar</c> and
/// <c>NameChar</c> productions.
/// </summary>
internal static class XmlNameChars
{
    /// <summary>True if <paramref name="cp"/> is a valid first character of a Name.</summary>
    public static bool IsNameStartChar(int cp)
    {
        if (cp == ':' || cp == '_') return true;
        if (cp >= 'A' && cp <= 'Z') return true;
        if (cp >= 'a' && cp <= 'z') return true;
        if (cp >= 0x00C0 && cp <= 0x00D6) return true;
        if (cp >= 0x00D8 && cp <= 0x00F6) return true;
        if (cp >= 0x00F8 && cp <= 0x02FF) return true;
        if (cp >= 0x0370 && cp <= 0x037D) return true;
        if (cp >= 0x037F && cp <= 0x1FFF) return true;
        if (cp >= 0x200C && cp <= 0x200D) return true;
        if (cp >= 0x2070 && cp <= 0x218F) return true;
        if (cp >= 0x2C00 && cp <= 0x2FEF) return true;
        if (cp >= 0x3001 && cp <= 0xD7FF) return true;
        if (cp >= 0xF900 && cp <= 0xFDCF) return true;
        if (cp >= 0xFDF0 && cp <= 0xFFFD) return true;
        if (cp >= 0x10000 && cp <= 0xEFFFF) return true;
        return false;
    }

    /// <summary>True if <paramref name="cp"/> is a valid non-first character of a Name.</summary>
    public static bool IsNameChar(int cp)
    {
        if (IsNameStartChar(cp)) return true;
        if (cp == '-' || cp == '.') return true;
        if (cp >= '0' && cp <= '9') return true;
        if (cp == 0x00B7) return true;
        if (cp >= 0x0300 && cp <= 0x036F) return true;
        if (cp >= 0x203F && cp <= 0x2040) return true;
        return false;
    }

    /// <summary>
    /// True if <paramref name="cp"/> is a valid XML 1.0 <c>Char</c>
    /// <em>and</em> not in the C1 control range.
    /// </summary>
    /// <remarks>
    /// XML 1.0 itself permits the C1 controls (U+0080–U+009F), but they are
    /// strongly discouraged and several downstream consumers reject them.
    /// Per the downgrade contract we strip them too. NEL (U+0085) is
    /// substituted to CR+LF before this predicate runs.
    /// </remarks>
    public static bool IsXml10Char(int cp)
    {
        if (cp == 0x9 || cp == 0xA || cp == 0xD) return true;
        if (cp >= 0x20 && cp <= 0x7F) return true;
        if (cp >= 0xA0 && cp <= 0xD7FF) return true;
        if (cp >= 0xE000 && cp <= 0xFFFD) return true;
        if (cp >= 0x10000 && cp <= 0x10FFFF) return true;
        return false;
    }
}
