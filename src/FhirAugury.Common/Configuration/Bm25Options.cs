namespace FhirAugury.Common.Configuration;

/// <summary>
/// BM25 scoring parameters and text indexing options for keyword indexing.
/// </summary>
public class Bm25Options
{
    /// <summary>
    /// Term frequency saturation parameter. Higher values increase the impact of
    /// additional term occurrences. Typical range: 1.2–2.0.
    /// </summary>
    public double K1 { get; set; } = 1.2;

    /// <summary>
    /// Document length normalization parameter. 0 = no normalization,
    /// 1 = full normalization. Typical value: 0.75.
    /// </summary>
    public double B { get; set; } = 0.75;

    /// <summary>
    /// When true, the internal lemmatizer normalizes inflected word forms to their
    /// base form during BM25 keyword extraction. When false, tokens are indexed as-is.
    /// </summary>
    public bool UseLemmatization { get; set; } = true;

    /// <summary>
    /// FTS5 tokenizer specification used when creating full-text search virtual tables.
    /// Examples: "porter", "unicode61", "unicode61 remove_diacritics 1".
    /// When null, the SQLite default tokenizer (unicode61) is used.
    /// </summary>
    public string? FtsTokenizer { get; set; }
}
