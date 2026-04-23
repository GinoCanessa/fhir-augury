using FhirAugury.Source.Jira.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SharedIndexer = FhirAugury.Common.WorkGroups.Hl7WorkGroupIndexer;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Thin Jira-source wrapper around the shared
/// <see cref="FhirAugury.Common.WorkGroups.Hl7WorkGroupIndexer"/>. Opens a
/// <see cref="SqliteConnection"/> against the Jira database and delegates
/// parsing + persistence to the shared indexer + <see cref="JiraHl7WorkGroupStore"/>.
/// </summary>
public sealed class Hl7WorkGroupIndexer
{
    private readonly JiraDatabase _database;
    private readonly SharedIndexer _shared;

    public Hl7WorkGroupIndexer(JiraDatabase database, ILogger<Hl7WorkGroupIndexer> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _shared = new SharedIndexer(new JiraHl7WorkGroupStore(), logger);
    }

    /// <summary>
    /// Parses <paramref name="xmlPath"/> and upserts the contained work
    /// groups into the Jira database. Returns the total row count after the
    /// call. Behavior matches the original Jira-internal implementation.
    /// </summary>
    public int Rebuild(string? xmlPath, CancellationToken ct = default)
    {
        using SqliteConnection conn = _database.OpenConnection();
        return _shared.Rebuild(xmlPath, conn, ct);
    }
}
