using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;

/// <summary>
/// Resolves the work group(s) that <em>own</em> a hydration unit, independent of
/// which work group raised the attributed tickets. The owner is determined by a
/// deterministic chain that varies by unit type:
/// <list type="bullet">
///   <item><c>Artifact</c>: registry → repo-read → spec-DB → base-resource →
///   most-recent ticket → <c>(unknown)</c>.</item>
///   <item><c>Page</c>: registry → page marker → <c>(unknown)</c> (never ticket).</item>
///   <item><c>DataType</c>: the distinct set of owners of the covered datatypes
///   (each via the artifact own-WG chain; never ticket).</item>
/// </list>
/// The repo-read, spec-DB, base-resource, and datatype-set sources are layered in
/// later phases; this seam already routes per type and resolves canonical codes
/// to display names via <see cref="WorkGroupNameResolver"/>. The primary owner is
/// always the first entry of the returned list.
/// </summary>
public static class OwningWorkGroupResolver
{
    /// <summary>
    /// Resolves the owning work group set for <paramref name="unit"/>. Artifacts
    /// fall through to the legacy ticket-recency owner; pages and datatypes never
    /// consult tickets.
    /// </summary>
    public static IReadOnlyList<WorkGroupRef> Resolve(
        HydrationUnit unit,
        string clonePath,
        string owner,
        string name,
        UnitAttribution attribution,
        IReadOnlyList<ResolvedSourceFile> resolvedFiles,
        IReadOnlyList<string> headDatatypeNames,
        string? workGroupHint,
        BallotNotesHydrationOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(options);

        IReadOnlyList<ResolvedSourceFile> sourceFiles = resolvedFiles ?? [];
        IReadOnlyList<string> headDatatypes = headDatatypeNames ?? [];
        Dictionary<string, string> nameCache = new(StringComparer.OrdinalIgnoreCase);
        using SqliteConnection? db = TryOpenGitHubDb(options.GitHubDbPath, logger);

        if (string.Equals(unit.Type, "Page", StringComparison.OrdinalIgnoreCase))
        {
            return ResolvePage(unit, clonePath, owner, name, db, nameCache, logger);
        }

        if (string.Equals(unit.Type, "DataType", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDataType(unit, clonePath, owner, name, headDatatypes, db, nameCache, options, logger);
        }

        return ResolveArtifact(unit, clonePath, owner, name, attribution, sourceFiles, workGroupHint, db, nameCache, options, logger);
    }

    private static IReadOnlyList<WorkGroupRef> ResolveArtifact(
        HydrationUnit unit,
        string clonePath,
        string owner,
        string name,
        UnitAttribution attribution,
        IReadOnlyList<ResolvedSourceFile> resolvedFiles,
        string? workGroupHint,
        SqliteConnection? db,
        IDictionary<string, string> nameCache,
        BallotNotesHydrationOptions options,
        ILogger? logger)
    {
        // Own-WG steps for the unit itself: registry → repo-read → spec-DB.
        string? code = ResolveArtifactOwnCode(owner, name, unit.Name, clonePath, resolvedFiles, db, options, logger);
        if (!string.IsNullOrWhiteSpace(code))
        {
            return [MakeRef(db, code, nameCache)];
        }

        // Base-resource (profiles / extensions): inherit the base's owning WG via
        // the same own-WG steps (by base name; the base has no local SD files here).
        string? baseName = RepoWorkGroupReader.ReadBaseResourceName(clonePath, resolvedFiles, logger);
        if (!string.IsNullOrWhiteSpace(baseName)
            && !string.Equals(baseName, unit.Name, StringComparison.OrdinalIgnoreCase))
        {
            string? baseCode = ResolveArtifactOwnCode(owner, name, baseName, clonePath, [], db, options, logger);
            if (!string.IsNullOrWhiteSpace(baseCode))
            {
                return [MakeRef(db, baseCode, nameCache)];
            }
        }

        // Late fallback: most-recently-attributed ticket's work group.
        (string wg, string wgCode) = TicketAttributor.SelectOwningWorkGroup(attribution.Tickets, workGroupHint);
        if (!string.IsNullOrWhiteSpace(wg))
        {
            return [new WorkGroupRef(wgCode, wg)];
        }

        return [WorkGroupRef.Unknown];
    }

    /// <summary>
    /// The artifact own-WG chain (registry → repo-read → spec-DB) for a single
    /// artifact name, returning a canonical code or <c>null</c>.
    /// </summary>
    private static string? ResolveArtifactOwnCode(
        string owner,
        string name,
        string artifactName,
        string clonePath,
        IReadOnlyList<ResolvedSourceFile> resolvedFiles,
        SqliteConnection? db,
        BallotNotesHydrationOptions options,
        ILogger? logger)
    {
        if (db is not null)
        {
            string? registryCode = SpecArtifactWorkGroupResolver.Resolve(db, owner, name, "Artifact", artifactName, logger);
            if (!string.IsNullOrWhiteSpace(registryCode)) return registryCode;
        }

        string? repoCode = RepoWorkGroupReader.ReadArtifactWg(clonePath, resolvedFiles, logger);
        if (!string.IsNullOrWhiteSpace(repoCode)) return repoCode;

        return SpecDbWorkGroupReader.Resolve(options.FhirR6DbPath, options.FhirSpecDbPath, artifactName, logger);
    }

    private static IReadOnlyList<WorkGroupRef> ResolvePage(
        HydrationUnit unit,
        string clonePath,
        string owner,
        string name,
        SqliteConnection? db,
        IDictionary<string, string> nameCache,
        ILogger? logger)
    {
        // 1. Registry (jira_spec_pages) — primary.
        if (db is not null)
        {
            string? code = SpecArtifactWorkGroupResolver.Resolve(db, owner, name, "Page", unit.Name, logger);
            if (!string.IsNullOrWhiteSpace(code))
            {
                return [MakeRef(db, code, nameCache)];
            }
        }

        // 2. Page "Responsible Owner" marker read from the clone.
        string? markerCode = RepoWorkGroupReader.ReadPageMarker(clonePath, unit.Name);
        if (!string.IsNullOrWhiteSpace(markerCode))
        {
            return [MakeRef(db, markerCode, nameCache)];
        }

        // 3. (unknown) — pages never fall back to a ticket work group.
        return [WorkGroupRef.Unknown];
    }

    private static IReadOnlyList<WorkGroupRef> ResolveDataType(
        HydrationUnit unit,
        string clonePath,
        string owner,
        string name,
        IReadOnlyList<string> headDatatypeNames,
        SqliteConnection? db,
        IDictionary<string, string> nameCache,
        BallotNotesHydrationOptions options,
        ILogger? logger)
    {
        IReadOnlyList<string> datatypeNames = DatatypeNameExtractor.Extract(
            unit.ChangedPaths, () => headDatatypeNames);

        List<WorkGroupRef> refs = [];
        HashSet<string> seenCodes = new(StringComparer.OrdinalIgnoreCase);

        foreach (string datatype in datatypeNames)
        {
            // Per-datatype own-WG chain. No repo-read files are passed: each
            // datatype's bare SD file (source/datatypes/<name>.xml) is not matched
            // by the structuredefinition-name filter, and passing the whole unit's
            // files would let one datatype's WG bleed onto another. Registry +
            // spec-DB carry datatype ownership. Datatypes never consult tickets.
            string? code = ResolveArtifactOwnCode(owner, name, datatype, clonePath, [], db, options, logger);
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (seenCodes.Add(code)) refs.Add(MakeRef(db, code, nameCache));
        }

        return refs.Count == 0 ? [WorkGroupRef.Unknown] : OrderWithPrimary(refs);
    }

    /// <summary>
    /// Orders the owner set deterministically: FHIR Infrastructure (<c>fhir</c>)
    /// first when present, otherwise alphabetical by display name. The first entry
    /// becomes the note's primary <c>WorkGroup</c> / <c>WorkGroupCode</c>.
    /// </summary>
    private static IReadOnlyList<WorkGroupRef> OrderWithPrimary(List<WorkGroupRef> refs)
    {
        List<WorkGroupRef> ordered = [.. refs.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)];
        int fhirIndex = ordered.FindIndex(r => string.Equals(r.Code, "fhir", StringComparison.OrdinalIgnoreCase));
        if (fhirIndex > 0)
        {
            WorkGroupRef fhir = ordered[fhirIndex];
            ordered.RemoveAt(fhirIndex);
            ordered.Insert(0, fhir);
        }
        return ordered;
    }

    /// <summary>Builds a ref, resolving the code's display name when a DB is open.</summary>
    private static WorkGroupRef MakeRef(SqliteConnection? db, string code, IDictionary<string, string> nameCache)
        => WorkGroupResolutionHelpers.MakeRef(db, code, nameCache);

    private static SqliteConnection? TryOpenGitHubDb(string? path, ILogger? logger)
        => WorkGroupResolutionHelpers.TryOpenGitHubDb(path, logger);
}
