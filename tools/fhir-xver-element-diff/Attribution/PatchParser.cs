using System.Text.RegularExpressions;

namespace FhirAugury.Tools.FhirXverElementDiff.Attribution;

/// <summary>The kind of element facet a diff hunk altered (only the cleanly-parseable ones).</summary>
internal enum ElementFacet
{
    /// <summary>An element's own <c>&lt;path&gt;</c> line was added or removed (a field add/remove).</summary>
    Structural,

    /// <summary>An element-level <c>&lt;min&gt;</c>/<c>&lt;max&gt;</c> line changed.</summary>
    Cardinality,
}

/// <summary>
/// One facet change a single commit made to a specific element path. <see cref="NewMin"/>/
/// <see cref="NewMax"/> carry the <c>+</c>-side (post-change) cardinality value when present,
/// for the R6 ballot4 snapshot gate.
/// </summary>
internal readonly record struct ElementTouch(string Path, ElementFacet Facet, string? NewMin, string? NewMax);

/// <summary>
/// Parses a single StructureDefinition-XML unified diff into per-element facet touches, so a
/// commit that cleanly isolates one element can be attributed to that element's row rather
/// than to the whole structure (Phase 6 hybrid precision). An element-level
/// <c>&lt;min&gt;</c>/<c>&lt;max&gt;</c> change is tied to its element via the nearest preceding
/// <c>&lt;path value="…"/&gt;</c> context line — the diff is fetched with extra context so that
/// line is in-hunk — and the enclosing element is forgotten at every hunk/file boundary so a
/// change whose path is out of context is conservatively dropped (falls back to the
/// structure-window record) rather than mis-attributed. The <c>&lt;base&gt;</c> and
/// <c>&lt;slicing&gt;</c> sub-blocks carry their own path/min/max, so they are suppressed. The
/// pre-migration spreadsheet form matches none of these patterns and yields no touches.
/// </summary>
internal static partial class PatchParser
{
    [GeneratedRegex("<path value=\"([^\"]+)\"")]
    private static partial Regex PathValue();

    [GeneratedRegex("<min value=\"([^\"]+)\"")]
    private static partial Regex MinValue();

    [GeneratedRegex("<max value=\"([^\"]+)\"")]
    private static partial Regex MaxValue();

    public static IReadOnlyList<ElementTouch> Parse(string patch)
    {
        if (string.IsNullOrEmpty(patch))
        {
            return [];
        }

        List<ElementTouch> touches = [];
        string? enclosing = null;
        // Depth of open <base>/<slicing> blocks: their nested path/min/max are not the
        // element's own facets, so any line at depth > 0 is ignored.
        int suppressDepth = 0;

        foreach (string line in patch.Split('\n'))
        {
            if (line.StartsWith("diff --git", StringComparison.Ordinal)
                || line.StartsWith("@@", StringComparison.Ordinal))
            {
                // Hunk/file boundary: the enclosing element is no longer known (discontiguous).
                enclosing = null;
                suppressDepth = 0;
                continue;
            }
            if (line.Length == 0
                || line.StartsWith("+++", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            char prefix = line[0];
            bool changed = prefix is '+' or '-';
            if (!changed && prefix != ' ')
            {
                continue; // hunk metadata such as "\ No newline at end of file"
            }
            string content = line[1..];

            bool opensSuppressed = content.Contains("<base>", StringComparison.Ordinal)
                || content.Contains("<slicing", StringComparison.Ordinal);
            bool closesSuppressed = content.Contains("</base>", StringComparison.Ordinal)
                || content.Contains("</slicing>", StringComparison.Ordinal);
            if (opensSuppressed)
            {
                suppressDepth++;
            }
            bool suppressed = suppressDepth > 0;

            Match pathMatch = PathValue().Match(content);
            if (pathMatch.Success)
            {
                if (!suppressed)
                {
                    enclosing = pathMatch.Groups[1].Value;
                    if (changed)
                    {
                        touches.Add(new ElementTouch(enclosing, ElementFacet.Structural, null, null));
                    }
                }
            }
            else if (changed && !suppressed && enclosing is not null)
            {
                string? newMin = null;
                string? newMax = null;
                bool cardinality = false;

                Match minMatch = MinValue().Match(content);
                if (minMatch.Success)
                {
                    cardinality = true;
                    if (prefix == '+')
                    {
                        newMin = minMatch.Groups[1].Value;
                    }
                }

                Match maxMatch = MaxValue().Match(content);
                if (maxMatch.Success)
                {
                    cardinality = true;
                    if (prefix == '+')
                    {
                        newMax = maxMatch.Groups[1].Value;
                    }
                }

                if (cardinality)
                {
                    touches.Add(new ElementTouch(enclosing, ElementFacet.Cardinality, newMin, newMax));
                }
            }

            if (closesSuppressed && suppressDepth > 0)
            {
                suppressDepth--;
            }
        }

        return touches;
    }
}
