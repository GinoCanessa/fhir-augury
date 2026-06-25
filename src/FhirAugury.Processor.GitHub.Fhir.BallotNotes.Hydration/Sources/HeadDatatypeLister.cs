using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

/// <summary>
/// Lists the datatype names defined at HEAD of a clone — the top-level
/// <c>source/datatypes/&lt;name&gt;.xml</c> stems, excluding nested paths and
/// <c>-</c>-bearing variant/code-system/value-set/spreadsheet files. Shared by the
/// hydrator (resolving owners for an aggregate-only datatypes change) and the
/// owning-WG re-stamp tool, so <c>DataType</c> re-stamps match fresh hydration
/// exactly. Best-effort: a non-git directory yields an empty list.
/// </summary>
public static class HeadDatatypeLister
{
    private const string DatatypesPrefix = "source/datatypes/";

    /// <summary>Lists HEAD datatype names from <paramref name="clonePath"/>.</summary>
    public static async Task<IReadOnlyList<string>> ListAsync(string clonePath, CancellationToken ct = default)
    {
        GitRunner.GitResult result = await GitRunner.TryRunAsync(
            clonePath, ["ls-tree", "-r", "--name-only", "HEAD", "--", DatatypesPrefix], ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return [];

        List<string> names = [];
        foreach (string line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string path = line.Trim();
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.StartsWith(DatatypesPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            string remainder = path[DatatypesPrefix.Length..];
            if (remainder.Contains('/')) continue;
            string stem = remainder[..^".xml".Length];
            if (stem.Length == 0 || stem.Contains('-')) continue;
            names.Add(stem);
        }
        return names;
    }
}
