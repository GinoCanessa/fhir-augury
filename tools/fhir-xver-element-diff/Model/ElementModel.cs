namespace FhirAugury.Tools.FhirXverElementDiff.Model;

/// <summary>A single declared type on an element (<c>TypeName</c> + optional <c>TypeProfile</c>).</summary>
internal readonly record struct ElementType(string Name, string? Profile);

/// <summary>
/// One snapshot element with the facets the six change flags are computed from.
/// Element identity for diffing is <see cref="NormalizedKey"/> (root-relative path
/// with the structure-name root stripped and choice <c>[x]</c> folded), not the raw
/// <see cref="Path"/>, so a renamed structure/backbone diffs correctly.
/// </summary>
internal sealed record ElementModel(
    string Path,
    string RootRelativePath,
    string NormalizedKey,
    string Name,
    string? SliceName,
    int Min,
    string MaxString,
    bool IsInherited,
    string? BasePath,
    string TypeLiteral,
    IReadOnlyList<ElementType> Types,
    IReadOnlyList<string> TargetProfiles)
{
    /// <summary>Cardinality rendered as <c>min..max</c> (e.g. <c>0..1</c>).</summary>
    public string Cardinality => $"{Min}..{MaxString}";

    /// <summary>
    /// Computes the normalized diff key for a raw element path: strips the first
    /// (structure-name) segment, then folds any trailing <c>[x]</c> choice marker off
    /// each remaining segment (<c>deceased[x]</c> → <c>deceased</c>). The root element
    /// (path == structure name) yields an empty key and is not diffed as a field.
    /// </summary>
    public static string ComputeNormalizedKey(string rootRelativePath)
    {
        if (rootRelativePath.Length == 0)
        {
            return string.Empty;
        }

        string[] segments = rootRelativePath.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            if (seg.EndsWith("[x]", StringComparison.Ordinal))
            {
                segments[i] = seg[..^3];
            }
        }
        return string.Join('.', segments);
    }

    /// <summary>Strips the first (structure-name) segment from a raw path; root → empty string.</summary>
    public static string ComputeRootRelativePath(string path)
    {
        int dot = path.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? string.Empty : path[(dot + 1)..];
    }
}
