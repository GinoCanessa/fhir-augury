namespace FhirAugury.Tools.FhirXverElementDiff.Model;

/// <summary>
/// A fully-loaded release: all in-scope structures plus the release-global
/// element-by-path index used to resolve each inherited element's base facets.
/// Owns the <see cref="IsPurelyInherited"/> test that drives the base-aware
/// inheritance filter (decision #4): an element is dropped from a diff only when it
/// is inherited <em>and</em> identical to its base element on every tracked facet;
/// locally-constrained inherited elements (e.g. <c>xhtml.id</c> <c>0..1</c>→<c>0..0</c>)
/// are kept.
/// </summary>
internal sealed class ReleaseModel
{
    private readonly Dictionary<string, StructureModel> _byName;
    private readonly Dictionary<string, ElementModel> _elementsByPath;

    public ReleaseModel(ResolvedRelease release, IReadOnlyList<StructureModel> structures)
    {
        Release = release;
        Structures = structures;

        _byName = new Dictionary<string, StructureModel>(StringComparer.Ordinal);
        foreach (StructureModel structure in structures)
        {
            _byName[structure.Name] = structure;
        }

        // Index every element by its raw Path for base-facet resolution. Base paths
        // (e.g. "Resource.id", "Element.id", "BackboneElement.modifierExtension") are
        // rooted at the unique base structure name, so raw Path is a stable key; skip
        // sliced rows so a slice never shadows the canonical element.
        _elementsByPath = new Dictionary<string, ElementModel>(StringComparer.Ordinal);
        foreach (StructureModel structure in structures)
        {
            foreach (ElementModel element in structure.Elements)
            {
                if (element.SliceName is not null)
                {
                    continue;
                }
                _elementsByPath.TryAdd(element.Path, element);
            }
        }
    }

    public ResolvedRelease Release { get; }

    public IReadOnlyList<StructureModel> Structures { get; }

    public bool TryGetStructure(string name, out StructureModel structure) =>
        _byName.TryGetValue(name, out structure!);

    /// <summary>
    /// True when <paramref name="element"/> is inherited and identical to its base
    /// element on every tracked facet (min, max, type set, target-profile set). Such
    /// elements are noise — they are the base element re-projected onto this structure
    /// — and are dropped from element diffs (analyzed once on their own base structure).
    /// Locally-constrained inherited elements return false and are kept. Elements whose
    /// base cannot be resolved conservatively return false (kept) so a real change is
    /// never silently dropped.
    /// </summary>
    public bool IsPurelyInherited(ElementModel element)
    {
        if (!element.IsInherited)
        {
            return false;
        }
        if (string.IsNullOrEmpty(element.BasePath))
        {
            return false;
        }
        if (!_elementsByPath.TryGetValue(element.BasePath, out ElementModel? baseElement))
        {
            return false;
        }
        if (ReferenceEquals(baseElement, element))
        {
            return false;
        }
        return FacetsEqual(element, baseElement);
    }

    /// <summary>
    /// Compares the four tracked facets of two elements: min cardinality, max string,
    /// the set of <c>(TypeName, TypeProfile)</c> tuples, and the set of target profiles.
    /// </summary>
    public static bool FacetsEqual(ElementModel a, ElementModel b) =>
        a.Min == b.Min
        && string.Equals(a.MaxString, b.MaxString, StringComparison.Ordinal)
        && TypesEqual(a.Types, b.Types)
        && SetsEqual(a.TargetProfiles, b.TargetProfiles);

    /// <summary>Order-insensitive set equality over declared type tuples.</summary>
    public static bool TypesEqual(IReadOnlyList<ElementType> a, IReadOnlyList<ElementType> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        HashSet<ElementType> set = [.. a];
        foreach (ElementType t in b)
        {
            if (!set.Contains(t))
            {
                return false;
            }
        }
        return set.Count == new HashSet<ElementType>(b).Count;
    }

    /// <summary>Order-insensitive set equality over string collections.</summary>
    public static bool SetsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        HashSet<string> set = new(a, StringComparer.Ordinal);
        foreach (string s in b)
        {
            if (!set.Contains(s))
            {
                return false;
            }
        }
        return set.Count == new HashSet<string>(b, StringComparer.Ordinal).Count;
    }
}
