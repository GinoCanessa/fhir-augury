using Microsoft.Data.Sqlite;

namespace FhirAugury.Common.Database;

/// <summary>
/// Small static helpers for additive in-place SQLite schema migrations.
/// </summary>
/// <remarks>
/// SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, so callers must inspect
/// <c>PRAGMA table_info</c> before issuing <c>ALTER TABLE ... ADD COLUMN</c>.
/// These helpers consolidate that pattern so source services can perform
/// nullable-column migrations from a single place. Indexes can be created
/// safely with the SQLite-native <c>CREATE INDEX IF NOT EXISTS</c>; this
/// type does not wrap that.
/// </remarks>
public static class SqliteSchemaHelpers
{
    /// <summary>
    /// Issues <c>ALTER TABLE {table} ADD COLUMN {column} {typeAndDefault}</c>
    /// only when the column is not already present on <paramref name="table"/>.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="table">Table name (must already exist).</param>
    /// <param name="column">Column name to add.</param>
    /// <param name="typeAndDefault">
    /// Type clause plus any default/NOT NULL fragment, e.g. <c>"TEXT"</c> or
    /// <c>"INTEGER NOT NULL DEFAULT 0"</c>.
    /// </param>
    /// <returns><c>true</c> when the column was added; <c>false</c> when it already existed.</returns>
    public static bool AddColumnIfMissing(SqliteConnection connection, string table, string column, string typeAndDefault)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeAndDefault);

        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using SqliteDataReader r = info.ExecuteReader();
            while (r.Read())
            {
                existing.Add(r.GetString(1));
            }
        }

        if (existing.Contains(column))
        {
            return false;
        }

        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAndDefault}";
        alter.ExecuteNonQuery();
        return true;
    }
}
