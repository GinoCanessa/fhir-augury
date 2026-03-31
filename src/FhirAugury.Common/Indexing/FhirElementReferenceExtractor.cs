using System.Text.RegularExpressions;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;

namespace FhirAugury.Common.Indexing;

/// <summary>
/// Extracts FHIR element path references (e.g., Patient.name, Observation.value[x])
/// from text. Uses <see cref="FhirVocabulary"/> to validate resource types and
/// avoid false positives from non-FHIR dotted paths.
/// Pure detection — text in, typed records out. No storage or DI dependencies.
/// </summary>
public static partial class FhirElementReferenceExtractor
{
    // Matches ResourceType.element paths, including nested paths and choice types [x]
    [GeneratedRegex(@"\b([A-Z][a-zA-Z]+\.[a-z][a-zA-Z]*(?:\[x\])?(?:\.[a-z][a-zA-Z]*(?:\[x\])?)*)(?![\w\[])")]
    private static partial Regex FhirElementRegex();

    public static List<FhirElementXRefRecord> GetReferences(
        string sourceType, string sourceId, params string[] texts)
    {
        List<FhirElementXRefRecord> results = [];
        HashSet<string> seen = [];

        foreach (string text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (Match match in FhirElementRegex().Matches(text))
            {
                string elementPath = match.Groups[1].Value;

                // Extract resource type (first segment before the dot)
                int dotIndex = elementPath.IndexOf('.');
                if (dotIndex < 0) continue;
                string resourceType = elementPath[..dotIndex];

                // Validate against known FHIR resource types
                if (!FhirVocabulary.IsResourceName(resourceType)) continue;

                if (!seen.Add(elementPath)) continue;

                results.Add(new FhirElementXRefRecord
                {
                    Id = FhirElementXRefRecord.GetIndex(),
                    SourceType = sourceType,
                    SourceId = sourceId,
                    LinkType = "mentions",
                    Context = CrossRefPatterns.GetSurroundingText(text, match.Index, 160),
                    ResourceType = resourceType,
                    ElementPath = elementPath,
                });
            }
        }
        return results;
    }
}
