using System.Collections.Frozen;

namespace FhirAugury.Common.Text;

/// <summary>
/// Common English stop words to filter from keyword indexes.
/// Provides hardcoded defaults that can be merged with database-loaded stop words.
/// </summary>
public static class StopWords
{
    private static readonly HashSet<string> DefaultWords =
    [
        "a", "about", "above", "after", "again", "against", "all", "am", "an",
        "and", "any", "are", "aren't", "as", "at", "be", "because", "been",
        "before", "being", "below", "between", "both", "but", "by", "can",
        "can't", "cannot", "could", "couldn't", "did", "didn't", "do", "does",
        "doesn't", "doing", "don't", "down", "during", "each", "few", "for",
        "from", "further", "get", "got", "had", "hadn't", "has", "hasn't",
        "have", "haven't", "having", "he", "he'd", "he'll", "he's", "her",
        "here", "here's", "hers", "herself", "him", "himself", "his", "how",
        "how's", "i", "i'd", "i'll", "i'm", "i've", "if", "in", "into", "is",
        "isn't", "it", "it's", "its", "itself", "let's", "me", "more", "most",
        "mustn't", "my", "myself", "no", "nor", "not", "of", "off", "on",
        "once", "only", "or", "other", "ought", "our", "ours", "ourselves",
        "out", "over", "own", "per", "same", "shan't", "she", "she'd", "she'll",
        "she's", "should", "shouldn't", "so", "some", "such", "than", "that",
        "that's", "the", "their", "theirs", "them", "themselves", "then",
        "there", "there's", "these", "they", "they'd", "they'll", "they're",
        "they've", "this", "those", "through", "to", "too", "under", "until",
        "up", "upon", "us", "very", "was", "wasn't", "we", "we'd", "we'll",
        "we're", "we've", "were", "weren't", "what", "what's", "when", "when's",
        "where", "where's", "which", "while", "who", "who's", "whom", "why",
        "why's", "will", "with", "won't", "would", "wouldn't", "you", "you'd",
        "you'll", "you're", "you've", "your", "yours", "yourself", "yourselves",
        // Additional common words often filtered in technical text
        "also", "just", "like", "may", "might", "much", "need", "new", "now",
        "old", "one", "see", "still", "take", "thing", "think", "two", "use",
        "used", "using", "want", "way", "well",
    ];

    /// <summary>Returns true if the word is in the default hardcoded stop word set.</summary>
    public static bool IsStopWord(string word) => DefaultWords.Contains(word);

    /// <summary>
    /// Creates a frozen set combining the hardcoded defaults with additional stop words
    /// (typically loaded from an auxiliary database).
    /// </summary>
    public static FrozenSet<string> CreateMergedSet(IEnumerable<string>? additionalWords = null)
    {
        HashSet<string> merged = new(DefaultWords, StringComparer.Ordinal);

        if (additionalWords is not null)
        {
            foreach (string word in additionalWords)
            {
                merged.Add(word);
            }
        }

        return merged.ToFrozenSet(StringComparer.Ordinal);
    }
}
