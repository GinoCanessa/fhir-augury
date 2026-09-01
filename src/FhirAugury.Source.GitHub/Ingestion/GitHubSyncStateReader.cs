using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Reads the GitHub <c>sync_state</c> table, constrained to operational
/// sub-sources. The history-backfill feature writes namespaced
/// <c>backfill:&lt;repo&gt;</c> marker rows; those must never satisfy
/// "last sync" reads (doing so would corrupt the incremental window or surface
/// a marker as the service's reported status). Operational reads therefore
/// select the most-recent row whose <see cref="GitHubSyncStateRecord.SubSource"/>
/// is one of <see cref="OperationalSubSources"/>.
/// </summary>
public static class GitHubSyncStateReader
{
    /// <summary>Sub-sources written by real ingestion runs (never backfill markers).</summary>
    public static readonly string[] OperationalSubSources = ["incremental", "full", "rebuild"];

    /// <summary>
    /// Returns the most-recent operational sync-state row (by
    /// <see cref="GitHubSyncStateRecord.LastSyncAt"/>), ignoring any
    /// <c>backfill:&lt;repo&gt;</c> marker rows. Null when no operational run has
    /// completed yet.
    /// </summary>
    public static GitHubSyncStateRecord? GetMostRecentOperational(SqliteConnection connection)
    {
        return GitHubSyncStateRecord
            .SelectList(connection, SourceName: IGitHubDataProvider.SourceName)
            .Where(r => OperationalSubSources.Contains(r.SubSource))
            .OrderByDescending(r => r.LastSyncAt)
            .FirstOrDefault();
    }
}
