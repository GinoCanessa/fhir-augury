using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.WorkGroups;

/// <summary>
/// Builds <see cref="WorkGroupResolver"/> instances from an
/// <see cref="IHl7WorkGroupStore"/> snapshot. DI call sites typically
/// register this factory as a singleton and produce a fresh
/// <see cref="WorkGroupResolver"/> per request via
/// <see cref="Create(SqliteConnection, WorkGroupResolverOptions?, ILogger{WorkGroupResolver}?)"/>;
/// the snapshot is small (~50 rows) so per-request reconstruction keeps
/// semantics simple and prevents staleness across catalog reloads.
/// </summary>
public sealed class WorkGroupResolverFactory
{
    private readonly IHl7WorkGroupStore _store;

    public WorkGroupResolverFactory(IHl7WorkGroupStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Builds a resolver from the provided live <paramref name="connection"/>.
    /// Loads every row via <see cref="IHl7WorkGroupStore.LoadAll(SqliteConnection)"/>.
    /// </summary>
    public WorkGroupResolver Create(
        SqliteConnection connection,
        WorkGroupResolverOptions? options = null,
        ILogger<WorkGroupResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IReadOnlyList<Hl7WorkGroupDto> snapshot = _store.LoadAll(connection);
        return new WorkGroupResolver(snapshot, options, logger);
    }

    /// <summary>
    /// Test seam: builds a resolver from a caller-supplied snapshot, with
    /// no DB round-trip. Production code should prefer
    /// <see cref="Create(SqliteConnection, WorkGroupResolverOptions?, ILogger{WorkGroupResolver}?)"/>.
    /// </summary>
    public static WorkGroupResolver CreateFromSnapshot(
        IReadOnlyList<Hl7WorkGroupDto> snapshot,
        WorkGroupResolverOptions? options = null,
        ILogger<WorkGroupResolver>? logger = null) =>
        new WorkGroupResolver(snapshot, options, logger);
}
