using System.Collections.Frozen;

namespace FhirAugury.Common.Text;

/// <summary>
/// Shared token counting and classification logic for BM25 indexers.
/// Replaces the duplicated CountAndClassifyTokens methods in each source indexer.
/// </summary>
public static class TokenCounter
{
    /// <summary>
    /// Counts and classifies tokens for BM25 indexing, applying stop word filtering
    /// and optional lemmatization.
    /// </summary>
    /// <param name="tokens">Pre-tokenized list of lowercased tokens.</param>
    /// <param name="lemmatizer">Lemmatizer for normalizing inflected forms. Use <see cref="Lemmatizer.Empty"/> for no lemmatization.</param>
    /// <param name="stopWords">Stop word set. When null, uses the default hardcoded set.</param>
    /// <returns>Dictionary of keyword → (count, keyword type string).</returns>
    public static Dictionary<string, (int Count, string KeywordType)> CountAndClassifyTokens(
        List<string> tokens,
        Lemmatizer? lemmatizer = null,
        FrozenSet<string>? stopWords = null)
    {
        lemmatizer ??= Lemmatizer.Empty;
        Dictionary<string, (int Count, string KeywordType)> result = [];

        foreach (string token in tokens)
        {
            // Classify the original token
            KeywordType classification = KeywordClassifier.Classify(token);

            // Check stop words: use provided set if available, else fall back to default
            if (classification == KeywordType.StopWord)
            {
                continue;
            }

            if (stopWords is not null && stopWords.Contains(token))
            {
                continue;
            }

            // For regular words, apply lemmatization
            string indexToken = token;
            if (classification == KeywordType.Word)
            {
                indexToken = lemmatizer.Lemmatize(token);
            }

            if (result.TryGetValue(indexToken, out (int Count, string KeywordType) existing))
            {
                result[indexToken] = (existing.Count + 1, existing.KeywordType);
            }
            else
            {
                result[indexToken] = (1, KeywordClassifier.ToStorageString(classification));
            }
        }

        return result;
    }
}
