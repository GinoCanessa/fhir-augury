using System.Collections.Concurrent;
using FhirAugury.Common.WorkGroups;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Free-text → canonical HL7 work-group <c>code</c> resolver, backed by the
/// local <c>hl7_workgroups</c> table. Held as a singleton; the snapshot is
/// reloaded by the GitHub ingestion pipeline after every refresh of the
/// underlying table.
/// </summary>
/// <remarks>
/// Resolution order (case-insensitive):
/// <list type="number">
///   <item>Exact match against <c>Code</c>.</item>
///   <item>Exact match against <c>Name</c>.</item>
///   <item>Match against <c>NameClean</c> after running the input through
///         <see cref="Hl7WorkGroupNameCleaner.Clean"/>.</item>
/// </list>
/// Retired rows still resolve — callers that care about retirement should
/// look up the row separately. Returns <c>null</c> for unmatched input;
/// each unique unmatched input is logged at <c>Debug</c> exactly once.
/// </remarks>
public sealed class WorkGroupResolver
{
    private readonly GitHubDatabase _database;
    private readonly ILogger<WorkGroupResolver> _logger;
    private readonly ConcurrentDictionary<string, byte> _unmatchedLogged = new(StringComparer.OrdinalIgnoreCase);

    private volatile Snapshot _snapshot = Snapshot.Empty;

    public WorkGroupResolver(GitHubDatabase database, ILogger<WorkGroupResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _logger = logger;
    }

    /// <summary>Number of rows in the cached snapshot.</summary>
    public int Count => _snapshot.ByCode.Count;

    /// <summary>
    /// Atomically reload the cached snapshot from <c>hl7_workgroups</c>.
    /// Cheap; safe to call after every WG refresh.
    /// </summary>
    public void Reload()
    {
        using SqliteConnection conn = _database.OpenConnection();
        Reload(conn);
    }

    /// <summary>Reload using a caller-provided connection (test seam).</summary>
    public void Reload(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<Hl7WorkGroupRecord> rows = Hl7WorkGroupRecord.SelectList(connection);

        Dictionary<string, string> byCode = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> byClean = new(StringComparer.OrdinalIgnoreCase);

        foreach (Hl7WorkGroupRecord r in rows)
        {
            byCode[r.Code] = r.Code;
            byName[r.Name] = r.Code;
            if (!string.IsNullOrEmpty(r.NameClean))
                byClean[r.NameClean] = r.Code;
        }

        _snapshot = new Snapshot(byCode, byName, byClean);
        _unmatchedLogged.Clear();
    }

    /// <summary>
    /// Resolves <paramref name="freeText"/> to a canonical HL7 work-group
    /// <c>code</c>, or <c>null</c> when no match is found.
    /// </summary>
    public string? Resolve(string? freeText)
    {
        if (string.IsNullOrWhiteSpace(freeText)) return null;
        Snapshot snap = _snapshot;
        string trimmed = freeText.Trim();

        if (snap.ByCode.TryGetValue(trimmed, out string? code)) return code;
        if (snap.ByName.TryGetValue(trimmed, out code)) return code;

        string clean = Hl7WorkGroupNameCleaner.Clean(trimmed);
        if (!string.IsNullOrEmpty(clean) && snap.ByClean.TryGetValue(clean, out code))
            return code;

        if (_unmatchedLogged.TryAdd(trimmed, 0))
            _logger.LogDebug("workgroup resolver: unmatched input {Input}", trimmed);

        return null;
    }

    private sealed record Snapshot(
        IReadOnlyDictionary<string, string> ByCode,
        IReadOnlyDictionary<string, string> ByName,
        IReadOnlyDictionary<string, string> ByClean)
    {
        public static readonly Snapshot Empty = new(
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
    }
}
