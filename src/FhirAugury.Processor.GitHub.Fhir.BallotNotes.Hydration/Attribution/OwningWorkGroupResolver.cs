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
        string? workGroupHint,
        BallotNotesHydrationOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(options);

        IReadOnlyList<ResolvedSourceFile> sourceFiles = resolvedFiles ?? [];
        Dictionary<string, string> nameCache = new(StringComparer.OrdinalIgnoreCase);
        using SqliteConnection? db = TryOpenGitHubDb(options.GitHubDbPath, logger);

        if (string.Equals(unit.Type, "Page", StringComparison.OrdinalIgnoreCase))
        {
            return ResolvePage(unit, clonePath, owner, name, db, nameCache, logger);
        }

        if (string.Equals(unit.Type, "DataType", StringComparison.OrdinalIgnoreCase))
        {
            // Phase 4 finishes the datatype set; for now it shares the ticket path.
            return TicketOnly(attribution, workGroupHint);
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

    private static IReadOnlyList<WorkGroupRef> TicketOnly(UnitAttribution attribution, string? workGroupHint)
    {
        (string wg, string wgCode) = TicketAttributor.SelectOwningWorkGroup(attribution.Tickets, workGroupHint);
        return [new WorkGroupRef(wgCode, wg)];
    }

    /// <summary>Builds a ref, resolving the code's display name when a DB is open.</summary>
    private static WorkGroupRef MakeRef(SqliteConnection? db, string code, IDictionary<string, string> nameCache)
    {
        string display = db is null ? code : WorkGroupNameResolver.Resolve(db, code, nameCache);
        return new WorkGroupRef(code, display);
    }

    private static SqliteConnection? TryOpenGitHubDb(string? path, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString;

            SqliteConnection connection = new(connectionString);
            connection.Open();
            return connection;
        }
        catch (SqliteException ex)
        {
            logger?.LogDebug(ex, "Owning-WG resolver could not open github.db at {Path}", path);
            return null;
        }
    }
}
