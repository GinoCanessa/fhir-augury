using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;

/// <summary>
/// Shared low-level helpers used by the work-group resolvers
/// (<see cref="OwningWorkGroupResolver"/>, <see cref="WorkGroupLineageResolver"/>,
/// <see cref="AppliedWorkGroupResolver"/>): opening the read-only
/// <c>github.db</c> registry and turning a canonical code into a display-named
/// <see cref="WorkGroupRef"/>. Centralised so the sibling lineage resolvers do
/// not reach into <see cref="OwningWorkGroupResolver"/>'s private members.
/// </summary>
internal static class WorkGroupResolutionHelpers
{
    /// <summary>
    /// Builds a <see cref="WorkGroupRef"/> for <paramref name="code"/>, resolving
    /// the code's display name via <see cref="WorkGroupNameResolver"/> when a DB is
    /// open, otherwise falling back to the raw code.
    /// </summary>
    public static WorkGroupRef MakeRef(SqliteConnection? db, string code, IDictionary<string, string> nameCache)
    {
        string display = db is null ? code : WorkGroupNameResolver.Resolve(db, code, nameCache);
        return new WorkGroupRef(code, display);
    }

    /// <summary>
    /// Opens the registry <c>github.db</c> at <paramref name="path"/> read-only, or
    /// returns <c>null</c> when the path is missing/unset or cannot be opened.
    /// </summary>
    public static SqliteConnection? TryOpenGitHubDb(string? path, ILogger? logger)
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
            logger?.LogDebug(ex, "Work-group resolver could not open github.db at {Path}", path);
            return null;
        }
    }
}
