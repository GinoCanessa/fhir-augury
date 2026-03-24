namespace FhirAugury.Common.Text;

/// <summary>
/// Classification categories for BM25 indexing tokens.
/// </summary>
public enum KeywordType
{
    /// <summary>Regular word.</summary>
    Word,

    /// <summary>Common English stop word (filtered from index).</summary>
    StopWord,

    /// <summary>FHIR element path (e.g., Patient.name.given).</summary>
    FhirPath,

    /// <summary>FHIR operation (e.g., $validate).</summary>
    FhirOperation,
}

/// <summary>
/// Classifies tokens into categories for BM25 indexing.
/// </summary>
public static class KeywordClassifier
{
    /// <summary>Token is a regular word.</summary>
    [Obsolete("Use KeywordType.Word instead.")]
    public const string TypeWord = "word";

    /// <summary>Token is a common English stop word (filtered from index).</summary>
    [Obsolete("Use KeywordType.StopWord instead.")]
    public const string TypeStopWord = "stop_word";

    /// <summary>Token is a FHIR element path (e.g., Patient.name.given).</summary>
    [Obsolete("Use KeywordType.FhirPath instead.")]
    public const string TypeFhirPath = "fhir_path";

    /// <summary>Token is a FHIR operation (e.g., $validate).</summary>
    [Obsolete("Use KeywordType.FhirOperation instead.")]
    public const string TypeFhirOperation = "fhir_operation";

    /// <summary>
    /// Converts a <see cref="KeywordType"/> to its storage string representation.
    /// </summary>
    public static string ToStorageString(KeywordType type) => type switch
    {
        KeywordType.Word => "word",
        KeywordType.StopWord => "stop_word",
        KeywordType.FhirPath => "fhir_path",
        KeywordType.FhirOperation => "fhir_operation",
        _ => "word",
    };

    /// <summary>
    /// Classifies a single token.
    /// </summary>
    /// <param name="token">A lowercased token.</param>
    /// <returns>The keyword type classification.</returns>
    public static KeywordType Classify(string token)
    {
        if (token.StartsWith('$'))
        {
            return KeywordType.FhirOperation;
        }

        int dotIndex = token.IndexOf('.');
        if (dotIndex >= 0 && FhirVocabulary.IsResourceName(token[..dotIndex]))
        {
            return KeywordType.FhirPath;
        }

        if (FhirVocabulary.IsResourceName(token))
        {
            return KeywordType.FhirPath;
        }

        if (StopWords.IsStopWord(token))
        {
            return KeywordType.StopWord;
        }

        return KeywordType.Word;
    }
}
