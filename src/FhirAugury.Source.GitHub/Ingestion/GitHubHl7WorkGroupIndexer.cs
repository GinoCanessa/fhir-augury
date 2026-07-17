using FhirAugury.Source.GitHub.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SharedIndexer = FhirAugury.Common.WorkGroups.Hl7WorkGroupIndexer;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Thin GitHub-source wrapper around the shared
/// <see cref="FhirAugury.Common.WorkGroups.Hl7WorkGroupIndexer"/>. Opens a
/// <see cref="SqliteConnection"/> against the GitHub database and delegates
/// parsing + persistence to the shared indexer + <see cref="GitHubHl7WorkGroupStore"/>.
/// </summary>
public sealed class GitHubHl7WorkGroupIndexer
{
    private readonly GitHubDatabase _database;
    private readonly SharedIndexer _shared;

    public GitHubHl7WorkGroupIndexer(GitHubDatabase database, ILogger<GitHubHl7WorkGroupIndexer> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _shared = new SharedIndexer(new GitHubHl7WorkGroupStore(), logger);
    }

    /// <summary>
    /// Parses <paramref name="xmlPath"/> and upserts the contained work
    /// groups into the GitHub database. Returns the total row count after
    /// the call.
    /// </summary>
    public int Rebuild(string? xmlPath, CancellationToken ct = default)
    {
        using SqliteConnection conn = _database.OpenConnection();
        return _shared.Rebuild(xmlPath, conn, ct);
    }
}
