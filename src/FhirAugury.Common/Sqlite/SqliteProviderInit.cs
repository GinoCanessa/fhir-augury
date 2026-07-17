namespace FhirAugury.Sqlite;

/// <summary>
/// Registers the SourceGear <c>e_sqlite3</c> provider for SQLitePCLRaw.
/// FhirAugury apps/tests register it automatically at module load; external
/// consumers of FhirAugury libraries can call <see cref="Init"/> explicitly.
/// </summary>
public static class SqliteProviderInit
{
    public static void Init()
        => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
}
