using Microsoft.Data.Sqlite;

namespace FhirAugury.Common.Tests;

/// <summary>
/// Guards the SQLite native binary the app ships with. We replaced the
/// vulnerable bundled <c>SQLitePCLRaw.lib.e_sqlite3</c> (SQLite &lt; 3.50.2,
/// GHSA-2m69-gcr7-jv3q) with <c>SourceGear.sqlite3</c> (SQLite 3.50.4) plus an
/// explicitly-registered provider. These tests fail fast if a future change
/// slides back to a bundled/older native lib or drops the provider registration.
/// </summary>
public class SqliteNativeVersionTests
{
    [Fact]
    public void Native_sqlite_version_is_at_least_3_50_2()
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        string version = (string)command.ExecuteScalar()!;

        Assert.True(
            IsAtLeast(version, 3, 50, 2),
            $"Expected patched SQLite >= 3.50.2 (CVE-2025-6965), but native lib reports {version}.");
    }

    private static bool IsAtLeast(string version, int major, int minor, int patch)
    {
        string[] parts = version.Split('.');
        int vMajor = int.Parse(parts[0]);
        int vMinor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
        int vPatch = parts.Length > 2 ? int.Parse(parts[2]) : 0;

        if (vMajor != major) return vMajor > major;
        if (vMinor != minor) return vMinor > minor;
        return vPatch >= patch;
    }
}
