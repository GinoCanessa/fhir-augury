using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Diff;

/// <summary>
/// Resolves structure renames over the Removed ∪ Added leftovers, in the request's
/// priority order:
/// <list type="number">
/// <item>An authoritative curated map (published change-logs) → <c>Confirmed</c>.</item>
/// <item>Ticket-confirmed pairs supplied by the caller → <c>Confirmed</c>.</item>
/// <item>A same-kind element-name Jaccard heuristic → only ever <c>Suspected</c>.</item>
/// </list>
/// A resolved rename is moved out of Removed/Added into Mapped so element diffing
/// compares the two structures root-relative (not as an all-remove/all-add split).
/// </summary>
internal sealed class StructureRenameDetector
{
    private readonly IReadOnlyDictionary<string, string> _curatedMap;
    private readonly IReadOnlySet<(string Old, string New)> _ticketConfirmed;
    private readonly double _heuristicThreshold;

    public StructureRenameDetector(
        IReadOnlyDictionary<string, string>? curatedMap = null,
        IReadOnlySet<(string Old, string New)>? ticketConfirmed = null,
        double heuristicThreshold = 0.5)
    {
        _curatedMap = curatedMap ?? CuratedRenameMap.Default;
        _ticketConfirmed = ticketConfirmed ?? new HashSet<(string, string)>();
        _heuristicThreshold = heuristicThreshold;
    }

    public void Apply(StructureBuckets buckets)
    {
        Dictionary<string, StructureModel> removedByName = new(StringComparer.Ordinal);
        foreach (StructureModel s in buckets.Removed)
        {
            removedByName[s.Name] = s;
        }
        Dictionary<string, StructureModel> addedByName = new(StringComparer.Ordinal);
        foreach (StructureModel s in buckets.Added)
        {
            addedByName[s.Name] = s;
        }

        HashSet<string> consumedRemoved = new(StringComparer.Ordinal);
        HashSet<string> consumedAdded = new(StringComparer.Ordinal);

        // (1) + (2) Confirmed renames: curated map and ticket-confirmed pairs.
        foreach ((string oldName, string newName) in EnumerateConfirmedPairs())
        {
            if (consumedRemoved.Contains(oldName) || consumedAdded.Contains(newName))
            {
                continue;
            }
            if (removedByName.TryGetValue(oldName, out StructureModel? oldStruct)
                && addedByName.TryGetValue(newName, out StructureModel? newStruct))
            {
                buckets.Mapped.Add(new StructurePair(oldStruct, newStruct, RenameKind.Confirmed));
                consumedRemoved.Add(oldName);
                consumedAdded.Add(newName);
            }
        }

        // (3) Heuristic: same-kind, highest element-name Jaccard >= threshold → Suspected.
        foreach (StructureModel removed in buckets.Removed)
        {
            if (consumedRemoved.Contains(removed.Name))
            {
                continue;
            }

            StructureModel? best = null;
            double bestScore = _heuristicThreshold;
            HashSet<string> removedKeys = FieldKeys(removed);
            foreach (StructureModel added in buckets.Added)
            {
                if (consumedAdded.Contains(added.Name) || added.Group != removed.Group)
                {
                    continue;
                }
                double score = Jaccard(removedKeys, FieldKeys(added));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = added;
                }
            }

            if (best is not null)
            {
                buckets.Mapped.Add(new StructurePair(removed, best, RenameKind.Suspected));
                consumedRemoved.Add(removed.Name);
                consumedAdded.Add(best.Name);
            }
        }

        buckets.Removed.RemoveAll(s => consumedRemoved.Contains(s.Name));
        buckets.Added.RemoveAll(s => consumedAdded.Contains(s.Name));
    }

    private IEnumerable<(string Old, string New)> EnumerateConfirmedPairs()
    {
        foreach (KeyValuePair<string, string> kvp in _curatedMap)
        {
            yield return (kvp.Key, kvp.Value);
        }
        foreach ((string Old, string New) pair in _ticketConfirmed)
        {
            yield return pair;
        }
    }

    /// <summary>The set of normalized (non-root) element keys used for heuristic matching.</summary>
    private static HashSet<string> FieldKeys(StructureModel structure)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (ElementModel e in structure.Elements)
        {
            if (e.NormalizedKey.Length > 0)
            {
                keys.Add(e.NormalizedKey);
            }
        }
        return keys;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 0.0;
        }
        int intersection = 0;
        foreach (string k in a)
        {
            if (b.Contains(k))
            {
                intersection++;
            }
        }
        int union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }
}

/// <summary>
/// Authoritative curated structure-rename map (old name → new name), seeded from the
/// published FHIR release change-logs. Confirmed renames — the heuristic never adds to
/// this. Ticket scanning may validate/extend it at runtime.
/// </summary>
internal static class CuratedRenameMap
{
    public static readonly IReadOnlyDictionary<string, string> Default =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // R4 → R4B (the "medicinal product definition" family)
            ["MedicinalProduct"] = "MedicinalProductDefinition",
            ["MedicinalProductPackaged"] = "PackagedProductDefinition",
            ["MedicinalProductPharmaceutical"] = "AdministrableProductDefinition",
            ["MedicinalProductManufactured"] = "ManufacturedItemDefinition",
            ["MedicinalProductAuthorization"] = "RegulatedAuthorization",
            ["MedicinalProductIngredient"] = "Ingredient",
            ["SubstanceSpecification"] = "SubstanceDefinition",
            // R4B → R5
            ["DeviceUseStatement"] = "DeviceUsage",
            ["RequestGroup"] = "RequestOrchestration",
        };
}
