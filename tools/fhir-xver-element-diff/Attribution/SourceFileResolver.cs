using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Attribution;

/// <summary>
/// Resolves a structure's <c>HL7/fhir</c> source file(s) from the union of the
/// <c>source/</c> trees at both window endpoints (<c>since</c> and <c>until</c>) — both are
/// needed because a removed/renamed structure exists only at <c>since</c>, and a newly
/// added one only at <c>until</c>. Candidate names are matched case-insensitively against
/// the real tree so exact casing (e.g. <c>structuredefinition-MedicationRequest.xml</c>) is
/// returned. Both the post-migration (<c>structuredefinition-&lt;Name&gt;.xml</c>) and the
/// pre-migration (<c>&lt;name&gt;-spreadsheet.xml</c>) forms are tried, because frozen
/// resources (e.g. Media) keep the spreadsheet form well past the 2021-01-14 migration.
/// </summary>
internal sealed class SourceFileResolver
{
    private readonly HashSet<string> _files;

    public SourceFileResolver(IEnumerable<string> sinceFiles, IEnumerable<string> untilFiles)
    {
        _files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in sinceFiles)
        {
            _files.Add(file);
        }
        foreach (string file in untilFiles)
        {
            _files.Add(file);
        }
    }

    /// <summary>True when at least one endpoint tree was loaded.</summary>
    public bool Any => _files.Count > 0;

    /// <summary>
    /// The source file paths (repo-relative, real casing) that define
    /// <paramref name="structure"/> at either endpoint, including its old name when it is a
    /// rename. Empty when nothing resolves (base/special types resist resolution — blank
    /// attribution is acceptable there).
    /// </summary>
    public IReadOnlyList<string> Resolve(StructureModel structure, string? oldName)
    {
        List<string> candidates = [];
        AddCandidates(candidates, structure.Group, structure.Name);
        if (!string.IsNullOrEmpty(oldName)
            && !string.Equals(oldName, structure.Name, StringComparison.OrdinalIgnoreCase))
        {
            AddCandidates(candidates, structure.Group, oldName);
        }

        List<string> resolved = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in candidates)
        {
            if (_files.TryGetValue(candidate, out string? actual) && seen.Add(actual))
            {
                resolved.Add(actual);
            }
        }
        return resolved;
    }

    private static void AddCandidates(List<string> into, StructureGroup group, string name)
    {
        string lower = name.ToLowerInvariant();
        switch (group)
        {
            case StructureGroup.PrimitiveType:
                // Every primitive shares one source file.
                into.Add("source/datatypes/primitives.xml");
                break;
            case StructureGroup.ComplexType:
                into.Add($"source/datatypes/{lower}.xml");
                into.Add($"source/datatypes/{lower}-spreadsheet.xml");
                break;
            case StructureGroup.Resource:
                // The SD filename carries the PascalCase name; the folder is lowercase.
                into.Add($"source/{lower}/structuredefinition-{name}.xml");
                into.Add($"source/{lower}/{lower}-spreadsheet.xml");
                break;
        }
    }
}
