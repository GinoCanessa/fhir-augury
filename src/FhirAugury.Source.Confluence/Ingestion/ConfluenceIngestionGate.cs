using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// The durable "stop issuing Confluence requests" switch. Set when an edge
/// challenge is detected, cleared only by a human.
/// </summary>
/// <remarks>
/// State lives in the Confluence source's own database rather than in
/// configuration or process memory, so an Aspire or container restart while the
/// WAF is still hostile does not evaporate the block and start hammering the
/// site again before re-learning to stop.
/// </remarks>
public class ConfluenceIngestionGate
{
    /// <summary>
    /// The single status literal that the pipeline, the controllers, and the
    /// tests all share.
    /// </summary>
    public const string BlockedStatus = "blocked_by_human_challenge";

    /// <summary>The one row's key; a second scope has never been needed.</summary>
    public const string IngestionScope = "ingestion";

    private readonly ConfluenceDatabase _database;
    private readonly ILogger<ConfluenceIngestionGate> _logger;
    private readonly Lock _writeLock = new();

    private ConfluenceIngestionBlockRecord? _current;

    public ConfluenceIngestionGate(ConfluenceDatabase database, ILogger<ConfluenceIngestionGate> logger)
    {
        _database = database;
        _logger = logger;

        // Read once at construction so a restart re-learns an active block
        // before the first scheduled pass can fire.
        _current = Read();

        if (_current is { Blocked: true })
        {
            _logger.LogWarning(
                "Confluence ingestion is blocked since {BlockedAt}: {Reason}",
                _current.BlockedAt, _current.Reason);
        }
    }

    /// <summary>True while a human still has to clear an edge challenge.</summary>
    public bool IsBlocked => _current is { Blocked: true };

    /// <summary>The current block row, blocked or cleared, or null if never blocked.</summary>
    public ConfluenceIngestionBlockRecord? Current => _current;

    /// <summary>
    /// Records the block. Idempotent: a second challenge while already blocked
    /// logs and changes nothing, so the original <c>BlockedAt</c> survives and
    /// stays the honest answer to "since when?".
    /// </summary>
    public void Block(ConfluenceHumanInterventionRequiredException exception)
    {
        lock (_writeLock)
        {
            using SqliteConnection connection = _database.OpenConnection();
            ConfluenceIngestionBlockRecord? existing = ReadFrom(connection);

            if (existing is { Blocked: true })
            {
                _logger.LogWarning(
                    "Confluence ingestion is already blocked since {BlockedAt}; leaving the original block intact",
                    existing.BlockedAt);
                _current = existing;
                return;
            }

            ConfluenceIngestionBlockRecord block = new()
            {
                Id = existing?.Id ?? ConfluenceIngestionBlockRecord.GetIndex(),
                Scope = IngestionScope,
                Blocked = true,
                BlockedAt = DateTimeOffset.UtcNow,
                Reason = exception.Message,
                HttpStatus = exception.StatusCode,
                ReasonPhrase = exception.ReasonPhrase,
                Fingerprint = exception.Fingerprint,
                RequestUrl = exception.RequestUrl,
                ClearedAt = null,
                ClearedBy = null,
            };

            Write(connection, block, existing is not null);
            _current = block;

            _logger.LogError(
                "Confluence ingestion blocked by an edge challenge ({Fingerprint}); no further requests will be issued until it is cleared",
                block.Fingerprint);
        }
    }

    /// <summary>
    /// Reopens the gate. Returns whether a block was actually standing, so the
    /// caller can tell an operator "nothing to clear" without it being an error.
    /// </summary>
    public bool Clear(string? clearedBy)
    {
        lock (_writeLock)
        {
            using SqliteConnection connection = _database.OpenConnection();
            ConfluenceIngestionBlockRecord? existing = ReadFrom(connection);

            if (existing is null)
            {
                return false;
            }

            bool wasBlocked = existing.Blocked;

            // Stamped rather than deleted: the row is the record that this
            // happened, and when someone decided it was over.
            existing.Blocked = false;
            existing.ClearedAt = DateTimeOffset.UtcNow;
            existing.ClearedBy = string.IsNullOrWhiteSpace(clearedBy) ? null : clearedBy;

            Write(connection, existing, exists: true);
            _current = existing;

            if (wasBlocked)
            {
                _logger.LogInformation(
                    "Confluence ingestion block cleared by {ClearedBy}", existing.ClearedBy ?? "an unnamed operator");
            }

            return wasBlocked;
        }
    }

    private ConfluenceIngestionBlockRecord? Read()
    {
        using SqliteConnection connection = _database.OpenConnection();
        return ReadFrom(connection);
    }

    private static ConfluenceIngestionBlockRecord? ReadFrom(SqliteConnection connection) =>
        ConfluenceIngestionBlockRecord.SelectSingle(connection, Scope: IngestionScope);

    private static void Write(
        SqliteConnection connection, ConfluenceIngestionBlockRecord record, bool exists)
    {
        if (exists)
        {
            ConfluenceIngestionBlockRecord.Update(connection, record);
        }
        else
        {
            ConfluenceIngestionBlockRecord.Insert(connection, record);
        }
    }
}
