using System.Collections.Frozen;

namespace FhirAugury.Common.Text;

/// <summary>
/// Normalizes inflected word forms to their base (lemma) form using a preloaded dictionary.
/// </summary>
public class Lemmatizer
{
    /// <summary>A lemmatizer with no lemma data — all tokens pass through unchanged.</summary>
    public static Lemmatizer Empty { get; } = new(FrozenDictionary<string, string>.Empty);

    private readonly FrozenDictionary<string, string> _lemmas;

    /// <summary>The number of lemma entries loaded.</summary>
    public int Count => _lemmas.Count;

    public Lemmatizer(FrozenDictionary<string, string> lemmas)
    {
        _lemmas = lemmas;
    }

    /// <summary>
    /// Returns the lemma form of a token if one exists, otherwise returns the original token.
    /// </summary>
    public string Lemmatize(string token)
    {
        return _lemmas.TryGetValue(token, out string? lemma) ? lemma : token;
    }

    /// <summary>
    /// Attempts to find the lemma form of a token.
    /// </summary>
    /// <returns>True if a lemma mapping was found.</returns>
    public bool TryLemmatize(string token, out string lemma)
    {
        if (_lemmas.TryGetValue(token, out string? found))
        {
            lemma = found;
            return true;
        }

        lemma = token;
        return false;
    }
}
