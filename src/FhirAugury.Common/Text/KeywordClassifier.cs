namespace FhirAugury.Common.Text;

/// <summary>
/// Classifies tokens into categories for BM25 indexing.
/// </summary>
public static class KeywordClassifier
{
    /// <summary>Token is a regular word.</summary>
    public const string TypeWord = "word";

    /// <summary>Token is a common English stop word (filtered from index).</summary>
    public const string TypeStopWord = "stop_word";

    /// <summary>Token is a FHIR element path (e.g., Patient.name.given).</summary>
    public const string TypeFhirPath = "fhir_path";

    /// <summary>Token is a FHIR operation (e.g., $validate).</summary>
    public const string TypeFhirOperation = "fhir_operation";

    /// <summary>
    /// Classifies a single token.
    /// </summary>
    /// <param name="token">A lowercased token.</param>
    /// <returns>The keyword type classification.</returns>
    public static string Classify(string token)
    {
        if (token.StartsWith('$'))
        {
            return TypeFhirOperation;
        }

        if (token.Contains('.') && FhirVocabulary.IsResourceName(token.Split('.')[0]))
        {
            return TypeFhirPath;
        }

        if (FhirVocabulary.IsResourceName(token))
        {
            return TypeFhirPath;
        }

        if (StopWords.IsStopWord(token))
        {
            return TypeStopWord;
        }

        return TypeWord;
    }
}
