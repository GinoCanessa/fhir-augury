using System.Runtime.CompilerServices;

namespace FhirAugury.Sqlite;

internal static class SqliteProviderModuleInitializer
{
    [ModuleInitializer]
    internal static void Init()
        => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
}
