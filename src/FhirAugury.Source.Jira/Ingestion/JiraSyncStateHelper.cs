using FhirAugury.Common.Api;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Helpers for encoding/decoding project-scoped sync state keys.
/// SubSource format: "{project}:{runType}" (e.g., "FHIR:full", "FHIR-I:incremental").
/// </summary>
public static class JiraSyncStateHelper
{
    private const string PreservedRunType = "preserved";

    /// <summary>Builds a SubSource key from project and run type.</summary>
    public static string SyncKey(string project, string runType)
        => $"{project}:{runType}";

    /// <summary>
    /// Parses a SubSource key into project and run type.
    /// Falls back to ("FHIR", subSource) for legacy rows without a colon.
    /// </summary>
    public static (string Project, string RunType) ParseSyncKey(string subSource)
    {
        int colon = subSource.IndexOf(':');
        return colon >= 0
            ? (subSource[..colon], subSource[(colon + 1)..])
            : ("FHIR", subSource);
    }

    /// <summary>
    /// Returns the watermark after an attempted run. Only an error-free full
    /// or incremental upstream run advances it.
    /// </summary>
    public static DateTimeOffset? ComputeNextLastSuccessfulSyncAt(
        DateTimeOffset? previous,
        string runType,
        IngestionResult? result)
    {
        bool isUpstreamRun =
            string.Equals(runType, "full", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(runType, "incremental", StringComparison.OrdinalIgnoreCase);
        bool completedWithoutErrors =
            result is not null &&
            result.ItemsFailed == 0 &&
            result.Errors.Count == 0;

        if (!isUpstreamRun || !completedWithoutErrors)
        {
            return previous;
        }

        DateTimeOffset completedAt = result!.CompletedAt;
        return previous is null || completedAt > previous
            ? completedAt
            : previous;
    }

    /// <summary>Reads the canonical successful watermark for one project.</summary>
    public static DateTimeOffset? GetLastSuccessfulSyncAt(
        SqliteConnection connection,
        string project)
    {
        IReadOnlyDictionary<string, DateTimeOffset?> watermarks =
            CaptureProjectWatermarks(connection);
        return watermarks.TryGetValue(project, out DateTimeOffset? watermark)
            ? watermark
            : null;
    }

    /// <summary>
    /// Captures every known Jira project and its latest successful upstream
    /// watermark. Projects with no proven successful refresh remain mapped to
    /// null.
    /// </summary>
    public static IReadOnlyDictionary<string, DateTimeOffset?> CaptureProjectWatermarks(
        SqliteConnection connection)
    {
        Dictionary<string, DateTimeOffset?> watermarks =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (JiraProjectRecord project in JiraProjectRecord.SelectList(connection))
        {
            watermarks.TryAdd(project.Key, null);
        }

        foreach (JiraSyncStateRecord state in JiraSyncStateRecord.SelectList(connection)
                     .Where(s => string.Equals(
                         s.SourceName,
                         JiraSource.SourceName,
                         StringComparison.OrdinalIgnoreCase)))
        {
            (string project, _) = ParseSyncKey(state.SubSource);
            if (!watermarks.TryGetValue(project, out DateTimeOffset? existing))
            {
                watermarks[project] = state.LastSuccessfulSyncAt;
            }
            else if (state.LastSuccessfulSyncAt is DateTimeOffset candidate &&
                     (existing is null || candidate > existing))
            {
                watermarks[project] = candidate;
            }
        }

        return watermarks;
    }

    /// <summary>
    /// Restores proven watermarks after a destructive local reset. No row is
    /// created for a null value, because doing so would manufacture a sync
    /// attempt where none was known.
    /// </summary>
    public static void RestoreProjectWatermarks(
        SqliteConnection connection,
        IReadOnlyDictionary<string, DateTimeOffset?> watermarks)
    {
        foreach ((string project, DateTimeOffset? watermark) in watermarks)
        {
            if (watermark is null)
            {
                continue;
            }

            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sync_state
                    (Id, SourceName, SubSource, LastSyncAt,
                     LastSuccessfulSyncAt, LastCursor, ItemsIngested,
                     SyncSchedule, NextScheduledAt, Status, LastError)
                VALUES
                    (@id, @sourceName, @subSource, @lastSyncAt,
                     @lastSuccessfulSyncAt, NULL, 0,
                     NULL, NULL, 'watermark_preserved', NULL)
                """;
            insert.Parameters.AddWithValue("@id", JiraSyncStateRecord.GetIndex());
            insert.Parameters.AddWithValue("@sourceName", JiraSource.SourceName);
            insert.Parameters.AddWithValue("@subSource", SyncKey(project, PreservedRunType));
            insert.Parameters.AddWithValue("@lastSyncAt", watermark.Value);
            insert.Parameters.AddWithValue("@lastSuccessfulSyncAt", watermark.Value);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Builds a provenance envelope from the caller's current SQLite
    /// transaction. Additional represented project keys are added with null
    /// watermarks when no sync state exists for them.
    /// </summary>
    public static SourceReadProvenance CaptureProvenance(
        SqliteConnection connection,
        IEnumerable<string>? representedProjects = null)
    {
        Dictionary<string, DateTimeOffset?> watermarks =
            new(CaptureProjectWatermarks(connection), StringComparer.OrdinalIgnoreCase);

        if (representedProjects is not null)
        {
            foreach (string project in representedProjects)
            {
                if (!string.IsNullOrWhiteSpace(project))
                {
                    watermarks.TryAdd(project, null);
                }
            }
        }

        JiraSourceStateRecord? sourceState = JiraSourceStateRecord.SelectSingle(
            connection,
            Id: JiraSourceStateRecord.SingletonId);

        return new SourceReadProvenance
        {
            Source = JiraSource.SourceName,
            ContentRevision = sourceState?.ContentRevision ?? 0,
            IsStable = sourceState is not null && !sourceState.MutationInProgress,
            ProjectLastSuccessfulRefreshAt = watermarks,
        };
    }
}
