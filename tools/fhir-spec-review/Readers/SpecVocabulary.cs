using System.Collections.Frozen;

namespace FhirAugury.Tools.FhirSpecReview.Readers;

/// <summary>
/// Sanitized FHIR vocabulary for one source (the current build, or a published
/// baseline). All keys/values are already passed through
/// <see cref="SpecReview.KeywordSanitizer"/>. Only the dimensions the legacy
/// removed-artifact check actively matches are populated: structures (by
/// artifact class), element paths, and search-parameter names.
/// </summary>
internal sealed class SpecVocabulary
{
    public SpecVocabulary(
        FrozenDictionary<string, string> structures,
        FrozenSet<string> elementPaths,
        FrozenSet<string> searchParameterNames)
    {
        Structures = structures;
        ElementPaths = elementPaths;
        SearchParameterNames = searchParameterNames;
    }

    /// <summary>Sanitized structure name → artifact class (e.g. <c>patient</c> → <c>Resource</c>).</summary>
    public FrozenDictionary<string, string> Structures { get; }

    /// <summary>Sanitized element paths (e.g. <c>patientcontact</c>).</summary>
    public FrozenSet<string> ElementPaths { get; }

    /// <summary>Sanitized search-parameter names.</summary>
    public FrozenSet<string> SearchParameterNames { get; }

    public bool IsEmpty =>
        Structures.Count == 0 && ElementPaths.Count == 0 && SearchParameterNames.Count == 0;

    public static SpecVocabulary Empty { get; } = new(
        FrozenDictionary<string, string>.Empty,
        FrozenSet<string>.Empty,
        FrozenSet<string>.Empty);
}
