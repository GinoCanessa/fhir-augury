using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;

/// <summary>
/// The two definition-sourced work-group lineages for a unit, kept deliberately
/// separate (not collapsed by priority like <see cref="OwningWorkGroupResolver"/>):
/// <list type="bullet">
///   <item><see cref="Listed"/> — the WG declared on the artifact/page itself,
///   read from the repo clone (SD <c>structuredefinition-wg</c> for artifacts; the
///   page <c>[%wg%]</c> marker for pages). Never borrows the index.</item>
///   <item><see cref="Index"/> — the WG from the JIRA spec-artifact/page
///   registry.</item>
/// </list>
/// Each is a single-entry list for artifacts/pages and a per-covered-datatype set
/// for the datatypes surface. <c>(unknown)</c> sentinel when a source resolves
/// nothing (except datatype <see cref="Listed"/>, which falls back to FHIR
/// Infrastructure per covered datatype).
/// </summary>
public readonly record struct WorkGroupLineages(
    IReadOnlyList<WorkGroupRef> Listed,
    IReadOnlyList<WorkGroupRef> Index);

/// <summary>
/// Computes the <em>Listed</em> (repo-read) and <em>JIRA index</em> (registry)
/// work-group lineages for a hydration unit, reusing the same single-source
/// readers as <see cref="OwningWorkGroupResolver"/> but <em>without</em> the
/// priority chain that collapses them into one primary owner. The hydrator owns
/// the <c>github.db</c> connection + name cache and passes them in.
/// </summary>
public static class WorkGroupLineageResolver
{
    /// <summary>The canonical FHIR Infrastructure code datatype Listed falls back to.</summary>
    private const string FhirInfrastructureCode = "fhir";

    /// <summary>
    /// Resolves the Listed + Index lineages for <paramref name="unit"/> per its
    /// type. Pages use the marker for Listed; artifacts use their SD; the datatypes
    /// surface resolves per covered datatype (Listed falling back to FHIR
    /// Infrastructure where a datatype declares no WG).
    /// </summary>
    public static WorkGroupLineages Resolve(
        HydrationUnit unit,
        string clonePath,
        string owner,
        string name,
        IReadOnlyList<ResolvedSourceFile> resolvedFiles,
        IReadOnlyList<string> headDatatypeNames,
        SqliteConnection? db,
        IDictionary<string, string> nameCache,
        BallotNotesHydrationOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(nameCache);
        ArgumentNullException.ThrowIfNull(options);

        IReadOnlyList<ResolvedSourceFile> sourceFiles = resolvedFiles ?? [];
        IReadOnlyList<string> headDatatypes = headDatatypeNames ?? [];

        if (string.Equals(unit.Type, "Page", StringComparison.OrdinalIgnoreCase))
        {
            return ResolvePage(unit, clonePath, owner, name, db, nameCache, logger);
        }

        if (string.Equals(unit.Type, "DataType", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDataType(unit, clonePath, owner, name, sourceFiles, headDatatypes, db, nameCache, logger);
        }

        return ResolveArtifact(unit, clonePath, owner, name, sourceFiles, db, nameCache, logger);
    }

    private static WorkGroupLineages ResolveArtifact(
        HydrationUnit unit,
        string clonePath,
        string owner,
        string name,
        IReadOnlyList<ResolvedSourceFile> resolvedFiles,
        SqliteConnection? db,
        IDictionary<string, string> nameCache,
        ILogger? logger)
    {
        string? listedCode = RepoWorkGroupReader.ReadArtifactWg(clonePath, resolvedFiles, logger);
        string? indexCode = db is null
            ? null
            : SpecArtifactWorkGroupResolver.Resolve(db, owner, name, "Artifact", unit.Name, logger);

        return new WorkGroupLineages(
            SingleOrUnknown(listedCode, db, nameCache),
            SingleOrUnknown(indexCode, db, nameCache));
    }

    private static WorkGroupLineages ResolvePage(
        HydrationUnit unit,
        string clonePath,
        string owner,
        string name,
        SqliteConnection? db,
        IDictionary<string, string> nameCache,
        ILogger? logger)
    {
        string? listedCode = RepoWorkGroupReader.ReadPageMarker(clonePath, unit.Name);
        string? indexCode = db is null
            ? null
            : SpecArtifactWorkGroupResolver.Resolve(db, owner, name, "Page", unit.Name, logger);

        return new WorkGroupLineages(
            SingleOrUnknown(listedCode, db, nameCache),
            SingleOrUnknown(indexCode, db, nameCache));
    }

    private static WorkGroupLineages ResolveDataType(
        HydrationUnit unit,
        string clonePath,
        string owner,
        string name,
        IReadOnlyList<ResolvedSourceFile> resolvedFiles,
        IReadOnlyList<string> headDatatypeNames,
        SqliteConnection? db,
        IDictionary<string, string> nameCache,
        ILogger? logger)
    {
        IReadOnlyList<string> datatypeNames = DatatypeNameExtractor.Extract(
            unit.ChangedPaths, () => headDatatypeNames);

        List<WorkGroupRef> listed = [];
        List<WorkGroupRef> index = [];
        HashSet<string> listedSeen = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> indexSeen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string datatype in datatypeNames)
        {
            // Listed: per-datatype repo-read, restricted to the datatype's own SD
            // files so one datatype's WG cannot bleed onto another. Bare
            // source/datatypes/<n>.xml files are not SD-named, so this is usually
            // empty — in which case the datatype defaults to FHIR Infrastructure
            // (a datatype-only relaxation; artifacts/pages stay (unknown)).
            string? listedCode = RepoWorkGroupReader.ReadArtifactWg(
                clonePath, FilterFilesForDatatype(resolvedFiles, datatype), logger);
            string effectiveListed = string.IsNullOrWhiteSpace(listedCode) ? FhirInfrastructureCode : listedCode;
            if (listedSeen.Add(effectiveListed))
            {
                listed.Add(WorkGroupResolutionHelpers.MakeRef(db, effectiveListed, nameCache));
            }

            // Index: per-datatype registry lookup (datatypes are artifacts in the
            // registry). Skipped silently when unresolved/ambiguous.
            if (db is not null)
            {
                string? indexCode = SpecArtifactWorkGroupResolver.Resolve(db, owner, name, "Artifact", datatype, logger);
                if (!string.IsNullOrWhiteSpace(indexCode) && indexSeen.Add(indexCode))
                {
                    index.Add(WorkGroupResolutionHelpers.MakeRef(db, indexCode, nameCache));
                }
            }
        }

        return new WorkGroupLineages(
            listed.Count == 0 ? [WorkGroupRef.Unknown] : OrderWithPrimary(listed),
            index.Count == 0 ? [WorkGroupRef.Unknown] : OrderWithPrimary(index));
    }

    /// <summary>
    /// Restricts <paramref name="resolvedFiles"/> to StructureDefinition files
    /// whose name references <paramref name="datatype"/>, so a per-datatype
    /// repo-read does not pick up an unrelated datatype's WG.
    /// </summary>
    private static IReadOnlyList<ResolvedSourceFile> FilterFilesForDatatype(
        IReadOnlyList<ResolvedSourceFile> resolvedFiles, string datatype)
    {
        if (resolvedFiles.Count == 0 || string.IsNullOrWhiteSpace(datatype)) return [];

        List<ResolvedSourceFile> matched = [];
        foreach (ResolvedSourceFile file in resolvedFiles)
        {
            string fileName = Path.GetFileNameWithoutExtension(file.Path);
            if (fileName.Contains(datatype, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(file);
            }
        }
        return matched;
    }

    private static IReadOnlyList<WorkGroupRef> SingleOrUnknown(
        string? code, SqliteConnection? db, IDictionary<string, string> nameCache)
        => string.IsNullOrWhiteSpace(code)
            ? [WorkGroupRef.Unknown]
            : [WorkGroupResolutionHelpers.MakeRef(db, code, nameCache)];

    /// <summary>
    /// Orders a set deterministically: FHIR Infrastructure (<c>fhir</c>) first when
    /// present, otherwise alphabetical by display name — mirroring
    /// <see cref="OwningWorkGroupResolver"/>'s primary ordering.
    /// </summary>
    private static IReadOnlyList<WorkGroupRef> OrderWithPrimary(List<WorkGroupRef> refs)
    {
        List<WorkGroupRef> ordered = [.. refs.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)];
        int fhirIndex = ordered.FindIndex(r => string.Equals(r.Code, FhirInfrastructureCode, StringComparison.OrdinalIgnoreCase));
        if (fhirIndex > 0)
        {
            WorkGroupRef fhir = ordered[fhirIndex];
            ordered.RemoveAt(fhirIndex);
            ordered.Insert(0, fhir);
        }
        return ordered;
    }
}
